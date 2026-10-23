using System.Diagnostics;
using System.Threading.Channels;

namespace ScreenShare.Core.Streaming;

/// <summary>
/// Точка встречи захвата и клиентов: один поток снимает экран, многие получают
/// кадры.
///
/// Ключевое решение — хранится только **последний** кадр, а у каждого клиента
/// своя очередь глубиной 1 с политикой DropOldest. Для трансляции живого видео
/// накопление кадров бессмысленно: если клиент не успевает, ему полезнее
/// пропустить устаревшие кадры и показать свежий, чем копить задержку. Иначе
/// при кратковременном сбое сети картинка «уезжала» бы на секунды назад.
/// </summary>
public sealed class FrameHub
{
    private readonly List<ChannelWriter<Frame>> _subscribers = [];

    private Frame? _latest;
    private long _sequence;
    private long _totalFrames;
    private long _totalBytes;
    private long _droppedByClients;

    /// <summary>Самый свежий кадр (или null, если ещё ни одного).</summary>
    public Frame? Latest => Volatile.Read(ref _latest);

    public long TotalFrames => Interlocked.Read(ref _totalFrames);
    public long TotalBytes => Interlocked.Read(ref _totalBytes);

    /// <summary>Сколько кадров отброшено из-за медленных клиентов.</summary>
    public long DroppedByClients => Interlocked.Read(ref _droppedByClients);

    /// <summary>
    /// Запоминает кадр как текущий и рассылает его всем подписчикам.
    /// Вызывается из потока захвата.
    /// </summary>
    public Frame Publish(Frame frame)
    {
        Volatile.Write(ref _latest, frame);
        Interlocked.Increment(ref _totalFrames);
        Interlocked.Add(ref _totalBytes, frame.Jpeg.Length);

        ChannelWriter<Frame>[] subscribers;

        lock (_subscribers)
            subscribers = [.. _subscribers];

        foreach (ChannelWriter<Frame> writer in subscribers)
        {
            // TryWrite не блокирует: если клиент не успел забрать кадр,
            // следующий просто вытеснит предыдущий из очереди глубиной 1.
            // Блокировка здесь означала бы остановку захвата из-за
            // медленного клиента.
            if (!writer.TryWrite(frame))
                Interlocked.Increment(ref _droppedByClients);
        }

        return frame;
    }

    public long NextSequence() => Interlocked.Increment(ref _sequence);

    /// <summary>
    /// Подписка клиента на кадры. Очередь глубиной 1: пока клиент не забрал
    /// предыдущий кадр, следующий вытесняет его.
    /// </summary>
    public ChannelReader<Frame> Subscribe(out Action unsubscribe)
    {
        var channel = Channel.CreateBounded<Frame>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        lock (_subscribers)
            _subscribers.Add(channel.Writer);

        unsubscribe = () =>
        {
            lock (_subscribers)
                _subscribers.Remove(channel.Writer);

            channel.Writer.TryComplete();
        };

        return channel.Reader;
    }

    public int SubscriberCount
    {
        get { lock (_subscribers) return _subscribers.Count; }
    }

    /// <summary>Счётчик пропущенных клиентом кадров (для статистики).</summary>
    public void ReportClientDrop() => Interlocked.Increment(ref _droppedByClients);

    /// <summary>Снимок статистики потока.</summary>
    public HubStatistics Snapshot(TimeSpan uptime)
    {
        long frames = TotalFrames;
        long bytes = TotalBytes;

        return new HubStatistics
        {
            CapturedAt = DateTimeOffset.Now,
            Uptime = uptime,
            TotalFrames = frames,
            TotalBytes = bytes,
            AverageFps = uptime.TotalSeconds > 0.5 ? frames / uptime.TotalSeconds : 0,
            AverageFrameBytes = frames > 0 ? (long)(bytes / (double)frames) : 0,
            DroppedByClients = DroppedByClients,
        };
    }
}

public sealed record HubStatistics
{
    public required DateTimeOffset CapturedAt { get; init; }
    public required TimeSpan Uptime { get; init; }
    public long TotalFrames { get; init; }
    public long TotalBytes { get; init; }
    public double AverageFps { get; init; }
    public long AverageFrameBytes { get; init; }
    public long DroppedByClients { get; init; }
}