using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using ScreenShare.Core.Encoding;
using ScreenShare.Core.Streaming;

namespace ScreenShare.Core.Capture;

/// <summary>
/// Захват экрана в JPEG-кадры (п. 3.1 № 1 и № 2).
///
/// Работает в отдельном потоке с циклом по таймеру. Ресурсы GDI+ используются
/// экономно: растр переиспользуется между кадрами, пока не меняется
/// разрешение или режим масштабирования. При потоке кадров создание нового
/// Bitmap на каждом кадре привело бы к росту потребления GDI-ресурсов и
/// утечкам в длительной трансляции.
/// </summary>
public sealed class ScreenCapturer
{
    private readonly FrameHub _hub;
    private readonly object _bitmapGate = new();

    private Bitmap? _surface;
    private Bitmap? _scaled;
    private PixelFormat _surfaceFormat;

    private CaptureOptions _options;
    private Rectangle _bounds;
    private string _deviceName = string.Empty;

    private long _sequence;
    private long _frames;
    private long _bytes;
    private long _errors;
    private int _lastFrameWidth;
    private int _lastFrameHeight;
    private double _lastFrameMs;
    private DateTimeOffset _startedAt;
    private readonly Stopwatch _uptimeClock = new();
    private bool _surfaceDirty;
    private long _accumulatedTicks;

    /// <summary>
    /// Скользящее среднее фактической частоты кадров (кадров в секунду).
    /// В отличие от кумулятивной AverageFps, оно отражает текущий режим,
    /// а не всё время работы подряд.
    /// </summary>
    private double _recentFps;

    /// <summary>Скользящее среднее размера кадра в байтах.</summary>
    private double _recentFrameBytes;

    public ScreenCapturer(FrameHub hub, CaptureOptions options)
    {
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _options = options.Normalize();
    }

    /// <summary>Активные параметры захвата.</summary>
    public CaptureOptions Options => Volatile.Read(ref _options);

    /// <summary>Ошибки захвата, накопленные с момента запуска.</summary>
    public long ErrorCount => Interlocked.Read(ref _errors);

    /// <summary>Уже захваченные кадры и байты.</summary>
    public long CapturedFrames => Interlocked.Read(ref _frames);

    public long CapturedBytes => Interlocked.Read(ref _bytes);

    /// <summary>Средняя длительность захвата кадра, мс.</summary>
    public double AverageFrameMs => Interlocked.Read(ref _frames) == 0
        ? 0
        : _lastFrameMs;

    /// <summary>
    /// Меняет параметры на лету. Смена монитора или масштаба требует пересоздать
    /// растр, поэтому он сбрасывается на следующем кадре.
    /// </summary>
    public void Reconfigure(CaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        CaptureOptions normalized = options.Normalize();
        Volatile.Write(ref _options, normalized);

        if (!string.Equals(normalized.MonitorDeviceName, _deviceName, StringComparison.OrdinalIgnoreCase))
            InvalidateSurface();
    }

    /// <summary>
    /// Помечает растр как устаревший: он будет пересоздан при следующем
    /// захвате.
    ///
    /// Освобождать Bitmap здесь нельзя: метод вызывается из потока HTTP
    /// (смена настроек), тогда как в растр рисует поток захвата, и
    /// освобождение привело бы к попытке рисовать в освобождённый объект.
    /// Единственным владельцем растра остаётся цикл захвата.
    /// </summary>
    public void InvalidateSurface() => Volatile.Write(ref _surfaceDirty, true);

    /// <summary>Доступные мониторы (п. 3.2 «выбор монитора»).</summary>
    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();

        foreach (Screen screen in Screen.AllScreens)
        {
            Rectangle bounds = screen.Bounds;
            monitors.Add(new MonitorInfo(
                screen.DeviceName, bounds.X, bounds.Y, bounds.Width, bounds.Height, screen.Primary));
        }

        // Основной монитор — первым: это значение по умолчанию.
        return monitors
            .OrderByDescending(m => m.IsPrimary)
            .ToList();
    }

    /// <summary>
    /// Основной цикл захвата. Завершается при отмене токена.
    /// </summary>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        _startedAt = DateTimeOffset.Now;

        // Отдельные часы для общего времени работы: их нельзя перезапускать
        // в цикле, иначе «время работы» измеряло бы длительность одного кадра,
        // и средний FPS всегда выходил бы равным нулю.
        _uptimeClock.Restart();

        long iterationStart = Stopwatch.GetTimestamp();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                long frameStart = Stopwatch.GetTimestamp();
                int frameBytes = 0;

                try
                {
                    Frame? frame = CaptureOnce();

                    if (frame is not null)
                    {
                        _hub.Publish(frame);
                        Interlocked.Increment(ref _frames);
                        Interlocked.Add(ref _bytes, frame.Jpeg.Length);
                        frameBytes = frame.Jpeg.Length;
                    }
                }
                catch (Exception)
                {
                    // Ошибка отдельного кадра не должна рвать трансляцию:
                    // следующий кадр может отрисоваться нормально.
                    Interlocked.Increment(ref _errors);
                    InvalidateSurface();
                }

                double elapsedMs = Stopwatch.GetElapsedTime(frameStart).TotalMilliseconds;
                _lastFrameMs = elapsedMs;

                // Интервал читается на КАЖДОЙ итерации, а не один раз до цикла.
                // Если прочитать его перед циклом, смена FPS обновит лишь
                // переменную в настройках: трансляция продолжила бы идти
                // с прежней частотой, а «30» в панели оказалось бы враньём.
                double delay = Options.FrameIntervalMs - elapsedMs;

                if (delay > 0)
                    await Task.Delay(TimeSpan.FromMilliseconds(delay), stoppingToken)
                        .ConfigureAwait(false);

                // Скользящие значения: сколько фактически уходит кадров и
                // сколько каждый весит сейчас. Кумулятивное среднее меняется
                // медленно и долго хранит в себе прошлые настройки — из-за
                // этого смена FPS и качества на экране не видно.
                SmoothRate(Stopwatch.GetElapsedTime(iterationStart).TotalMilliseconds, frameBytes);
                iterationStart = Stopwatch.GetTimestamp();
            }
        }
        catch (OperationCanceledException)
        {
            // Нормальная остановка.
        }
        finally
        {
            _uptimeClock.Stop();

            // Время накапливается между перезапусками: счётчик кадров
            // живёт дольше одного запуска, и без этого средний FPS
            // после перезапуска выходил бы завышенным.
            Interlocked.Add(ref _accumulatedTicks, _uptimeClock.ElapsedTicks);
        }
    }

    /// <summary>
    /// Сглаживает фактическую частоту и размер кадра.
    ///
    /// Известное среднее дробится: кадр с задержкой в сети или резкий
    /// переход экрана утянули бы значение в край и оно мигало бы.
    /// Экспоненциальное сглаживание убирает это, оставляя реакцию на
    /// смену настроек в пределах нескольких кадров.
    /// </summary>
    private void SmoothRate(double iterationMs, int frameBytes)
    {
        if (iterationMs <= 0)
            return;

        double instantFps = 1000.0 / iterationMs;
        double previous;

        previous = Volatile.Read(ref _recentFps);
        Volatile.Write(ref _recentFps, previous <= 0 ? instantFps : (previous * 0.7) + (instantFps * 0.3));

        if (frameBytes <= 0)
            return;

        previous = Volatile.Read(ref _recentFrameBytes);
        Volatile.Write(ref _recentFrameBytes, previous <= 0 ? frameBytes : (previous * 0.7) + (frameBytes * 0.3));
    }

    /// <summary>
    /// Захватывает и кодирует один кадр. Возвращает null, если экран недоступен.
    /// </summary>
    public Frame? CaptureOnce()
    {
        CaptureOptions options = Options;

        Screen? screen = ResolveScreen(options.MonitorDeviceName);

        if (screen is null || !TryGetBounds(screen, out Rectangle bounds))
            return null;

        // Границы запоминаются для отчёта о состоянии: без этого снимок
        // показывал бы нулевое разрешение.
        lock (_bitmapGate)
        {
            _bounds = bounds;
            _deviceName = screen.DeviceName;
        }

        Bitmap? surface = EnsureSurface(bounds.Size, options);

        if (surface is null)
            return null;

        using var graphics = Graphics.FromImage(surface);

        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);

        int? cursorX = null;
        int? cursorY = null;

        if (options.ShowCursor && CursorOverlay.TryGetPosition(out int cx, out int cy))
        {
            // Курсор нужно рисовать в координатах кадра, а не экрана.
            int px = cx - bounds.X;
            int py = cy - bounds.Y;

            // Рисуем только если курсор попадает в кадр: иначе он «залипал» бы
            // в углу изображения.
            if (px >= 0 && py >= 0 && px < bounds.Width && py < bounds.Height)
            {
                if (CursorOverlay.TryDraw(graphics, px, py))
                {
                    cursorX = px;
                    cursorY = py;
                }
            }
        }

        Bitmap target = surface;
        bool ownsTarget = false;

        if (options.MaxWidth > 0 || options.MaxHeight > 0)
        {
            Bitmap scaled = JpegEncoder.Scale(surface, options.MaxWidth, options.MaxHeight);
            target = scaled;
            ownsTarget = !ReferenceEquals(scaled, surface);
        }

        try
        {
            int rawBytes = bounds.Width * bounds.Height * 3;
            byte[] jpeg = JpegEncoder.Encode(target, options.Quality);

            Volatile.Write(ref _lastFrameWidth, target.Width);
            Volatile.Write(ref _lastFrameHeight, target.Height);

            return new Frame
            {
                Jpeg = jpeg,
                Sequence = Interlocked.Increment(ref _sequence),
                TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Width = target.Width,
                Height = target.Height,
                RawBytes = rawBytes,
                CursorX = cursorX,
                CursorY = cursorY,
            };
        }
        finally
        {
            if (ownsTarget)
                target.Dispose();
        }
    }

    /// <summary>Статистика захвата для панели и API.</summary>
    public CaptureStatistics Snapshot()
    {
        long frames = CapturedFrames;
        long bytes = CapturedBytes;

        // Итоговое время захвата = накопленное между запусками + текущий отрезок.
        TimeSpan uptime = TimeSpan.FromTicks(
            Interlocked.Read(ref _accumulatedTicks) + _uptimeClock.ElapsedTicks);

        return new CaptureStatistics
        {
            Running = _startedAt != default && !ReferenceEquals(null, _hub.Latest),
            StartedAt = _startedAt,
            Uptime = uptime,
            TotalFrames = frames,
            TotalBytes = bytes,
            AverageFps = uptime.TotalSeconds > 0.5 ? frames / uptime.TotalSeconds : 0,
            AverageFrameBytes = frames > 0 ? (long)(bytes / (double)frames) : 0,
            RecentFps = Math.Round(Volatile.Read(ref _recentFps), 1),
            RecentFrameBytes = (long)(Volatile.Read(ref _recentFrameBytes) + 0.5),
            AverageFrameMs = Math.Round(_lastFrameMs, 2),
            TargetFps = Options.Fps,
            Quality = Options.Quality,
            Errors = ErrorCount,
            Monitor = DeviceName,
            Width = Volatile.Read(ref _lastFrameWidth),
            Height = Volatile.Read(ref _lastFrameHeight),
            Hub = _hub.Snapshot(uptime),
        };
    }

    // ---------------- внутреннее ----------------

    private string DeviceName
    {
        get { lock (_bitmapGate) return _deviceName; }
    }

    private static Screen? ResolveScreen(string deviceName)
    {
        Screen[] screens = Screen.AllScreens;

        if (string.IsNullOrWhiteSpace(deviceName))
            return Screen.PrimaryScreen ?? screens.FirstOrDefault();

        foreach (Screen screen in screens)
        {
            if (string.Equals(screen.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                return screen;
        }

        // Запрошенный монитор отключили: откатываемся на основной,
        // чтобы трансляция не молча встала.
        return Screen.PrimaryScreen ?? screens.FirstOrDefault();
    }

    private static bool TryGetBounds(Screen screen, out Rectangle bounds)
    {
        try
        {
            bounds = screen.Bounds;
            return bounds.Width > 0 && bounds.Height > 0;
        }
        catch
        {
            bounds = default;
            return false;
        }
    }

    private Bitmap? EnsureSurface(Size size, CaptureOptions options)
    {
        lock (_bitmapGate)
        {
            bool dirty = Volatile.Read(ref _surfaceDirty);

            if (!dirty &&
                _bounds.Width == size.Width && _bounds.Height == size.Height &&
                _surface is not null)
            {
                return _surface;
            }

            // Пересоздание выполняется только здесь, то есть в потоке
            // захвата, который является единственным владельцем растра.
            _surface?.Dispose();
            _scaled?.Dispose();

            // PixelFormat задан один раз: смена формата требует нового растра.
            if (_surfaceFormat != PixelFormat.Format24bppRgb)
            {
                _surfaceFormat = PixelFormat.Format24bppRgb;
                _surface = null;
            }

            _surface = JpegEncoder.CreateBitmap(size.Width, size.Height);
            _scaled = null;
            Volatile.Write(ref _surfaceDirty, false);

            return _surface;
        }
    }
}

public sealed record CaptureStatistics
{
    public required bool Running { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required TimeSpan Uptime { get; init; }
    public long TotalFrames { get; init; }
    public long TotalBytes { get; init; }
    public double AverageFps { get; init; }
    public long AverageFrameBytes { get; init; }

    /// <summary>
    /// Сколько кадров уходит сейчас. Кумулятивная AverageFps долго хранит
    /// прошлые настройки, поэтому показывать нужно эту.
    /// </summary>
    public double RecentFps { get; init; }

    /// <summary>Сколько весит кадр сейчас — реагирует на смену качества.</summary>
    public long RecentFrameBytes { get; init; }

    public double AverageFrameMs { get; init; }
    public required int TargetFps { get; init; }
    public required int Quality { get; init; }
    public long Errors { get; init; }
    public required string Monitor { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required HubStatistics Hub { get; init; }
}