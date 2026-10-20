using Ganss.Xss;

namespace RpdFos.Core.Documents;

/// <summary>
/// Санитайзер форматированного текста документа (NFR-SEC-10) — только на сервере, клиенту не доверяем.
/// Белый список: абзацы, перенос строки, полужирный, курсив, маркированный и нумерованный списки — без атрибутов,
/// стилей и ссылок. Остальная разметка снимается, текст внутри неё сохраняется (вставка из Word не теряет слов);
/// содержимое исполняемых и служебных элементов удаляется целиком.
/// </summary>
public sealed class RichTextSanitizer
{
    public static readonly IReadOnlySet<string> AllowedTags =
        new HashSet<string> { "p", "br", "strong", "b", "em", "i", "ul", "ol", "li" };

    // Их содержимое — не текст документа: код, стили, встроенные объекты.
    private static readonly HashSet<string> DroppedWithContent =
        ["script", "style", "template", "noscript", "iframe", "object", "embed", "svg", "math", "head", "title"];

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
    public string? Sanitize(string? html) => string.IsNullOrEmpty(html) ? html : _sanitizer.Sanitize(html).Trim();
}
