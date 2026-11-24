using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using RpdFos.Core.Printing;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// Конвертер PDF для тестов: настоящий (Gotenberg) — отдельный сервис, здесь его нет (проверки номеров страниц по настоящему PDF —
/// <see cref="TocPdfTests"/>). Запоминает присланный DOCX, отвечает PDF-заглушкой и закладками — по заголовкам оглавления этого DOCX,
/// страницы с третьей по порядку; <see cref="FailNext"/> — следующая конвертация отказывает, как недоступный сервис,
/// <see cref="HangNext"/> — не отвечает, пока её не отменят. Тесты коллекции идут по очереди, поэтому общее состояние допустимо.
/// </summary>
public sealed class FakePdfConverter : IPdfConverter
{
    public static byte[]? LastDocx { get; private set; }
    public static bool FailNext { get; set; }
    public static bool HangNext { get; set; }

    /// <summary>Сколько раз просили конвертировать: DOCX по прежним шаблонам обходится без конвертера.</summary>
    public static int Conversions { get; private set; }

    public async Task<byte[]> ConvertAsync(byte[] docx, CancellationToken ct = default)
    {
        Conversions++;
        if (FailNext)
        {
            FailNext = false;
            throw new PdfConversionException("Конвертер PDF недоступен (тест)");
        }
        if (HangNext)
        {
            HangNext = false;
            await Task.Delay(Timeout.Infinite, ct);
        }
        LastDocx = docx;
        return Encoding.ASCII.GetBytes($"%PDF-1.7\n% тестовый PDF из DOCX {docx.Length} байт\n%%EOF\n");
    }

    public Task<IReadOnlyList<PdfHeading>> HeadingsAsync(byte[] pdf, CancellationToken ct = default)
    {
        using var document = WordprocessingDocument.Open(new MemoryStream(LastDocx!), false);
        var body = document.MainDocumentPart!.Document!.Body!;
        var anchors = body.Descendants<Hyperlink>().Select(l => l.Anchor?.Value).OfType<string>().ToHashSet();
        return Task.FromResult<IReadOnlyList<PdfHeading>>(body.Descendants<Paragraph>()
            .Where(p => p.Elements<BookmarkStart>().Any(b => anchors.Contains(b.Name!.Value!)))
            .Select((p, i) => new PdfHeading(p.InnerText, i + 3)).ToList());
    }
}
