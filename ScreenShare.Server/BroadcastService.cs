using System.Diagnostics;
using ScreenShare.Core.Capture;
using ScreenShare.Core.Networking;
using ScreenShare.Core.Streaming;

namespace ScreenShare.Server;

/// <summary>
/// Учёт подключившихся клиентов: ограничение числа одновременных
/// подключений и статистика по каждому из них (п. 3.2).
///
/// Счётчики обновляются из потоков отдачи кадров, поэтому используются
/// атомарные операции.
/// </summary>
public sealed class ClientRegistry
{
    private readonly Dictionary<long, ClientSession> _clients = new();
    private readonly object _gate = new();
    private long _nextId;

    public ClientRegistry(int maxClients) => MaxClients = Math.Max(1, maxClients);

    /// <summary>Предел одновременных подключений.</summary>
    public int MaxClients { get; set; }

    public int Count
    {
        get { lock (_gate) return _clients.Count; }
    }

    public IReadOnlyList<ClientSession> All
    {
        get { lock (_gate) return _clients.Values.ToArray(); }
    }

    /// <summary>
    /// Регистрирует клиента либо возвращает false, если достигнут предел.
    /// </summary>
    public bool TryRegister(string remoteAddress, string transport, string? name, out ClientSession? session)
    {
        lock (_gate)
        {
            if (_clients.Count >= MaxClients)
            {
                session = null;
                return false;
            }

            // Свой порядковый номер нужен для автоимени, когда клиент
            // своё имя не задал.
            var created = new ClientSession(
                Interlocked.Increment(ref _nextId),
                remoteAddress,
                transport,
                DateTimeOffset.Now,
                _clients.Count + 1);

            created.SetName(name);

            _clients[created.Id] = created;
            session = created;

            return true;
        }
    }

    public void Unregister(ClientSession session)
    {
        lock (_gate)
            _clients.Remove(session.Id);
    }
}

/// <summary>Состояние одного клиента.</summary>
public sealed class ClientSession(
    long id,
    string remoteAddress,
    string transport,
    DateTimeOffset connectedAt,
    int ordinal)
{
    private long _framesSent;
    private long _bytesSent;
    private long _framesDropped;
    private long _lastFrameAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private double _measuredFps;
    private string _name = string.Empty;

    public long Id { get; } = id;
    public string RemoteAddress { get; } = remoteAddress;
    public string Transport { get; } = transport;
    public DateTimeOffset ConnectedAt { get; } = connectedAt;

    /// <summary>
    /// Имя, заданное клиентом, либо автоимяна по адресу.
    /// Показывается администратору, поэтому нормализуется на входе.
    /// </summary>
    public string Name { get { lock (this) return _name; } }

    /// <summary>Присваивает имя клиента, откатываясь к автоимени при пустом значении.</summary>
    public void SetName(string? name)
    {
        string sanitized = ViewerNames.Sanitize(name);
        lock (this)
            _name = sanitized.Length > 0
                ? sanitized
                : ViewerNames.Fallback(RemoteAddress, ordinal);
    }

    public long FramesSent => Interlocked.Read(ref _framesSent);
    public long BytesSent => Interlocked.Read(ref _bytesSent);
    public long FramesDropped => Interlocked.Read(ref _framesDropped);

    /// <summary>Фактический темп получения кадров клиентом, кадров в секунду.</summary>
    public double MeasuredFps
    {
        get { lock (this) return _measuredFps; }
    }

    public void RecordFrame(int bytes, int intervalMs)
    {
        Interlocked.Increment(ref _framesSent);
        Interlocked.Add(ref _bytesSent, bytes);

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (this)
        {
            long delta = now - Interlocked.Read(ref _lastFrameAt);

            if (delta > 200)
            {
                // Экспоненциальное сглаживание: мгновенный темп сильно скачет
                // из-за одиночных задержек в сети.
                double instant = 1000.0 / Math.Max(1, intervalMs);
                _measuredFps = _measuredFps <= 0 ? instant : (_measuredFps * 0.7) + (instant * 0.3);
                Interlocked.Exchange(ref _lastFrameAt, now);
            }
        }
    }

    public void RecordDrop() => Interlocked.Increment(ref _framesDropped);

    public ClientInfo ToInfo() => new()
    {
        Id = Id,
        Name = Name,
        RemoteAddress = RemoteAddress,
        Transport = Transport,
        ConnectedAt = ConnectedAt,
        FramesSent = FramesSent,
        BytesSent = BytesSent,
        FramesDropped = FramesDropped,
        MeasuredFps = Math.Round(MeasuredFps, 1),
    };
}

public sealed record ClientInfo
{
    public required long Id { get; init; }

    /// <summary>Имя клиента: заданное им самим либо автоимяна по адресу.</summary>
    public required string Name { get; init; }

    public required string RemoteAddress { get; init; }
    public required string Transport { get; init; }
    public required DateTimeOffset ConnectedAt { get; init; }
    public long FramesSent { get; init; }
    public long BytesSent { get; init; }
    public long FramesDropped { get; init; }
    public double MeasuredFps { get; init; }
}

/// <summary>
/// Трансляция: собирает захват, клиентов и сетевые параметры в один объект,
/// которым пользуются и HTTP-обработчики, и консольный вывод.
/// </summary>
public sealed class BroadcastService : IAsyncDisposable
{
    private readonly FrameHub _hub = new();
    private CancellationTokenSource? _stopping;
    private readonly object _lifecycleGate = new();
    private readonly ClientRegistry _clients;

    private ScreenCapturer _capturer;
    private Task? _captureTask;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public BroadcastService(ServerOptions options)
    {
        Options = options;
        _clients = new ClientRegistry(options.MaxClients);
        _capturer = new ScreenCapturer(_hub, BuildCaptureOptions(options));
    }

    public ServerOptions Options { get; private set; }

    public FrameHub Hub => _hub;

    public ClientRegistry Clients => _clients;

    public ScreenCapturer Capturer => _capturer;

    public bool IsRunning => _captureTask is { IsCompleted: false };

    /// <summary>Адрес, по которому трансляция доступна другим устройствам.</summary>
    public string AccessAddress => $"http://{ListenAddress}:{Options.Port}";

    public string ListenAddress =>
        Options.BindAllInterfaces ? NetworkInfo.GetPreferredAddress() : "127.0.0.1";

    public static CaptureOptions BuildCaptureOptions(ServerOptions options) => new()
    {
        MonitorDeviceName = options.Monitor,
        Fps = options.Fps,
        Quality = options.Quality,
        MaxWidth = options.MaxWidth,
        MaxHeight = options.MaxHeight,
        ShowCursor = options.ShowCursor,
    };

    /// <summary>Запускает захват экрана (п. 3.1 № 7).</summary>
    public void Start()
    {
        lock (_lifecycleGate)
        {
            if (IsRunning)
                return;

            // Источник отмены создаётся заново при каждом запуске.
            // Если переиспользовать уже отменённый, цикл захвата завершится
            // сразу же, и кнопка «Запустить» перестанет что-либо делать.
            _stopping?.Dispose();
            _stopping = new CancellationTokenSource();

            CancellationToken token = _stopping.Token;

            // Захватчик создаётся один раз и просто перенастраивается:
            // иначе после перезапуска счётчики кадров обнулялись бы,
            // и в панели статистика выглядела бы как «кадры пропали».
            _capturer.Reconfigure(BuildCaptureOptions(Options));

            _captureTask = Task.Run(() => _capturer.RunAsync(token));
        }
    }

    /// <summary>Останавливает захват (п. 3.1 № 7).</summary>
    public async Task StopAsync()
    {
        Task? task;
        CancellationTokenSource? stopping;

        lock (_lifecycleGate)
        {
            task = _captureTask;
            stopping = _stopping;

            _captureTask = null;
            _stopping = null;
        }

        if (task is null)
            return;

        if (stopping is not null)
            await stopping.CancelAsync().ConfigureAwait(false);

        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // Ошибка цикла захвата не должна мешать завершению работы сервера.
        }

        stopping?.Dispose();
    }

    /// <summary>Применяет новые параметры на лету без остановки трансляции.</summary>
    public void ApplyOptions(ServerOptions options)
    {
        Options = options;
        _clients.MaxClients = options.MaxClients;
        _capturer.Reconfigure(BuildCaptureOptions(options));
    }

    /// <summary>Сводка для панели и API (п. 3.1 № 8, п. 3.2 «статистика»).</summary>
    public BroadcastStatus GetStatus()
    {
        CaptureStatistics capture = _capturer.Snapshot();

        return new BroadcastStatus
        {
            Running = IsRunning,
            AccessAddress = AccessAddress,
            ListenAddress = ListenAddress,
            Port = Options.Port,
            Monitors = ScreenCapturer.GetMonitors(),
            Interfaces = NetworkInfo.DescribeInterfaces(),
            Capture = capture,
            Clients = _clients.All.Select(c => c.ToInfo()).ToArray(),
            ClientCount = _clients.Count,
            MaxClients = _clients.MaxClients,
            TotalBytesSent = _clients.All.Sum(c => c.BytesSent),
            Uptime = _clock.Elapsed,
        };
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}

public sealed record BroadcastStatus
{
    public required bool Running { get; init; }
    public required string AccessAddress { get; init; }
    public required string ListenAddress { get; init; }
    public required int Port { get; init; }
    public required IReadOnlyList<MonitorInfo> Monitors { get; init; }
    public required IReadOnlyList<InterfaceAddress> Interfaces { get; init; }
    public required CaptureStatistics Capture { get; init; }
    public required IReadOnlyList<ClientInfo> Clients { get; init; }
    public required int ClientCount { get; init; }
    public required int MaxClients { get; init; }
    public long TotalBytesSent { get; init; }
    public required TimeSpan Uptime { get; init; }
}