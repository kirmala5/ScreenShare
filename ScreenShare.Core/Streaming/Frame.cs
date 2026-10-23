namespace ScreenShare.Core.Streaming;

/// <summary>
/// Один кадр трансляции: уже сжатый JPEG плюс метаданные.
///
/// Метаданные нужны не только для статистики: клиент по <see cref="Sequence"/>
/// понимает, что пришёл новый кадр, а по <see cref="TimestampMs"/> может
/// считать задержку.
/// </summary>
public sealed record Frame
{
    /// <summary>Данные JPEG. Кадр неизменяем — им можно пользоваться из потока отдачи.</summary>
    public required byte[] Jpeg { get; init; }

    /// <summary>Порядковый номер кадра, начиная с 1.</summary>
    public required long Sequence { get; init; }

    /// <summary>Время захвата в миллисекундах UTC.</summary>
    public required long TimestampMs { get; init; }

    /// <summary>Размер кадра в пикселях (после масштабирования, если оно задано).</summary>
    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>Размер в байтах до сжатия — для оценки коэффициента сжатия.</summary>
    public required int RawBytes { get; init; }

    /// <summary>Позиция курсора в пикселях кадра, если курсор был отрисован.</summary>
    public int? CursorX { get; init; }

    public int? CursorY { get; init; }
}