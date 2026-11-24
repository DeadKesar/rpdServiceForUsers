using System.Text.RegularExpressions;
using RpdFos.Api.Tests.Documents;
using RpdFos.Core.Printing;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using Xunit;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// Номера страниц оглавления по настоящему PDF (этап 8, A13): что напечатано в строке оглавления, сверяется со страницей, на которой
/// по тексту PDF стоит заголовок, и с номером внизу этой страницы. Те же номера вписываются в DOCX.
/// </summary>
public class TocPdfTests(ConverterFixture app) : IClassFixture<ConverterFixture>
{
    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, true, false)]
    public async Task Page_numbers_are_the_pages_of_the_headings(bool labs, bool practice, bool courseProject, bool section6)
    {
        var result = TocSample.Print(labs, practice, courseProject, section6);

        var bytes = await app.Converter.ConvertAsync(result.Content);

        using var pdf = PdfDocument.Open(bytes);
        var pages = pdf.GetPages().Select(p => (p.Number, Lines: Lines(p))).ToList();
        var contents = pages.Single(p => p.Lines[0] == "Содержание");
        Assert.Equal(2, contents.Number);
        var printed = Entries(contents.Lines.Skip(1));
        // Строки — те же, что построил генератор, и все на странице «Содержание».
        Assert.Equal(TocSample.Expected(section6).Select(TocSample.Bare), printed.Select(e => TocSample.Bare(e.Text)));

        var actual = result.Toc!.Lines.Select(line => Found(line, pages.Where(p => p.Number > contents.Number))).ToList();
        Assert.Equal(actual.Select(p => p.Number), printed.Select(e => e.Page));
        // Документ не уместился на одной странице: совпадение номеров не случайно.
        Assert.True(actual.Select(p => p.Number).Distinct().Count() >= 4);
        // Номер внизу страницы — тот же, что в оглавлении.
        Assert.All(actual, p => Assert.Equal(p.Number.ToString(), p.Lines[^1]));

        // В DOCX — номера той же вёрстки: по закладкам PDF, которые конвертер делает из заголовков.
        var headings = await app.Converter.HeadingsAsync(bytes);
        Assert.Equal(result.Toc.Lines.Count, headings.Count); // лишних закладок в PDF нет
        Assert.Equal(printed.Select(e => (int?)e.Page), DocxToc.Pages(result.Toc, headings));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(40)]
    public async Task Approval_sheet_follows_the_last_section_without_a_blank_page(int lastLines)
    {
        // Последний раздел кончается в разных местах страницы: раньше в нижней её трети пустые абзацы перед листом согласования
        // переходили на новую, и она оставалась пустой. На странице, кроме титула, всегда есть номер: пустая — где больше ничего нет.
        var result = TocSample.Print(false, false, false, section6: true, lastLines: lastLines);

        using var pdf = PdfDocument.Open(await app.Converter.ConvertAsync(result.Content));
        var pages = pdf.GetPages().Select(Lines).ToList();

        Assert.All(pages, lines => Assert.True(lines.Count > 1));
        // Лист согласования — с верха последней страницы, перед ним — страница с концом раздела 6.
        Assert.Equal(["ЛИСТ", "согласования рабочей программы"], pages[^1].Take(2));
        Assert.EndsWith(lastLines > 0 ? $"Строка {lastLines}." : "дисциплины.", pages[^2][^2]);
    }

    [Fact]
    public async Task Word_variant_is_laid_out_the_same_way()
    {
        // Поле TOC и вписанные номера вёрстку не меняют: файл для Word даёт те же страницы, что и PDF.
        var result = TocSample.Print(true, true, true, true);
        var pages = DocxToc.Pages(result.Toc!, await app.Converter.HeadingsAsync(await app.Converter.ConvertAsync(result.Content)));

        var word = DocxToc.ForWord(result.Content, result.Toc!, pages);

        Assert.DoesNotContain(null, pages);
        Assert.Equal(pages, DocxToc.Pages(result.Toc!, await app.Converter.HeadingsAsync(await app.Converter.ConvertAsync(word))));
    }

    // Страница, на которой стоит заголовок: строка PDF — заголовок целиком, у длинного — его начало с номером раздела.
    private static (int Number, List<string> Lines) Found(TocLine line, IEnumerable<(int Number, List<string> Lines)> pages)
    {
        var heading = TocSample.Bare($"{line.Number} {line.Title}");
        return pages.Single(p => p.Lines.Select(TocSample.Bare).Any(l => l == heading || l.Length >= 20 && heading.StartsWith(l, StringComparison.Ordinal)));
    }

    // Строки оглавления: длинное название — на двух строках, номер страницы — в конце последней, после точек заполнителя.
    private static List<(string Text, int Page)> Entries(IEnumerable<string> lines)
    {
        var entries = new List<(string, int)>();
        var text = "";
        foreach (var line in lines)
        {
            text = text.Length == 0 ? line : text + " " + line;
            if (Regex.Match(text, @"^(?<text>.*?\p{L}\)?)[\s.]+(?<page>\d+)$") is not { Success: true } match) continue;
            entries.Add((match.Groups["text"].Value, int.Parse(match.Groups["page"].Value)));
            text = "";
        }
        return entries;
    }

    // Строки страницы сверху вниз: слова с одной базовой линией.
    private static List<string> Lines(Page page) => page.GetWords()
        .GroupBy(w => Math.Round(w.Letters[0].StartBaseLine.Y, 0))
        .OrderByDescending(g => g.Key)
        .Select(g => string.Join(" ", g.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)))
        .ToList();
}
