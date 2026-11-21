namespace RpdFos.Core.Documents;

/// <summary>Принятый рисунок: тип, размер в точках и содержимое без метаданных.</summary>
public sealed record ImageInfo(string MimeType, string Extension, int Width, int Height, byte[] Content);

/// <summary>
/// Проверка и очистка рисунка для форматированного текста (этап 7, B4а; NFR-SEC-10). Тип определяется по сигнатуре
/// файла, а не по имени и заголовку запроса: PNG и JPEG. Файл разбирается по структуре, и в результат попадают только
/// части, нужные для показа и печати, — текстовые поля PNG, EXIF, XMP, комментарии и миниатюры JPEG (GPS, модель камеры,
/// имя автора) не сохраняются. Рисунок не перекодируется: пиксели остаются теми, что прислал автор. Побочное следствие —
/// поворот из EXIF (фото с телефона) вместе с метаданными пропадает.
/// </summary>
public static class ImageFiles
{
    public const int MaxBytes = 2 * 1024 * 1024;

    /// <summary>Рисунков на один документ.</summary>
    public const int MaxPerDocument = 100;

    /// <summary>Сторона рисунка в точках: больше — не схема и не скриншот, а ошибка (и нагрузка на браузер и конвертер).</summary>
    public const int MaxSide = 10000;

    public const string Png = "image/png", Jpeg = "image/jpeg";

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // Обязательные части PNG, прозрачность и описание цвета: без них изменились бы цвета и прозрачность.
    private static readonly HashSet<string> PngKept = ["IHDR", "PLTE", "tRNS", "IDAT", "IEND", "gAMA", "cHRM", "sRGB", "iCCP", "sBIT"];

    public const string NotImage = "Файл не похож на рисунок: принимаются PNG и JPEG.";
    public const string Damaged = "Файл рисунка повреждён.";
    public const string Unsupported = "Этот вид JPEG не поддерживается: сохраните рисунок как PNG или обычный JPEG.";

    public static string TooBigSides => $"Рисунок больше {MaxSide} точек по стороне.";

    /// <summary>Проверенный и очищенный рисунок или причина отказа (текст для автора).</summary>
    public static (ImageInfo? Image, string? Problem) Inspect(byte[] data)
    {
        if (data.AsSpan().StartsWith(PngSignature)) return ReadPng(data);
        if (data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return ReadJpeg(data);
        return (null, NotImage);
    }

    private static (ImageInfo?, string?) ReadPng(byte[] data)
    {
        var result = new MemoryStream(data.Length);
        result.Write(PngSignature);
        var position = PngSignature.Length;
        int width = 0, height = 0;
        var first = true;
        var sawData = false;
        while (position + 12 <= data.Length)
        {
            var length = (long)ReadUInt32(data, position);
            var type = System.Text.Encoding.ASCII.GetString(data, position + 4, 4);
            var total = 12 + length;
            if (length > int.MaxValue || position + total > data.Length) return (null, Damaged);
            if (first)
            {
                if (type != "IHDR" || length != 13) return (null, Damaged);
                width = (int)Math.Min(ReadUInt32(data, position + 8), int.MaxValue);
                height = (int)Math.Min(ReadUInt32(data, position + 12), int.MaxValue);
                first = false;
            }
            if (type == "IDAT") sawData = true;
            if (PngKept.Contains(type)) result.Write(data, position, (int)total);
            position += (int)total;
            if (type == "IEND")
            {
                if (!sawData) return (null, Damaged);
                return Checked(Png, ".png", width, height, result);
            }
        }
        return (null, Damaged);
    }

    private static (ImageInfo?, string?) ReadJpeg(byte[] data)
    {
        var result = new MemoryStream(data.Length);
        result.WriteByte(0xFF);
        result.WriteByte(0xD8);
        var position = 2;
        int width = 0, height = 0;
        bool sawFrame = false, sawScan = false, sawEnd = false;
        while (position < data.Length)
        {
            if (data[position] != 0xFF) return (null, Damaged);
            while (position < data.Length && data[position] == 0xFF) position++;
            if (position >= data.Length) break;
            var marker = data[position++];
            if (marker == 0xD9) { sawEnd = true; break; }
            // Маркеры без длины: повторное начало, служебный и перезапуски.
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) continue;
            if (position + 2 > data.Length) return (null, Damaged);
            var length = (data[position] << 8) | data[position + 1];
            if (length < 2 || position + length > data.Length) return (null, Damaged);
            var start = position - 2;
            var end = position + length;

            var frame = marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
            if (frame)
            {
                // Базовый, расширенный и прогрессивный — их показывают все браузеры и Word; арифметическое кодирование
                // и без потерь — нет.
                if (marker is not (0xC0 or 0xC1 or 0xC2)) return (null, Unsupported);
                if (length < 8) return (null, Damaged);
                height = (data[position + 3] << 8) | data[position + 4];
                width = (data[position + 5] << 8) | data[position + 6];
                sawFrame = true;
            }

            // Остаются таблицы, кадр, перезапуски, разметка Adobe (цветовое преобразование) и заголовок JFIF (только плотность).
            var keep = frame || marker is 0xC4 or 0xDB or 0xDD or 0xDA or 0xEE ||
                       marker == 0xE0 && length >= 7 && data[position + 2] == (byte)'J' && data[position + 3] == (byte)'F' &&
                       data[position + 4] == (byte)'I' && data[position + 5] == (byte)'F';
            position = end;
            if (marker == 0xDA)
            {
                if (!sawFrame) return (null, Damaged);
                sawScan = true;
                // Данные скана идут до следующего маркера: «FF 00» и перезапуски — часть данных.
                while (position + 1 < data.Length &&
                       !(data[position] == 0xFF && data[position + 1] != 0x00 && data[position + 1] is not (>= 0xD0 and <= 0xD7)))
                    position++;
                if (position + 1 >= data.Length) return (null, Damaged);
                end = position;
            }
            if (keep) result.Write(data, start, end - start);
        }
        if (!sawFrame || !sawScan || !sawEnd) return (null, Damaged);
        result.WriteByte(0xFF);
        result.WriteByte(0xD9);
        return Checked(Jpeg, ".jpg", width, height, result);
    }

    private static (ImageInfo?, string?) Checked(string mime, string extension, int width, int height, MemoryStream content) =>
        width < 1 || height < 1 ? (null, Damaged) :
        width > MaxSide || height > MaxSide ? (null, TooBigSides) :
        (new ImageInfo(mime, extension, width, height, content.ToArray()), null);

    private static uint ReadUInt32(byte[] data, int offset) =>
        (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);
}
