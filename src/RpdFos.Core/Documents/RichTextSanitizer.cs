using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Ganss.Xss;

namespace RpdFos.Core.Documents;

/// <summary>
/// Санитайзер форматированного текста документа (NFR-SEC-10) — только на сервере, клиенту не доверяем.
/// Белый список: абзацы, перенос строки, полужирный, курсив, маркированный и нумерованный списки, таблицы
/// (строки, ячейки, объединение ячеек) — без стилей, ссылок и прочих атрибутов. Остальная разметка снимается,
/// текст внутри неё сохраняется (вставка из Word не теряет слов); содержимое исполняемых и служебных элементов
/// удаляется целиком.
/// </summary>
public sealed partial class RichTextSanitizer
{
    public static readonly IReadOnlySet<string> AllowedTags = new HashSet<string>
    {
        "p", "br", "strong", "b", "em", "i", "ul", "ol", "li",
        // Группы строк — не ради оформления: разбор HTML (браузер, редактор) создаёт их сам, и без них структура
        // таблицы после повторного разбора менялась бы.
        "table", "thead", "tbody", "tfoot", "tr", "th", "td"
    };

    /// <summary>
    /// Наибольшее число столбцов или строк, которое занимает ячейка. Больше — не таблица, а ошибка: печать
    /// строит сетку по этим числам, а в Word не больше 63 столбцов.
    /// </summary>
    public const int MaxSpan = 100;

    private static readonly string[] SpanAttributes = ["colspan", "rowspan"];

    // Их содержимое — не текст документа: код, стили, встроенные объекты.
    private static readonly HashSet<string> DroppedWithContent =
        ["script", "style", "template", "noscript", "iframe", "object", "embed", "svg", "math", "head", "title"];

    [GeneratedRegex(@"font-weight\s*:\s*(bold|bolder|[6-9]00)", RegexOptions.IgnoreCase)]
    private static partial Regex BoldStyle();

    [GeneratedRegex(@"font-style\s*:\s*(italic|oblique)", RegexOptions.IgnoreCase)]
    private static partial Regex ItalicStyle();

    // Только цифры ASCII: «٢» или «2;x» — не число для объединения ячеек.
    [GeneratedRegex("^[0-9]{1,3}$")]
    private static partial Regex Digits();

    private readonly HtmlSanitizer _sanitizer;

    public RichTextSanitizer()
    {
        _sanitizer = new HtmlSanitizer();
        _sanitizer.AllowedTags.Clear();
        _sanitizer.AllowedTags.UnionWith(AllowedTags);
        _sanitizer.AllowedAttributes.Clear();
        // Список атрибутов общий для всех элементов; «только у ячеек и только числом» проверяет Tables.
        _sanitizer.AllowedAttributes.UnionWith(SpanAttributes);
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
        string.IsNullOrEmpty(html) ? html : Tables(_sanitizer.Sanitize(StylesToTags(html))).Trim();

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

    /// <summary>
    /// Таблицы после белого списка: объединение ячеек — только у <c>td</c>/<c>th</c> и только числом от 2 до
    /// <see cref="MaxSpan"/> (иное — снимается: ячейка занимает одно место), вложенная таблица — текстом. Печать и
    /// редактор рассчитывают на одноуровневую таблицу; слова вложенной остаются в ячейке внешней.
    /// </summary>
    private static string Tables(string html)
    {
        if (!html.Contains("<t", StringComparison.OrdinalIgnoreCase) && !html.Contains("span=", StringComparison.OrdinalIgnoreCase))
            return html;
        var body = new HtmlParser().ParseDocument("").Body!;
        body.InnerHtml = html;

        // Сначала самые глубокие: у внешней таблицы к тому времени останутся только свои строки.
        var nested = body.QuerySelectorAll("td table, th table").ToList();
        for (var i = nested.Count - 1; i >= 0; i--) Flatten(nested[i]);

        foreach (var space in body.QuerySelectorAll("table, thead, tbody, tfoot, tr, td, th").SelectMany(e => e.ChildNodes)
                     .OfType<IText>().Where(IsLayoutSpace).ToList())
            space.Remove();

        foreach (var element in body.QuerySelectorAll("[colspan], [rowspan]").ToList())
            foreach (var name in SpanAttributes)
            {
                if (element.GetAttribute(name) is not { } value) continue;
                if (element.LocalName is "td" or "th" && Digits().IsMatch(value) &&
                    int.Parse(value) is var span && span is > 1 and <= MaxSpan)
                    element.SetAttribute(name, span.ToString());
                else element.RemoveAttribute(name);
            }
        return body.InnerHtml;
    }

    // Ячейки вложенной таблицы — подряд через пробел, строки и текст вокруг — через перенос: слова не склеиваются.
    private static void Flatten(IElement table)
    {
        var parent = table.ParentElement!;
        var owner = table.Owner!;
        if (Neighbor(table, previous: true) is { } before && before is not IElement { LocalName: "br" })
            parent.InsertBefore(owner.CreateElement("br"), table);
        var after = Neighbor(table, previous: false);

        var rows = table.QuerySelectorAll("tr").ToList();
        for (var r = 0; r < rows.Count; r++)
        {
            if (r > 0)
            {
                TrimTail();
                parent.InsertBefore(owner.CreateElement("br"), table);
            }
            var cells = rows[r].Children.Where(c => c.LocalName is "td" or "th").ToList();
            for (var c = 0; c < cells.Count; c++)
            {
                if (c > 0) Space();
                foreach (var child in cells[c].ChildNodes.ToList())
                    if (child is IElement { LocalName: "p" } paragraph)
                    {
                        // Абзац внутри абзаца не бывает: слова абзаца ячейки — просто текст строки.
                        while (paragraph.FirstChild is { } word) parent.InsertBefore(word, table);
                        Space();
                    }
                    else parent.InsertBefore(child, table);
            }
        }
        TrimTail();
        if (after is not null and not IElement { LocalName: "br" }) parent.InsertBefore(owner.CreateElement("br"), table);
        table.Remove();

        // Пробел не нужен, если слова уже разделены.
        void Space()
        {
            if (table.PreviousSibling is IText { Data: var text } && text.EndsWith(' ')) return;
            parent.InsertBefore(owner.CreateTextNode(" "), table);
        }

        // Пробел в конце строки — след разделителя, который не понадобился.
        void TrimTail()
        {
            if (table.PreviousSibling is IText { Data: var text } last && text.EndsWith(' ')) last.Data = text.TrimEnd();
        }
    }

    // Отступы и переводы строк между строками и ячейками, по краям ячейки и между её абзацами (так выглядит таблица
    // из Word) — не текст. Пробел между словами внутри ячейки остаётся, как и неразрывный пробел: редактор кладёт его
    // в пустую ячейку, чтобы в неё можно было встать курсором.
    private static bool IsLayoutSpace(IText text) =>
        text.Data.All(c => c is ' ' or '\t' or '\r' or '\n' or '\f') &&
        (text.ParentElement is { LocalName: "table" or "thead" or "tbody" or "tfoot" or "tr" } ||
         text.PreviousSibling is null or IElement { LocalName: "p" or "ul" or "ol" } ||
         text.NextSibling is null or IElement { LocalName: "p" or "ul" or "ol" });

    // Соседний с таблицей узел, не считая пустых строк между разметкой.
    private static INode? Neighbor(IElement table, bool previous)
    {
        var node = previous ? table.PreviousSibling : table.NextSibling;
        while (node is IText { Data: var text } && string.IsNullOrWhiteSpace(text))
            node = previous ? node.PreviousSibling : node.NextSibling;
        return node;
    }
}
