using System.Globalization;
using ClosedXML.Excel;

namespace DisciplineDataExtractor.Internal;

/// <summary>Чтение ячеек Excel: числа в планах бывают и числами, и текстом.</summary>
internal static class ExcelCells
{
    public static string Text(IXLCell cell) =>
        cell.Value.IsNumber
            ? cell.Value.GetNumber().ToString(CultureInfo.InvariantCulture)
            : TextRules.Clean(cell.GetString());

    /// <summary>Пустая ячейка — 0. Нечисловое значение — 0 и ошибка с привязкой к ячейке.</summary>
    public static int Int(IXLCell cell, IssueLog log, string file, string what)
    {
        if (cell.Value.IsBlank) return 0;

        double value;
        if (cell.Value.IsNumber)
            value = cell.Value.GetNumber();
        else
        {
            var text = TextRules.Clean(cell.GetString()).Replace(',', '.');
            if (text.Length == 0) return 0;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                log.Error(file, cell.Worksheet.Name, cell.Address.RowNumber,
                    $"{what}: нечисловое значение «{text}» в ячейке {cell.Address}");
                return 0;
            }
        }

        var rounded = (int)Math.Round(value, MidpointRounding.AwayFromZero);
        if (Math.Abs(value - rounded) > 1e-9)
            log.Warning(file, cell.Worksheet.Name, cell.Address.RowNumber,
                $"{what}: дробное значение {value.ToString(CultureInfo.InvariantCulture)} в ячейке {cell.Address} округлено до {rounded}");
        return rounded;
    }

    /// <summary>Номера колонок, которые занимает ячейка заголовка (с учётом объединения).</summary>
    public static (int First, int Last) Span(IXLCell cell)
    {
        var merged = cell.MergedRange();
        return merged is null
            ? (cell.Address.ColumnNumber, cell.Address.ColumnNumber)
            : (merged.RangeAddress.FirstAddress.ColumnNumber, merged.RangeAddress.LastAddress.ColumnNumber);
    }

    /// <summary>Первая непустая ячейка правее заданной в той же строке.</summary>
    public static IXLCell? RightOf(IXLCell cell) =>
        cell.WorksheetRow().CellsUsed()
            .Where(c => c.Address.ColumnNumber > Span(cell).Last && Text(c).Length > 0)
            .FirstOrDefault();
}
