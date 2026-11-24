using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using RpdFos.Core.DocumentTypes;

namespace RpdFos.Core.Printing;

/// <summary>
/// Строка оглавления: закладка заголовка, уровень с единицы, номер раздела с точкой («3.1.», у заголовка без номера — пусто) и
/// название; <paramref name="Heading"/> — заголовок, как он напечатан в тексте.
/// </summary>
public sealed record TocLine(string Anchor, int Level, string Number, string Title, string Heading);

/// <summary>Оглавление сформированного документа: уровни заголовков («1-2») и строки по порядку.</summary>
public sealed record DocumentToc(string Levels, IReadOnlyList<TocLine> Lines);

/// <summary>
/// Оглавление (цель вида <c>toc</c>, этап 8, A13). Строки строятся по заголовкам, которые остались в теле документа после
/// заполнения, — раздел, который не напечатан, в оглавление не попадает. Строка — ссылка на закладку заголовка и номер страницы
/// полем PAGEREF, оформление — у образца уровня внутри закладки оглавления. Номер вычисляет тот, кто раскладывает текст по
/// страницам: в PDF — конвертер, в DOCX он вписан по вёрстке PDF (<see cref="ForWord"/>).
/// </summary>
public static partial class DocxToc
{
    // «3.1 Структура дисциплины», «6. Заголовок автора», в черновике и «6.» без названия: номер раздела, точка или пробел и название.
    // «3D-модели» — не номер.
    [GeneratedRegex(@"^(?<number>\d{1,2}(?:\.\d{1,2})*)(?:\.\s*|\s+|$)(?<title>.*)$")]
    private static partial Regex Numbered();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>Строки оглавления вместо образцов внутри закладки; null — образца строки в закладке нет.</summary>
    internal static DocumentToc? Build(MainDocumentPart main, BookmarkStart bookmark, string name, string levels, List<string> warnings)
    {
        var body = main.Document!.Body!;
        var start = TopLevel(bookmark);
        var closing = body.Descendants<BookmarkEnd>().FirstOrDefault(e => e.Id?.Value == bookmark.Id?.Value);
        var last = closing is null ? start : TopLevel(closing);
        var block = new List<OpenXmlElement>();
        for (OpenXmlElement? element = start; element is not null; element = element.NextSibling())
        {
            block.Add(element);
            if (element == last) break;
        }
        // Образцы по порядку: первый абзац — строка уровня 1, второй — уровня 2; для уровней глубже — последний.
        var samples = block.OfType<Paragraph>().Where(p => p.Descendants<Text>().Any()).ToList();
        if (samples.Count == 0)
        {
            warnings.Add($"Закладка {name}: внутри нет образца строки оглавления — оставлена как есть.");
            return null;
        }

        var lines = new List<TocLine>();
        var anchors = body.Descendants<BookmarkStart>().Select(b => b.Name?.Value).ToHashSet();
        var id = body.Descendants<BookmarkStart>().Select(b => int.TryParse(b.Id?.Value, out var n) ? n : 0).DefaultIfEmpty(0).Max();
        var anchor = 0;
        foreach (var (heading, level, label) in Headings(main, TocLevels.Depth(levels) ?? 1))
        {
            string next;
            do next = $"_Toc{++anchor:D9}";
            while (!anchors.Add(next));
            Mark(heading, next, (++id).ToString());
            var text = HeadingText(heading);
            var (number, title) = Split(text, label);
            if (label is "") warnings.Add($"Автоматический номер заголовка «{text}» в оглавление не попал: считаются только цифры.");
            lines.Add(new TocLine(next, level, number, title, text));
        }

        var after = last;
        foreach (var line in lines)
        {
            var paragraph = Line(samples[Math.Min(line.Level, samples.Count) - 1], line);
            after.InsertAfterSelf(paragraph);
            after = paragraph;
        }
        // Пустой абзац после строк: в нём Word заканчивает поле оглавления (ForWord), и вёрстка DOCX и PDF одна.
        after.InsertAfterSelf(new Paragraph { ParagraphProperties = samples[^1].ParagraphProperties?.CloneNode(true) as ParagraphProperties });
        foreach (var element in block) element.Remove();
        return new DocumentToc(levels, lines);
    }

    /// <summary>
    /// Страницы строк по закладкам PDF, которые конвертер сделал из тех же заголовков: закладки идут по порядку документа. Строка
    /// без закладки — null.
    /// </summary>
    public static IReadOnlyList<int?> Pages(DocumentToc toc, IReadOnlyList<PdfHeading> headings)
    {
        var pages = new int?[toc.Lines.Count];
        var next = 0;
        for (var i = 0; i < pages.Length; i++)
            for (var j = next; j < headings.Count; j++)
                // У заголовка с автоматическим номером закладка PDF начинается с номера.
                if (Bare(headings[j].Title).EndsWith(Bare(toc.Lines[i].Heading), StringComparison.Ordinal))
                {
                    pages[i] = headings[j].Page;
                    next = j + 1;
                    break;
                }
        return pages;
    }

    /// <summary>
    /// Оглавление для Word: номера страниц вписаны в строки, строки — внутри поля TOC, чтобы «Обновить поле» работало на всём
    /// оглавлении. Номера для части строк неизвестны — Word предложит обновить поля, когда файл откроют. В DOCX для конвертера поля
    /// TOC нет: внутри него LibreOffice не вычисляет PAGEREF, а берёт вписанные номера.
    /// </summary>
    public static byte[] ForWord(byte[] docx, DocumentToc toc, IReadOnlyList<int?> pages)
    {
        using var stream = new MemoryStream();
        stream.Write(docx);
        using (var document = WordprocessingDocument.Open(stream, true))
        {
            var main = document.MainDocumentPart!;
            var links = main.Document!.Body!.Descendants<Hyperlink>().Where(l => l.Anchor?.Value is not null)
                .GroupBy(l => l.Anchor!.Value!).ToDictionary(g => g.Key, g => g.First());
            for (var i = 0; i < toc.Lines.Count; i++)
                if (pages[i] is { } page) PageText(links[toc.Lines[i].Anchor]).Text = page.ToString();

            var first = links[toc.Lines[0].Anchor];
            var properties = first.Elements<Run>().First().RunProperties;
            foreach (var run in (Run[])[Field(FieldCharValues.Begin), Instruction($" TOC \\o \"{toc.Levels}\" \\h \\z \\u "), Field(FieldCharValues.Separate)])
                first.InsertBeforeSelf(run);
            links[toc.Lines[^1].Anchor].Ancestors<Paragraph>().First().NextSibling<Paragraph>()!.AppendChild(Field(FieldCharValues.End));

            if (pages.Contains(null) && main.DocumentSettingsPart is { Settings: { } settings } && settings.GetFirstChild<UpdateFieldsOnOpen>() is null)
                settings.AddChild(new UpdateFieldsOnOpen { Val = true });

            Run Field(FieldCharValues type) => new(Properties(properties), new FieldChar { FieldCharType = type });
            Run Instruction(string code) => new(Properties(properties), new FieldCode(code) { Space = SpaceProcessingModeValues.Preserve });
        }
        return stream.ToArray();
    }

    // Заголовки — абзацы с уровнем структуры (своим или стиля) и текстом, по порядку документа, и их автоматические номера. В таблицах
    // и надписях заголовков разделов нет, но списки в них счёт номеров продолжают.
    private static List<(Paragraph Paragraph, int Level, string? Label)> Headings(MainDocumentPart main, int depth)
    {
        var styles = (main.StyleDefinitionsPart?.Styles?.Elements<Style>() ?? []).Where(s => s.StyleId?.Value is not null)
            .GroupBy(s => s.StyleId!.Value!).ToDictionary(g => g.Key, g => g.First());
        var numbers = new DocxListNumbers(main);
        var headings = new List<(Paragraph, int, string?)>();
        foreach (var paragraph in main.Document!.Body!.Descendants<Paragraph>())
        {
            var inherited = Inherited(paragraph, styles);
            var label = numbers.Next(paragraph, inherited);
            // Обычный текст у Word — уровень 9, считая с нуля.
            var level = (paragraph.ParagraphProperties?.OutlineLevel?.Val?.Value
                ?? inherited.Select(p => p.OutlineLevel?.Val?.Value).FirstOrDefault(l => l is not null) ?? 9) + 1;
            if (level <= depth && HeadingText(paragraph).Length > 0 &&
                !paragraph.Ancestors<TableCell>().Any() && !paragraph.Ancestors<TextBoxContent>().Any())
                headings.Add((paragraph, level, label));
        }
        return headings;
    }

    // Свойства абзаца из его стиля и стилей, на которых тот основан.
    private static List<StyleParagraphProperties> Inherited(Paragraph paragraph, Dictionary<string, Style> styles)
    {
        var result = new List<StyleParagraphProperties>();
        var seen = new HashSet<string>();
        for (var id = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
             id is not null && seen.Add(id) && styles.TryGetValue(id, out var style); id = style.BasedOn?.Val?.Value)
            if (style.StyleParagraphProperties is { } properties) result.Add(properties);
        return result;
    }

    // Номер и название: номер — автоматический или в начале текста заголовка, с точкой на конце; двоеточие заголовка в оглавлении не нужно.
    private static (string Number, string Title) Split(string heading, string? label)
    {
        var match = Numbered().Match(heading);
        var (number, title) = !string.IsNullOrEmpty(label) ? (label, heading)
            : match.Success ? (match.Groups["number"].Value, match.Groups["title"].Value)
            : ("", heading);
        return (number.Length > 0 && char.IsDigit(number[^1]) ? number + "." : number, title.TrimEnd(' ', ':'));
    }

    private static string HeadingText(Paragraph paragraph)
    {
        var text = new StringBuilder();
        foreach (var element in paragraph.Descendants())
            text.Append(element switch { Text t => t.Text, TabChar or Break => " ", NoBreakHyphen => "-", _ => "" });
        return Spaces().Replace(text.ToString(), " ").Trim();
    }

    private static string Bare(string text) => Spaces().Replace(text, "");

    private static OpenXmlElement TopLevel(OpenXmlElement element) =>
        element.Parent is Body ? element : element.Ancestors().First(a => a.Parent is Body);

    // Закладка — вокруг текста заголовка: на неё ссылается строка оглавления и её страницу печатает PAGEREF.
    private static void Mark(Paragraph heading, string anchor, string id)
    {
        var start = new BookmarkStart { Id = id, Name = anchor };
        if (heading.ParagraphProperties is { } properties) properties.InsertAfterSelf(start);
        else heading.PrependChild(start);
        heading.AppendChild(new BookmarkEnd { Id = id });
    }

    // «1.<таб>Название<таб>3» или «1.1. Название<таб>3»: номер отделяется табуляцией, когда у образца есть позиция слева.
    private static Paragraph Line(Paragraph sample, TocLine line)
    {
        var properties = sample.Descendants<Run>().First(r => r.Elements<Text>().Any()).RunProperties;
        var numberTab = sample.ParagraphProperties?.Tabs?.Elements<TabStop>().Any(t => t.Val?.Value == TabStopValues.Left) == true;
        var link = new Hyperlink { Anchor = line.Anchor, History = true };
        if (line.Number.Length > 0 && numberTab)
        {
            link.AppendChild(TextRun(line.Number));
            link.AppendChild(new Run(Properties(properties), new TabChar()));
            link.AppendChild(TextRun(line.Title));
        }
        else link.AppendChild(TextRun($"{line.Number} {line.Title}".Trim()));
        link.AppendChild(new Run(Properties(properties), new TabChar()));
        link.AppendChild(new Run(Properties(properties), new FieldChar { FieldCharType = FieldCharValues.Begin }));
        link.AppendChild(new Run(Properties(properties), new FieldCode($" PAGEREF {line.Anchor} \\h ") { Space = SpaceProcessingModeValues.Preserve }));
        link.AppendChild(new Run(Properties(properties), new FieldChar { FieldCharType = FieldCharValues.Separate }));
        link.AppendChild(TextRun(""));
        link.AppendChild(new Run(Properties(properties), new FieldChar { FieldCharType = FieldCharValues.End }));
        return new Paragraph(link) { ParagraphProperties = sample.ParagraphProperties?.CloneNode(true) as ParagraphProperties };

        Run TextRun(string text) => new(Properties(properties), new Text(text) { Space = SpaceProcessingModeValues.Preserve });
    }

    private static RunProperties Properties(RunProperties? sample) => sample?.CloneNode(true) as RunProperties ?? new RunProperties();

    // Вписанный номер страницы — текст между разделителем и концом поля PAGEREF строки.
    private static Text PageText(Hyperlink line) =>
        line.Elements<Run>().First(r => r.GetFirstChild<FieldChar>()?.FieldCharType?.Value == FieldCharValues.Separate).NextSibling<Run>()!.GetFirstChild<Text>()!;
}
