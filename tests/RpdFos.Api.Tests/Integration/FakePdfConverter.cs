using System.Text;
using RpdFos.Core.Printing;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// Конвертер PDF для тестов: настоящий (Gotenberg) — отдельный сервис, его в тестах нет. Запоминает присланный DOCX
/// и отвечает PDF-заглушкой; <see cref="FailNext"/> — следующая конвертация отказывает, как недоступный сервис.
/// Тесты коллекции идут по очереди, поэтому общее состояние допустимо.
/// </summary>
public sealed class FakePdfConverter : IPdfConverter
{
    public static byte[]? LastDocx { get; private set; }
    public static bool FailNext { get; set; }

    public Task<byte[]> ConvertAsync(byte[] docx, CancellationToken ct = default)
    {
        if (FailNext)
        {
            FailNext = false;
            throw new PdfConversionException("Конвертер PDF недоступен (тест)");
        }
        LastDocx = docx;
        return Task.FromResult(Encoding.ASCII.GetBytes($"%PDF-1.7\n% тестовый PDF из DOCX {docx.Length} байт\n%%EOF\n"));
    }
}
