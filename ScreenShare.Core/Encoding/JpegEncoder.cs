using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ScreenShare.Core.Encoding;

/// <summary>
/// Кодирование кадра в JPEG с управлением качеством и масштабом
/// (п. 3.1 № 2 и п. 3.2 «настройка FPS и качества»).
///
/// Важная деталь реализации: качество в JPEG задаётся не «на глаз», а
/// параметром кодировщика. Значения ниже 40 дают сильные артефакты на тексте
/// (скриншоты в основном состоят из текста), поэтому нижняя граница
/// ограничена величиной, при которой картинка остаётся читаемой.
/// </summary>
public static class JpegEncoder
{
    public const int MinQuality = 10;
    public const int MaxQuality = 100;

    /// <summary>
    /// Кодировщик JPEG ищется один раз: GetImageEncoders() возвращает
    /// GDI+ внутренние структуры, которые не стоит пересоздавать на каждый кадр.
    /// </summary>
    private static readonly ImageCodecInfo JpegCodec = FindJpegCodec();

    /// <summary>Кодирует изображение в JPEG.</summary>
    /// <param name="source">Кадр. Не изменяется и не освобождается.</param>
    /// <param name="quality">Качество JPEG, 10…100.</param>
    public static byte[] Encode(Image source, int quality)
    {
        ArgumentNullException.ThrowIfNull(source);

        quality = Math.Clamp(quality, MinQuality, MaxQuality);

        using var buffer = new MemoryStream(capacity: 128 * 1024);

        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);

        source.Save(buffer, JpegCodec, parameters);

        return buffer.ToArray();
    }

    /// <summary>
    /// Масштабирует изображение. Используется для ограничения размера кадра:
    /// разрешение 1920×1080 при 10 fps даёт заметный трафик, а половина
    /// разрешения на том же экране читается почти так же.
    /// </summary>
    public static Bitmap Scale(Image source, int maxWidth, int maxHeight)
    {
        ArgumentNullException.ThrowIfNull(source);

        // Ноль и отрицательное значение означают «без ограничения» по этой
        // стороне. Раньше здесь стояла проверка maxHeight <= 0, из-за чего
        // ограничение только по ширине (высота 0) вообще не применялось.
        int widthLimit = maxWidth > 0 ? maxWidth : int.MaxValue;
        int heightLimit = maxHeight > 0 ? maxHeight : int.MaxValue;

        if (source.Width <= widthLimit && source.Height <= heightLimit)
        {
            // Запрошенный размер не уменьшает картинку — возвращаем как есть.
            return source as Bitmap ?? CopyToBitmap(source);
        }

        // Сохраняем пропорции сторон.
        double scale = Math.Min(
            (double)widthLimit / source.Width,
            (double)heightLimit / source.Height);

        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));

        var target = CreateBitmap(width, height);

        using var graphics = Graphics.FromImage(target);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.HighQuality;

        graphics.DrawImage(source, new Rectangle(0, 0, width, height));

        return target;
    }

    /// <summary>
    /// Создаёт пустой растр для захвата экрана.
    ///
    /// Формат принудительно 24 bpp RGB: кадр экрана непрозрачен, а канал
    /// альфа в JPEG всё равно кодируется как мусор и увеличивает размер.
    /// </summary>
    public static Bitmap CreateBitmap(int width, int height) =>
        new(width, height, PixelFormat.Format24bppRgb);

    /// <summary>Глубокая копия в растр без альфа-канала.</summary>
    public static Bitmap CopyToBitmap(Image source)
    {
        var target = CreateBitmap(source.Width, source.Height);

        using var graphics = Graphics.FromImage(target);
        graphics.DrawImageUnscaled(source, 0, 0);

        return target;
    }

    private static ImageCodecInfo FindJpegCodec()
    {
        foreach (ImageCodecInfo codec in ImageCodecInfo.GetImageEncoders())
        {
            if (codec.FormatID == ImageFormat.Jpeg.Guid)
                return codec;
        }

        throw new InvalidOperationException(
            "В системе не найден кодировщик JPEG. Трансляция экрана без него невозможна.");
    }
}