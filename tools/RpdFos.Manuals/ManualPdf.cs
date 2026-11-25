using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Outline;

namespace RpdFos.Manuals;

/// <summary>
/// PDF руководства (этап 8, B9): страницу HTML печатает Chromium конвертера проекта (Gotenberg). Номеров страниц в оглавлении
/// Chromium не ставит, поэтому печать двойная: номера берутся из закладок первого прохода и печатаются вторым.
/// </summary>
public sealed class ManualPdf(HttpClient converter)
{
    /// <summary>Место номера в первом проходе — той же ширины, что и номер: второй проход не сдвигает страницы.</summary>
    private const string NoPage = "00";

    public async Task<byte[]> BuildAsync(Manual manual, CancellationToken ct = default)
    {
        var draft = await PrintAsync(manual, _ => NoPage, ct);
        var pages = PagesOf(draft, manual);
        var pdf = await PrintAsync(manual, h => pages[h.Anchor].ToString(), ct);

        // Молча отдать оглавление с неверными номерами нельзя: второй проход сверяется с первым.
        var final = PagesOf(pdf, manual);
        var moved = manual.Headings.FirstOrDefault(h => final[h.Anchor] != pages[h.Anchor]);
        return moved is null ? pdf : throw new InvalidOperationException($"«{moved.Text}» после второго прохода — на другой странице.");
    }

    /// <summary>Страницы заголовков по закладкам PDF: закладки идут в том же порядке, что и заголовки.</summary>
    public static IReadOnlyDictionary<string, int> PagesOf(byte[] pdf, Manual manual)
    {
        using var document = PdfDocument.Open(pdf);
        var marks = document.TryGetBookmarks(out var bookmarks)
            ? bookmarks.GetNodes().OfType<DocumentBookmarkNode>().ToList()
            : [];
        var pages = new Dictionary<string, int>();
        var next = 0;
        foreach (var heading in manual.Headings)
        {
            while (next < marks.Count && Squash(marks[next].Title) != Squash(heading.Text)) next++;
            if (next == marks.Count) throw new InvalidOperationException($"В PDF нет закладки «{heading.Text}».");
            pages[heading.Anchor] = marks[next++].PageNumber;
        }
        return pages;
    }

    private async Task<byte[]> PrintAsync(Manual manual, Func<ManualHeading, string> page, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent
        {
            { Html(Page(manual, page)), "files", "index.html" },
            { Html(Footer(manual)), "files", "footer.html" },
            { new StringContent("210mm"), "paperWidth" },
            { new StringContent("297mm"), "paperHeight" },
            { new StringContent("20mm"), "marginTop" },
            { new StringContent("20mm"), "marginBottom" },
            { new StringContent("25mm"), "marginLeft" },
            { new StringContent("15mm"), "marginRight" },
            { new StringContent("true"), "printBackground" },
            { new StringContent("true"), "generateDocumentOutline" },
            { new StringContent(JsonSerializer.Serialize(new { manual.Title, Subject = "АС создания РПД и ФОС" })), "metadata" }
        };
        // Конвертер складывает файлы в одну папку: снимки — без подпапки img.
        foreach (var image in manual.Images.Distinct())
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(manual.Path)!, image), ct);
            form.Add(new ByteArrayContent(bytes), "files", Path.GetFileName(image));
        }

        using var response = await converter.PostAsync("forms/chromium/convert/html", form, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Конвертер: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(ct)}");
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    private static StringContent Html(string text) =>
        new(text, Encoding.UTF8) { Headers = { ContentType = new MediaTypeHeaderValue("text/html") { CharSet = "utf-8" } } };

    private static string Page(Manual manual, Func<ManualHeading, string> page)
    {
        var contents = string.Concat(manual.Headings.Select(h =>
            $"<a class=\"toc{h.Level}\" href=\"#{h.Anchor}\"><span>{WebUtility.HtmlEncode(h.Text)}</span><i></i><b>{page(h)}</b></a>"));
        return $"""
            <!DOCTYPE html>
            <html lang="ru">
            <head>
            <meta charset="utf-8">
            <title>{WebUtility.HtmlEncode(manual.Title)}</title>
            <style>{Style}</style>
            </head>
            <body>
            <header>
            <p>Автоматизированная система создания рабочих программ дисциплин и фондов оценочных средств</p>
            <div class="title">{WebUtility.HtmlEncode(manual.Title)}</div>
            </header>
            <nav><div class="title">Содержание</div>{contents}</nav>
            <main>{manual.ToHtml(Path.GetFileName!)}</main>
            </body>
            </html>
            """;
    }

    private static string Footer(Manual manual) => $$"""
        <!DOCTYPE html>
        <html><head><meta charset="utf-8"><style>
        /* Поля страницы на колонтитул не действуют. Класс, а не div: у Chromium вокруг колонтитула есть свой div. */
        .line { width: 100%; box-sizing: border-box; padding: 0 15mm 0 25mm; display: flex; justify-content: space-between;
              font: 8pt 'Liberation Sans', Arial, sans-serif; color: #555; }
        </style></head>
        <body><div class="line"><span>{{WebUtility.HtmlEncode(manual.Title)}}</span><span class="pageNumber"></span></div></body></html>
        """;

    private static string Squash(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c) && c != '­'));

    private const string Style = """
        body { margin: 0; font: 11pt/1.4 'Liberation Sans', Arial, sans-serif; color: #000; }
        header { height: 240mm; display: flex; flex-direction: column; justify-content: center; text-align: center; }
        header .title { font-size: 22pt; font-weight: 700; margin-top: 10mm; }
        nav { break-before: page; }
        nav .title { font-size: 15pt; font-weight: 700; margin-bottom: 4mm; }
        nav a { display: flex; align-items: baseline; color: inherit; text-decoration: none; line-height: 1.55; }
        nav a i { flex: 1; border-bottom: 1px dotted #777; margin: 0 2mm; }
        nav a b { font-weight: 400; min-width: 7mm; text-align: right; }
        nav .toc3 { padding-left: 6mm; }
        main { break-before: page; }
        h2 { font-size: 16pt; line-height: 1.25; margin: 12mm 0 4mm; break-after: avoid; }
        main > h2:first-child { margin-top: 0; }
        h3 { font-size: 12.5pt; line-height: 1.25; margin: 7mm 0 2mm; break-after: avoid; }
        p, ul, ol { margin: 0 0 2.5mm; }
        ul, ol { padding-left: 7mm; }
        li { margin-bottom: 1mm; }
        a { color: #0b4f9e; }
        table { border-collapse: collapse; width: 100%; margin: 2mm 0 4mm; font-size: 9.5pt; line-height: 1.3; }
        th, td { border: 1px solid #999; padding: 1.2mm 2mm; text-align: left; vertical-align: top; }
        th { background: #eee; }
        tr { break-inside: avoid; }
        thead { display: table-header-group; }
        blockquote { margin: 2mm 0 4mm; padding: 2mm 4mm; border-left: 1.2mm solid #0b4f9e; background: #f1f5fa; break-inside: avoid; }
        blockquote p:last-child { margin: 0; }
        figure { margin: 3mm 0 5mm; text-align: center; break-inside: avoid; }
        figure img { box-sizing: border-box; max-width: 100%; max-height: 120mm; border: 1px solid #bbb; }
        figcaption { font-size: 9.5pt; margin-top: 1.5mm; }
        code { font: 9.5pt 'Liberation Mono', Consolas, monospace; }
        """;
}
