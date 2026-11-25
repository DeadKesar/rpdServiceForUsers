using System.Net;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Helpers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace RpdFos.Manuals;

/// <summary>Заголовок руководства: уровень 2 или 3, номер («2.1.»), текст и постоянный якорь.</summary>
public sealed record ManualHeading(int Level, string Number, string Title, string Anchor)
{
    /// <summary>Как заголовок печатается — и как он назван в закладках PDF.</summary>
    public string Text => $"{Number} {Title}";
}

/// <summary>
/// Руководство из одного файла Markdown (этап 8, B9). Якорь раздела — строка <c>&lt;a id="…"&gt;&lt;/a&gt;</c> перед заголовком: он
/// не зависит от текста заголовка, работает и на GitHub, и в PDF, и в справке «?» (B12). Номера разделов и рисунков в
/// исходном файле не пишутся — их ставит сборка.
/// </summary>
public sealed partial class Manual
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().Build();

    private readonly MarkdownDocument document;
    private readonly List<string> problems = [];

    private Manual(string path)
    {
        Path = path;
        document = Markdown.Parse(File.ReadAllText(path), Pipeline);
        Title = document.OfType<HeadingBlock>().Where(h => h.Level == 1).Select(h => TextOf(h.Inline)).FirstOrDefault() ?? "";
        if (Title.Length == 0) problems.Add($"{Name}: нет заголовка руководства (#).");
        Headings = ReadHeadings();
    }

    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string Title { get; }
    public IReadOnlyList<ManualHeading> Headings { get; }

    /// <summary>Снимки экрана — пути относительно файла руководства, по порядку.</summary>
    public IReadOnlyList<string> Images => Links(images: true).Select(l => l.Url!).ToList();

    public static Manual Load(string path) => new(path);

    public static IReadOnlyList<Manual> LoadAll(string folder) =>
        Directory.GetFiles(folder, "*.md").Order(StringComparer.Ordinal).Select(Load).ToList();

    /// <summary>
    /// Замечания ко всем руководствам: заголовок без якоря, повтор якоря, ссылка в никуда, отсутствующий или никем не
    /// показанный снимок. Пустой список — руководства можно собирать.
    /// </summary>
    public static IReadOnlyList<string> Check(string folder)
    {
        var manuals = LoadAll(folder);
        var found = manuals.SelectMany(m => m.problems).ToList();
        foreach (var manual in manuals) found.AddRange(manual.CheckLinks(manuals));

        var images = System.IO.Path.Combine(folder, "img");
        var shown = manuals.SelectMany(m => m.Images).Select(System.IO.Path.GetFileName).ToHashSet();
        if (Directory.Exists(images))
            found.AddRange(Directory.GetFiles(images).Select(System.IO.Path.GetFileName).Where(f => !shown.Contains(f))
                .Select(f => $"img/{f}: снимка нет ни в одном руководстве."));
        return found;
    }

    /// <summary>Тело руководства без заголовка первого уровня: разделы с номерами и якорями, снимки с подписями.</summary>
    /// <param name="image">Адрес снимка в странице по пути из Markdown.</param>
    public string ToHtml(Func<string, string> image)
    {
        // Подготовка меняет документ, поэтому он собирается из файла заново: вызов повторяемый.
        var copy = new Manual(Path);
        copy.Prepare(image);
        return copy.document.ToHtml(Pipeline);
    }

    private void Prepare(Func<string, string> image)
    {
        var headings = new Queue<ManualHeading>(Headings);
        var figure = 0;
        foreach (var block in document.ToList())
        {
            if (AnchorOf(block) is not null || block is HeadingBlock { Level: 1 })
            {
                document.Remove(block);
            }
            else if (block is HeadingBlock { Level: 2 or 3 } heading && headings.TryDequeue(out var info))
            {
                heading.GetAttributes().Id = info.Anchor;
                heading.Inline!.FirstChild!.InsertBefore(new LiteralInline(info.Number + " "));
            }
            else if (PictureOf(block) is { } picture)
            {
                var caption = WebUtility.HtmlEncode($"Рисунок {++figure} — {TextOf(picture)}");
                var html = $"<figure><img src=\"{WebUtility.HtmlEncode(image(picture.Url!))}\" alt=\"{caption}\">" +
                           $"<figcaption>{caption}</figcaption></figure>";
                document[document.IndexOf(block)] = new HtmlBlock(null) { Lines = new StringLineGroup(html) };
            }
        }
    }

    private List<ManualHeading> ReadHeadings()
    {
        var headings = new List<ManualHeading>();
        var anchors = new HashSet<string>();
        var (chapter, section) = (0, 0);
        string? pending = null;

        foreach (var block in document)
        {
            var anchor = AnchorOf(block);
            if (pending is not null && block is not HeadingBlock)
            {
                problems.Add($"{Name}: якорь «{pending}» стоит не перед заголовком.");
                pending = null;
            }
            if (anchor is not null) pending = anchor;
            if (block is not HeadingBlock { Level: > 1 } heading) continue;

            var title = TextOf(heading.Inline);
            if (heading.Level > 3)
            {
                problems.Add($"{Name}: «{title}» — заголовок глубже третьего уровня.");
            }
            else
            {
                (chapter, section) = heading.Level == 2 ? (chapter + 1, 0) : (chapter, section + 1);
                if (pending is null) problems.Add($"{Name}: у заголовка «{title}» нет якоря.");
                else if (!AnchorName().IsMatch(pending)) problems.Add($"{Name}: якорь «{pending}» — не строчная латиница, цифры и дефис.");
                else if (!anchors.Add(pending)) problems.Add($"{Name}: якорь «{pending}» повторяется.");
                headings.Add(new(heading.Level, heading.Level == 2 ? $"{chapter}." : $"{chapter}.{section}.", title, pending ?? ""));
            }
            pending = null;
        }
        if (pending is not null) problems.Add($"{Name}: якорь «{pending}» стоит не перед заголовком.");
        return headings;
    }

    private IEnumerable<string> CheckLinks(IReadOnlyList<Manual> manuals)
    {
        foreach (var link in Links(images: false))
        {
            var url = link.Url ?? "";
            if (url.StartsWith("http://") || url.StartsWith("https://") || url.StartsWith("mailto:")) continue;
            var (file, anchor) = url.Split('#', 2) is [var f, var a] ? (f, a) : (url, null);
            var target = file.Length == 0 ? this : manuals.FirstOrDefault(m => m.Name == file);
            if (target is null)
            {
                if (anchor is not null || !Exists(file)) yield return $"{Name}: ссылка «{url}» — нет такого файла.";
            }
            else if (anchor is not null && target.Headings.All(h => h.Anchor != anchor))
            {
                yield return $"{Name}: ссылка «{url}» — нет такого якоря.";
            }
        }
        var alone = document.Select(PictureOf).OfType<LinkInline>().ToHashSet();
        foreach (var picture in Links(images: true))
        {
            if (!Exists(picture.Url!)) yield return $"{Name}: нет снимка {picture.Url}.";
            if (TextOf(picture).Length == 0) yield return $"{Name}: у снимка {picture.Url} нет подписи.";
            if (!alone.Contains(picture)) yield return $"{Name}: снимок {picture.Url} стоит внутри текста, а не отдельным абзацем.";
        }
    }

    private bool Exists(string relative) =>
        File.Exists(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, relative));

    private IEnumerable<LinkInline> Links(bool images) => document.Descendants<LinkInline>().Where(l => l.IsImage == images);

    // Снимок — абзац верхнего уровня, в котором больше ничего нет.
    private static LinkInline? PictureOf(Block block) =>
        block is ParagraphBlock { Inline: { } inline } && inline.ToList() is [LinkInline { IsImage: true } picture] ? picture : null;

    // Строка из одного якоря: <a id="…"></a> — открывающий и закрывающий теги, больше в абзаце ничего нет.
    private static string? AnchorOf(Block block) =>
        block is ParagraphBlock { Inline: { } inline } && inline.ToList() is [HtmlInline open, HtmlInline { Tag: "</a>" }] &&
        AnchorTag().Match(open.Tag) is { Success: true } match
            ? match.Groups[1].Value
            : null;

    private static string TextOf(ContainerInline? inline) => inline is null ? "" : string.Concat(inline.Descendants()
        .Select(i => i switch
        {
            LiteralInline literal => literal.Content.ToString(),
            CodeInline code => code.Content,
            LineBreakInline => " ",
            _ => ""
        }));

    [GeneratedRegex("^<a id=\"([^\"]*)\">$")]
    private static partial Regex AnchorTag();

    [GeneratedRegex("^[a-z][a-z0-9-]*$")]
    private static partial Regex AnchorName();
}
