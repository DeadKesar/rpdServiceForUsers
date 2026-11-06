using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;

namespace RpdFos.Corpus;

/// <summary>Текст и таблицы настоящего документа: DOCX — абзацы и таблицы, PDF — текст страниц (таблиц нет).</summary>
public sealed record DocContent(string Text, IReadOnlyList<IReadOnlyList<IReadOnlyList<string>>> Tables, bool FromPdf);

public static class DocText
{
    public static DocContent Read(string path)
    {
        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            using var pdf = PdfDocument.Open(path);
            return new DocContent(string.Join("\n", pdf.GetPages().Select(p => p.Text)), [], FromPdf: true);
        }

        using var docx = WordprocessingDocument.Open(path, false);
        var body = docx.MainDocumentPart!.Document!.Body!;
        var text = string.Join("\n", body.Descendants<Paragraph>().Select(p => p.InnerText));
        var tables = body.Descendants<Table>()
            .Select(t => (IReadOnlyList<IReadOnlyList<string>>)t.Elements<TableRow>()
                .Select(r => (IReadOnlyList<string>)r.Elements<TableCell>().Select(c => Clean(c.InnerText)).ToList()).ToList())
            .ToList();
        return new DocContent(text, tables, FromPdf: false);
    }

    public static string Clean(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
}
