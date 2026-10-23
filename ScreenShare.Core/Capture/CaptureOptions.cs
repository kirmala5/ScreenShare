namespace ScreenShare.Core.Capture;

/// <summary>
/// Параметры захвата экрана. Меняются на лету через
/// <see cref="ScreenCapturer.Reconfigure"/> — без перезапуска трансляции.
/// </summary>
public sealed record CaptureOptions
{
    /// <summary>Имя устройства монитора (из <c>Screen.AllScreens</c>). Пусто — основной монитор.</summary>
    public string MonitorDeviceName { get; init; } = string.Empty;

    /// <summary>Кадров в секунду. 1…60.</summary>
    public int Fps { get; init; } = 10;

    /// <summary>Качество JPEG, 10…100.</summary>
    public int Quality { get; init; } = 70;

    /// <summary>Ограничение ширины кадра; 0 или меньше — без масштабирования.</summary>
    public int MaxWidth { get; init; }

    /// <summary>Ограничение высоты кадра; 0 или меньше — без масштабирования.</summary>
    public int MaxHeight { get; init; }

    /// <summary>Рисовать ли курсор мыши поверх кадра.</summary>
    public bool ShowCursor { get; init; } = true;

    /// <summary>Нормализованный интервал между кадрами в миллисекундах.</summary>
    public int FrameIntervalMs => 1000 / Math.Clamp(Fps, 1, 60);

    public CaptureOptions Normalize() => this with
    {
        Fps = Math.Clamp(Fps, 1, 60),
        Quality = Math.Clamp(Quality, Encoding.JpegEncoder.MinQuality, Encoding.JpegEncoder.MaxQuality),
        MaxWidth = Math.Max(0, MaxWidth),
        MaxHeight = Math.Max(0, MaxHeight),
    };
}

/// <summary>Описание монитора для выбора в интерфейсе (п. 3.2 «выбор монитора»).</summary>
public sealed record MonitorInfo(string DeviceName, int X, int Y, int Width, int Height, bool IsPrimary)
{
    public string DisplayName =>
        IsPrimary ? $"{DeviceName} ({Width}×{Height}, основной)" : $"{DeviceName} ({Width}×{Height})";

    public override string ToString() => DisplayName;
}