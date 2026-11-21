using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PdfLetter = UglyToad.PdfPig.Content.Letter;
using M = DocumentFormat.OpenXml.Math;
using Vml = DocumentFormat.OpenXml.Vml.Office;

namespace RpdFos.Corpus;

/// <summary>
/// Читает настоящий ФОС в плоский список блоков. Зачем свой читатель, а не <see cref="DocText"/>: отчёту нужны
/// признаки оформления (заголовок, полужирный, автонумерация), формулы и рисунки, закладки файла оформления.
/// </summary>
public static class FosReader
{
    public static FosDoc Read(string id, string path, string? convertedPath)
    {
        var name = Path.GetFileName(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".doc" or ".docx" or ".pdf"))
            return Failed(id, name, ext.TrimStart('.'), "формат не поддерживается");
        var real = ext == ".doc" ? convertedPath : path;
        if (real is null || !File.Exists(real))
            return Failed(id, name, ext.TrimStart('.'), "нужна копия .docx в _преобразовано\\ФОС");
        try
        {
            return Path.GetExtension(real).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                ? ReadPdf(id, name, real)
                : ReadDocx(id, name, ext == ".doc" ? "doc→docx" : "docx", ext == ".doc" ? "_преобразовано" : "исходный", real);
        }
        catch (Exception e)
        {
            return Failed(id, name, ext.TrimStart('.'), e.GetType().Name + ": " + e.Message);
        }
    }

    /// <summary>Номер закреплён за файлом, которого в папке больше нет: строка в отчётах остаётся, номер не выдаётся повторно.</summary>
    public static FosDoc Missing(string id, string name) =>
        Failed(id, name, Path.GetExtension(name).TrimStart('.').ToLowerInvariant(), "файла нет в папке");

    private static FosDoc Failed(string id, string name, string format, string error) =>
        new(id, name, format, "", [], [], null, 0, 0, 0, error);

    // ---------- DOCX ----------

    private sealed record StyleInfo(string Name, int? Outline, bool Bold, int? NumId);

    private static FosDoc ReadDocx(string id, string name, string format, string source, string path)
    {
        using var docx = WordprocessingDocument.Open(path, false);
        var main = docx.MainDocumentPart!;
        var styles = ReadStyles(main);
        var numFormats = ReadNumberFormats(main);
        var body = main.Document!.Body!;

        var blocks = new List<FosBlock>();
        var starts = new Dictionary<string, (string Name, int Block)>();
        var bookmarks = new List<FosBookmark>();
        void CloseBookmark(string? bookmarkId, int block)
        {
            if (bookmarkId is not null && starts.Remove(bookmarkId, out var s) && !s.Name.StartsWith('_'))
                bookmarks.Add(new FosBookmark(s.Name, s.Block, Math.Max(s.Block, block)));
        }

        foreach (var element in Flatten(body))
        {
            var index = blocks.Count;
            switch (element)
            {
                case Paragraph p:
                    foreach (var b in p.Descendants<BookmarkStart>())
                        if (b.Id?.Value is { } bid && b.Name?.Value is { } bn) starts[bid] = (bn, index);
                    blocks.Add(ReadParagraph(index, p, styles, numFormats));
                    foreach (var e in p.Descendants<BookmarkEnd>()) CloseBookmark(e.Id?.Value, index);
                    break;
                case Table t:
                    foreach (var b in t.Descendants<BookmarkStart>())
                        if (b.Id?.Value is { } bid && b.Name?.Value is { } bn) starts[bid] = (bn, index);
                    blocks.Add(ReadTable(index, t));
                    foreach (var e in t.Descendants<BookmarkEnd>()) CloseBookmark(e.Id?.Value, index);
                    break;
                case BookmarkStart b2 when b2.Id?.Value is { } bid2 && b2.Name?.Value is { } bn2:
                    starts[bid2] = (bn2, index);
                    break;
                case BookmarkEnd e2:
                    CloseBookmark(e2.Id?.Value, Math.Max(0, index - 1));
                    break;
            }
        }
        // Закладка без конца — в документе её нет: считаем до последнего блока.
        foreach (var s in starts.Values.Where(s => !s.Name.StartsWith('_')))
            bookmarks.Add(new FosBookmark(s.Name, s.Block, Math.Max(s.Block, blocks.Count - 1)));

        int? pages = int.TryParse(docx.ExtendedFilePropertiesPart?.Properties?.Pages?.Text, out var pg) ? pg : null;
        var textChars = blocks.OfType<FosParagraph>().Sum(p => p.Text.Length) +
                        blocks.OfType<FosTable>().Sum(t => t.Rows.Sum(r => r.Sum(c => c.Length)));
        var images = blocks.Sum(b => b is FosParagraph p ? p.Drawings : ((FosTable)b).Drawings);
        var small = blocks.Sum(b => b is FosParagraph p ? p.SmallDrawings : ((FosTable)b).SmallDrawings);
        return new FosDoc(id, name, format, source, blocks, bookmarks.OrderBy(b => b.StartBlock).ToList(), pages, images, small, textChars, null);
    }

    /// <summary>
    /// Граница «крупного» рисунка — 4,5 см по любой стороне. Эмблема вуза в шапке каждой части ФОС меньше,
    /// рисунки и снимки экрана в заданиях — больше; без этого эмблемы дали бы сотни «рисунков в заданиях».
    /// </summary>
    private const long BigEmu = 1_620_000;

    private static (int Big, int Small) CountDrawings(OpenXmlElement e)
    {
        int big = 0, small = 0;
        foreach (var d in e.Descendants<Drawing>())
        {
            var ext = d.Descendants<DW.Extent>().FirstOrDefault();
            if (ext is { Cx: { } cx, Cy: { } cy } && cx.Value < BigEmu && cy.Value < BigEmu) small++;
            else big++;
        }
        big += e.Descendants<Picture>().Count() + e.Descendants<EmbeddedObject>().Count(o => !IsEquationObject(o));
        return (big, small);
    }

    private static IEnumerable<OpenXmlElement> Flatten(OpenXmlElement parent)
    {
        foreach (var el in parent.ChildElements)
        {
            if (el is SdtBlock { SdtContentBlock: { } content })
                foreach (var inner in Flatten(content)) yield return inner;
            else yield return el;
        }
    }

    private static FosParagraph ReadParagraph(int index, Paragraph p, Dictionary<string, StyleInfo> styles,
        Dictionary<(int NumId, int Level), string> numFormats)
    {
        var text = Clean(TextOf(p));
        var pr = p.ParagraphProperties;
        var style = pr?.ParagraphStyleId?.Val?.Value is { } sid && styles.TryGetValue(sid, out var si) ? si : null;

        // Полужирный — если полужирны все непустые прогоны (или стиль абзаца полужирный и прогоны не отменяют).
        var runs = p.Descendants<Run>().Where(r => r.Descendants<Text>().Any(t => !string.IsNullOrWhiteSpace(t.Text))).ToList();
        var bold = runs.Count > 0 && runs.All(r => r.RunProperties?.Bold is { } b ? b.Val?.Value ?? true : style?.Bold == true);

        var centered = pr?.Justification?.Val?.Value == JustificationValues.Center;
        var outline = pr?.OutlineLevel?.Val?.Value ?? style?.Outline;
        var styleHeading = outline is < 9 || (style is not null && FosHeuristics.HeadingStyle.IsMatch(style.Name));

        var numPr = pr?.NumberingProperties;
        var numId = numPr?.NumberingId?.Val?.Value ?? style?.NumId;
        if (numId == 0) numId = null;
        var level = numPr?.NumberingLevelReference?.Val?.Value ?? 0;
        var format = numId is { } nid && numFormats.TryGetValue((nid, level), out var f) ? f : "";

        var equations = p.Descendants<M.OfficeMath>().Count() + p.Descendants<EmbeddedObject>().Count(IsEquationObject) +
                        p.Descendants<FieldCode>().Count(c => c.Text.TrimStart().StartsWith("EQ ", StringComparison.Ordinal));
        var (drawings, small) = CountDrawings(p);
        var pageBreak = pr?.PageBreakBefore is not null || p.Descendants<Break>().Any(b => b.Type?.Value == BreakValues.Page);
        var heading = FosHeuristics.LooksLikeHeading(text, styleHeading, bold, centered);
        return new FosParagraph(index, text, heading, bold, centered, numId, level, format, equations, drawings, small,
            pr?.SectionProperties is not null, pageBreak);
    }

    private static bool IsEquationObject(EmbeddedObject o) =>
        o.Descendants<Vml.OleObject>().Any(x => x.ProgId?.Value is { } id &&
            (id.Contains("Equation", StringComparison.OrdinalIgnoreCase) || id.Contains("DSMT", StringComparison.OrdinalIgnoreCase) ||
             id.Contains("MathType", StringComparison.OrdinalIgnoreCase)));

    private static FosTable ReadTable(int index, Table t)
    {
        var rows = t.Elements<TableRow>()
            .Select(r => (IReadOnlyList<string>)r.Elements<TableCell>().Select(c => Clean(string.Join(" ", c.Descendants<Paragraph>().Select(TextOf)))).ToList())
            .ToList();
        var equations = t.Descendants<M.OfficeMath>().Count() + t.Descendants<EmbeddedObject>().Count(IsEquationObject);
        var (drawings, small) = CountDrawings(t);
        var merged = t.Descendants<TableCell>().Count(c => c.TableCellProperties is { } p && (p.GridSpan?.Val?.Value > 1 || p.VerticalMerge is not null));
        return new FosTable(index, rows, equations, drawings, small, t.Descendants<Table>().Count(), merged);
    }

    /// <summary>Текст абзаца: табуляция и перенос строки дают пробел, иначе слова слипаются.</summary>
    private static string TextOf(Paragraph p)
    {
        var sb = new StringBuilder();
        foreach (var e in p.Descendants())
            switch (e)
            {
                case Text t: sb.Append(t.Text); break;
                case TabChar or Break or CarriageReturn: sb.Append(' '); break;
                case NoBreakHyphen: sb.Append('-'); break;
            }
        return sb.ToString();
    }

    private static Dictionary<string, StyleInfo> ReadStyles(MainDocumentPart main)
    {
        var result = new Dictionary<string, StyleInfo>();
        foreach (var s in main.StyleDefinitionsPart?.Styles?.Elements<Style>() ?? [])
        {
            if (s.StyleId?.Value is not { } sid) continue;
            var ppr = s.StyleParagraphProperties;
            result[sid] = new StyleInfo(s.StyleName?.Val?.Value ?? sid, ppr?.OutlineLevel?.Val?.Value,
                s.StyleRunProperties?.Bold is { } b && (b.Val?.Value ?? true), ppr?.NumberingProperties?.NumberingId?.Val?.Value);
        }
        return result;
    }

    /// <summary>Формат номера по (список, уровень): decimal, bullet, russianLower…</summary>
    private static Dictionary<(int, int), string> ReadNumberFormats(MainDocumentPart main)
    {
        var result = new Dictionary<(int, int), string>();
        var numbering = main.NumberingDefinitionsPart?.Numbering;
        if (numbering is null) return result;
        var abstracts = numbering.Elements<AbstractNum>().Where(a => a.AbstractNumberId?.Value is not null)
            .ToDictionary(a => a.AbstractNumberId!.Value, a => a);
        foreach (var n in numbering.Elements<NumberingInstance>())
        {
            if (n.NumberID?.Value is not { } numId || n.AbstractNumId?.Val?.Value is not { } aid || !abstracts.TryGetValue(aid, out var abs)) continue;
            foreach (var l in abs.Elements<Level>())
                if (l.LevelIndex?.Value is { } li)
                    result[(numId, li)] = l.NumberingFormat?.Val?.InnerText ?? "";
        }
        return result;
    }

    // ---------- PDF ----------

    private static FosDoc ReadPdf(string id, string name, string path)
    {
        using var pdf = PdfDocument.Open(path);
        var blocks = new List<FosBlock>();
        int big = 0, small = 0, chars = 0;
        foreach (var page in pdf.GetPages())
        {
            foreach (var image in page.GetImages())
                if (image.BoundingBox.Width >= 128 || image.BoundingBox.Height >= 128) big++; else small++;   // 128 pt ≈ 4,5 см
            foreach (var line in Lines(page))
            {
                chars += line.Text.Length;
                blocks.Add(new FosParagraph(blocks.Count, line.Text, FosHeuristics.LooksLikeHeading(line.Text, false, line.Bold, line.Centered),
                    line.Bold, line.Centered, null, 0, "", 0, 0, 0, false, false));
            }
        }
        return new FosDoc(id, name, "pdf", "исходный", blocks, [], pdf.NumberOfPages, big, small, chars, null);
    }

    private sealed record PdfLine(string Text, bool Bold, bool Centered);

    /// <summary>Строки страницы по буквам: абзацев PDF не хранит, а полужирный нужен, чтобы найти заголовки.</summary>
    private static IEnumerable<PdfLine> Lines(UglyToad.PdfPig.Content.Page page)
    {
        var ordered = page.Letters.Where(l => !string.IsNullOrEmpty(l.Value))
            .OrderByDescending(l => l.StartBaseLine.Y).ThenBy(l => l.StartBaseLine.X).ToList();
        var line = new List<PdfLetter>();
        foreach (var letter in ordered)
        {
            // Строка — буквы с близкой базовой линией; допуск — треть кегля.
            if (line.Count > 0 && Math.Abs(line[0].StartBaseLine.Y - letter.StartBaseLine.Y) > Math.Max(2, letter.PointSize / 3))
            {
                if (BuildLine(line, page.Width) is { } built) yield return built;
                line.Clear();
            }
            line.Add(letter);
        }
        if (BuildLine(line, page.Width) is { } last) yield return last;
    }

    private static PdfLine? BuildLine(List<PdfLetter> line, double pageWidth)
    {
        if (line.Count == 0) return null;
        var sorted = line.OrderBy(l => l.StartBaseLine.X).ToList();
        var sb = new StringBuilder();
        PdfLetter? prev = null;
        foreach (var l in sorted)
        {
            // Пробела в PDF может не быть вовсе — слова разделены только зазором.
            if (prev is not null && l.StartBaseLine.X - prev.EndBaseLine.X > Math.Max(1, l.PointSize * 0.2) &&
                !string.IsNullOrWhiteSpace(prev.Value) && !string.IsNullOrWhiteSpace(l.Value)) sb.Append(' ');
            sb.Append(l.Value);
            prev = l;
        }
        var text = Clean(sb.ToString());
        if (text.Length == 0) return null;
        var visible = sorted.Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
        var bold = visible.Count > 0 &&
                   visible.Count(l => l.FontDetails?.IsBold == true || l.FontName?.Contains("Bold", StringComparison.OrdinalIgnoreCase) == true) >= visible.Count * 0.8;
        var left = sorted[0].StartBaseLine.X;
        var right = sorted[^1].EndBaseLine.X;
        var centered = left > pageWidth * 0.12 && Math.Abs((left + right) / 2 - pageWidth / 2) < pageWidth * 0.05;
        return new PdfLine(text, bold, centered);
    }

    public static string Clean(string s) => Regex.Replace(s, @"\s+", " ").Trim();
}
