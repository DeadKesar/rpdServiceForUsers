namespace RpdFos.Core.Printing;

/// <summary>Как клетка сетки связана с клетками выше: сама начинает объединение, продолжает его или не объединена.</summary>
public enum MergeKind { None, Restart, Continue }

/// <summary>
/// Клетка сетки печатной таблицы. <see cref="Cell"/> — что в ней печатать; у продолжения объединения по вертикали
/// это ячейка, с которой оно началось (её текст уже напечатан выше), у недостающего места строки — null.
/// </summary>
public sealed record PlacedCell(PrintTableCell? Cell, int Column, int Span, MergeKind Merge);

/// <summary>
/// Сетка таблицы: ячейки HTML с <c>colspan</c> и <c>rowspan</c> разложены по столбцам так, как их хранит DOCX — в
/// каждой строке клетки покрывают все столбцы по одному разу; объединённое по вертикали продолжается клеткой с
/// <see cref="MergeKind.Continue"/>.
/// </summary>
public sealed class PrintTableGrid
{
    /// <summary>Больше столбцов в таблице Word не бывает; шире — таблицу печатают строками текста.</summary>
    public const int MaxColumns = 63;

    private PrintTableGrid(int columns, IReadOnlyList<IReadOnlyList<PlacedCell>> rows)
    {
        Columns = columns;
        Rows = rows;
    }

    public int Columns { get; }

    /// <summary>Клетки каждой строки по возрастанию столбца.</summary>
    public IReadOnlyList<IReadOnlyList<PlacedCell>> Rows { get; }

    /// <summary>Сетка таблицы; null — таблицы нет (нет строк) или она шире <see cref="MaxColumns"/>.</summary>
    public static PrintTableGrid? Build(PrintTable table)
    {
        var source = table.Rows;
        // Занятые места: хватает пары (строка, столбец), число мест не больше строк на MaxColumns.
        var occupied = new HashSet<(int Row, int Column)>();
        var rows = source.Select(_ => new List<PlacedCell>()).ToList();
        var columns = 0;

        for (var r = 0; r < source.Count; r++)
        {
            var column = 0;
            foreach (var cell in source[r].Cells)
            {
                while (occupied.Contains((r, column))) column++;
                // Ячейка не заходит на места, занятые ячейками сверху: в HTML такое пересечение — ошибка разметки,
                // а в DOCX его нет.
                var span = 1;
                while (span < Math.Min(cell.ColSpan, MaxColumns + 1) && !occupied.Contains((r, column + span))) span++;
                if (column + span > MaxColumns) return null;

                // Строк под ячейкой не больше, чем осталось в таблице.
                var down = Math.Clamp(cell.RowSpan, 1, source.Count - r);
                rows[r].Add(new PlacedCell(cell, column, span, down > 1 ? MergeKind.Restart : MergeKind.None));
                for (var d = 0; d < down; d++)
                {
                    for (var c = 0; c < span; c++) occupied.Add((r + d, column + c));
                    if (d > 0) rows[r + d].Add(new PlacedCell(cell, column, span, MergeKind.Continue));
                }
                column += span;
                columns = Math.Max(columns, column);
            }
        }
        if (columns == 0) return null;

        // Строка короче таблицы — недостающие места пустыми ячейками: строка DOCX покрывает всю сетку.
        var result = new List<IReadOnlyList<PlacedCell>>();
        for (var r = 0; r < rows.Count; r++)
        {
            for (var c = 0; c < columns; c++)
                if (!occupied.Contains((r, c))) rows[r].Add(new PlacedCell(null, c, 1, MergeKind.None));
            result.Add(rows[r].OrderBy(p => p.Column).ToList());
        }
        return new PrintTableGrid(columns, result);
    }
}
