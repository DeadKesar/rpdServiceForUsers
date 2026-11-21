using System.Buffers.Binary;
using System.Text;

namespace RpdFos.Api.Tests.Documents;

/// <summary>
/// Рисунки для тестов, собранные по байтам: настоящая структура PNG и JPEG (сигнатура, части, маркеры), но без настоящих
/// пикселей — проверка и очистка читают структуру, а не раскодируют рисунок.
/// </summary>
public static class TestImages
{
    /// <summary>PNG: IHDR, необязательные части, один IDAT, IEND. <paramref name="extra"/> — части между IHDR и IDAT.</summary>
    public static byte[] Png(int width = 40, int height = 30, int seed = 0, params (string Type, byte[] Data)[] extra)
    {
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = 8;
        header[9] = 2;
        using var stream = new MemoryStream();
        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(stream, "IHDR", header);
        foreach (var (type, data) in extra) Chunk(stream, type, data);
        // Содержимое IDAT не раскодируется, но у разных рисунков должно различаться — по нему считается контрольная сумма.
        Chunk(stream, "IDAT", [0x78, 0x9C, (byte)seed, (byte)(seed >> 8), 1, 2, 3, 4]);
        Chunk(stream, "IEND", []);
        return stream.ToArray();
    }

    public static byte[] PngWithText(string text, int seed = 0) =>
        Png(40, 30, seed, ("tEXt", Encoding.Latin1.GetBytes("Author\0" + text)), ("tIME", [0x07, 0xEA, 1, 2, 3, 4, 5]));

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        stream.Write(length);
        var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        stream.Write(body);
        // Контрольная сумма нужна файлу, чтобы он оставался настоящим PNG; разбор её не проверяет.
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(body));
        stream.Write(crc);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++) crc = (crc & 1) != 0 ? crc >> 1 ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    /// <summary>JPEG: SOI, JFIF, EXIF и комментарий (метаданные), таблицы, кадр, скан с данными, EOI.</summary>
    public static byte[] Jpeg(int width = 40, int height = 30, int seed = 0, bool progressive = false)
    {
        using var stream = new MemoryStream();
        stream.Write([0xFF, 0xD8]);
        Segment(stream, 0xE0, [(byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0]);
        Segment(stream, 0xE1, [(byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0, .. Encoding.ASCII.GetBytes("GPS-55.75,37.61 Camera X1")]);
        Segment(stream, 0xFE, Encoding.ASCII.GetBytes("comment with a name"));
        Segment(stream, 0xDB, [(byte)0, .. Enumerable.Repeat((byte)16, 64)]);
        Segment(stream, (byte)(progressive ? 0xC2 : 0xC0), [8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 1, 1, 0x11, 0]);
        Segment(stream, 0xC4, [(byte)0, .. Enumerable.Repeat((byte)0, 16), (byte)0]);
        Segment(stream, 0xDA, [1, 1, 0, 0, 63, 0]);
        // Данные скана: «FF 00» — часть данных, не маркер.
        stream.Write([(byte)seed, 0xFF, 0x00, 0x12, 0x34, (byte)(seed >> 8)]);
        stream.Write([0xFF, 0xD9]);
        return stream.ToArray();
    }

    private static void Segment(Stream stream, byte marker, byte[] data)
    {
        stream.Write([0xFF, marker, (byte)((data.Length + 2) >> 8), (byte)(data.Length + 2)]);
        stream.Write(data);
    }
}
