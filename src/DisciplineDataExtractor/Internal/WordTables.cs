using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace DisciplineDataExtractor.Internal;

/// <summary>Чтение таблиц Word: текст абзацев и ячейки по колонкам сетки с учётом объединения.</summary>
internal static class WordTables
{
    public static Body? BodyOf(WordprocessingDocument doc) => doc.MainDocumentPart?.Document?.Body;

    /// <summary>Таблицы документа по порядку (вложенные таблицы не учитываются).</summary>
    public static IReadOnlyList<Table> Tables(Body body) => body.Elements<Table>().ToList();

    /// <summary>Текст элемента: табуляции и переносы строк — пробелы (InnerText их теряет и склеивает слова).</summary>
    public static string TextOf(OpenXmlElement element)
    {
        var sb = new StringBuilder();
        foreach (var e in element.Descendants())
        {
            switch (e)
            {
                case Text t: sb.Append(t.Text); break;
                case TabChar or Break or CarriageReturn: sb.Append(' '); break;
            }
        }
        return TextRules.Clean(sb.ToString());
    }

    /// <summary>Непустые абзацы ячейки.</summary>
    public static IReadOnlyList<string> Paragraphs(TableCell cell) =>
        cell.Elements<Paragraph>().Select(TextOf).Where(s => s.Length > 0).ToList();

    /// <summary>Текст ячейки: абзацы через пробел (перенос в ячейке — тоже граница слова).</summary>
    public static string CellText(TableCell cell) => string.Join(' ', Paragraphs(cell));

    /// <summary>
    /// Ячейки строки по колонкам сетки: объединённая по горизонтали ячейка занимает несколько колонок,
    /// значение — в первой из них, остальные — null.
    /// </summary>
    public static IReadOnlyList<TableCell?> GridCells(TableRow row)
    {
        var result = new List<TableCell?>();
        foreach (var cell in row.Elements<TableCell>())
        {
            result.Add(cell);
            var span = cell.TableCellProperties?.GridSpan?.Val?.Value ?? 1;
            for (var i = 1; i < span; i++) result.Add(null);
        }
        return result;
    }

    /// <summary>Строка — заголовок раздела: одна ячейка на всю ширину («Обязательная часть»).</summary>
    public static bool IsSectionRow(TableRow row) => row.Elements<TableCell>().Count() == 1;
}
