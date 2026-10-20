using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace RpdFos.Core.DocumentTypes;

/// <summary>
/// Базовое имя закладки DOCX-шаблона и полные имена её вхождений по порядку в документе:
/// по полному имени сопоставление переопределяет форму слова для отдельного вхождения.
/// </summary>
public sealed record BookmarkGroup(string BaseName, IReadOnlyList<string> Names)
{
    public int Count => Names.Count;
}

/// <summary>
/// Закладки автозаполнения DOCX-шаблона оформления: <c>Autofill&lt;Поле&gt;&lt;N&gt;</c>. Одно значение
/// подставляется во все одноимённые закладки (fan-out), поэтому номер N отбрасывается — любой длины:
/// у генератора-предшественника отбрасывался один символ, и номер больше 9 ломал сопоставление.
/// </summary>
public static partial class DocxBookmarks
{
    public const string Prefix = "Autofill";

    [GeneratedRegex(@"\d+$")]
    private static partial Regex NumericSuffix();

    /// <summary>Базовое имя закладки: без числового суффикса.</summary>
    public static string BaseName(string bookmarkName) => NumericSuffix().Replace(bookmarkName, "");

    /// <summary>Закладки загруженного файла; null — файл не читается как документ Word.</summary>
    public static IReadOnlyList<BookmarkGroup>? TryRead(byte[] docx)
    {
        try
        {
            return Read(docx);
        }
        // Не ZIP, ZIP без документа Word, повреждённые части — ошибка данных пользователя, а не сервера.
        catch (Exception e) when (e is OpenXmlPackageException or InvalidDataException or FileFormatException or IOException)
        {
            return null;
        }
    }

    /// <summary>Базовые имена закладок с префиксом Autofill по порядку первого появления в документе.</summary>
    public static IReadOnlyList<BookmarkGroup> Read(byte[] docx)
    {
        using var document = WordprocessingDocument.Open(new MemoryStream(docx), false);
        var main = document.MainDocumentPart ?? throw new InvalidDataException("В документе нет основной части.");

        // Закладки бывают и в колонтитулах и сносках — генератор заполняет их так же.
        var roots = new List<OpenXmlElement?> { main.Document?.Body };
        roots.AddRange(main.HeaderParts.Select(p => (OpenXmlElement?)p.Header));
        roots.AddRange(main.FooterParts.Select(p => (OpenXmlElement?)p.Footer));
        roots.Add(main.FootnotesPart?.Footnotes);
        roots.Add(main.EndnotesPart?.Endnotes);

        return roots.OfType<OpenXmlElement>()
            .SelectMany(r => r.Descendants<BookmarkStart>())
            .Select(b => b.Name?.Value ?? "")
            .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal))
            .GroupBy(BaseName)
            .Select(g => new BookmarkGroup(g.Key, g.ToList()))
            .ToList();
    }
}
