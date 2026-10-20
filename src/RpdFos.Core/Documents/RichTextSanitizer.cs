using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Ganss.Xss;

namespace RpdFos.Core.Documents;

/// <summary>
/// Санитайзер форматированного текста документа (NFR-SEC-10) — только на сервере, клиенту не доверяем.
/// Белый список: абзацы, перенос строки, полужирный, курсив, маркированный и нумерованный списки — без атрибутов,
/// стилей и ссылок. Остальная разметка снимается, текст внутри неё сохраняется (вставка из Word не теряет слов);
/// содержимое исполняемых и служебных элементов удаляется целиком.
/// </summary>
public sealed partial class RichTextSanitizer
{
    public static readonly IReadOnlySet<string> AllowedTags =
        new HashSet<string> { "p", "br", "strong", "b", "em", "i", "ul", "ol", "li" };

    // Их содержимое — не текст документа: код, стили, встроенные объекты.
    private static readonly HashSet<string> DroppedWithContent =
        ["script", "style", "template", "noscript", "iframe", "object", "embed", "svg", "math", "head", "title"];

    [GeneratedRegex(@"font-weight\s*:\s*(bold|bolder|[6-9]00)", RegexOptions.IgnoreCase)]
    private static partial Regex BoldStyle();

    [GeneratedRegex(@"font-style\s*:\s*(italic|oblique)", RegexOptions.IgnoreCase)]
    private static partial Regex ItalicStyle();

    private readonly HtmlSanitizer _sanitizer;

    public RichTextSanitizer()
    {
        _sanitizer = new HtmlSanitizer();
        _sanitizer.AllowedTags.Clear();
        _sanitizer.AllowedTags.UnionWith(AllowedTags);
        _sanitizer.AllowedAttributes.Clear();
        _sanitizer.AllowedCssProperties.Clear();
        _sanitizer.AllowedAtRules.Clear();
        _sanitizer.AllowedClasses.Clear();
        _sanitizer.AllowedSchemes.Clear();
        _sanitizer.UriAttributes.Clear();
        _sanitizer.KeepChildNodes = true;
        _sanitizer.RemovingTag += (_, e) =>
        {
            if (DroppedWithContent.Contains(e.Tag.LocalName)) e.Tag.InnerHtml = "";
        };
    }

    /// <summary>Безопасная разметка из присланной клиентом; null и пустая строка — как есть.</summary>
    public string? Sanitize(string? html) =>
        string.IsNullOrEmpty(html) ? html : _sanitizer.Sanitize(StylesToTags(html)).Trim();

    /// <summary>
    /// Полужирный и курсив, заданные стилем, — в теги. Редактор Radzen (execCommand браузера) оформляет их
    /// <c>&lt;span style="font-weight: bold;"&gt;</c>; без перевода санитайзер снял бы span со стилем, и
    /// форматирование молча пропало бы. Стиль вне белого списка дальше всё равно снимается.
    /// </summary>
    private static string StylesToTags(string html)
    {
        if (!html.Contains("style", StringComparison.OrdinalIgnoreCase)) return html;
        var document = new HtmlParser().ParseDocument("");
        var body = document.Body!;
        body.InnerHtml = html;
        foreach (var element in body.QuerySelectorAll("[style]").ToList())
        {
            var style = element.GetAttribute("style") ?? "";
            if (BoldStyle().IsMatch(style)) Wrap(element, "strong");
            if (ItalicStyle().IsMatch(style)) Wrap(element, "em");
        }
        return body.InnerHtml;

        void Wrap(IElement element, string tag)
        {
            var wrapper = document.CreateElement(tag);
            while (element.FirstChild is { } child) wrapper.AppendChild(child);
            element.AppendChild(wrapper);
        }
    }
}
