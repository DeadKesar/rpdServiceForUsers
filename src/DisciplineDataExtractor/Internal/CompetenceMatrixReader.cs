using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace DisciplineDataExtractor.Internal;

/// <summary>Строка матрицы: дисциплина по названию (индекса в матрице нет) и отмеченные коды.</summary>
internal sealed record MatrixRow(string Sheet, int Row, string Name, string Semesters, IReadOnlyList<CompetenceCode> Codes);

/// <summary>
/// Матрица компетенций (.docx): таблицы «Наименование дисциплины — Семестры — коды компетенций».
/// Отметка в ячейке («+») — связь. Колонки с кодом индикатора («УК-1.1») дают связь с индикатором.
/// </summary>
internal sealed class CompetenceMatrixReader(SourceFile file, IssueLog log)
{
    private string F => file.FileName;

    public (string? DirectionCode, IReadOnlyList<MatrixRow> Rows) Read()
    {
        using var doc = WordprocessingDocument.Open(new MemoryStream(file.Content), false);
        if (WordTables.BodyOf(doc) is not { } body)
        {
            log.Error(F, null, null, "В документе нет содержимого");
            return (null, []);
        }

        var direction = TextRules.DirectionCode().Match(WordTables.TextOf(body));
        var result = new List<MatrixRow>();
        var tables = WordTables.Tables(body);
        for (var t = 0; t < tables.Count; t++)
        {
            var sheet = $"Таблица {t + 1}";
            var rows = tables[t].Elements<TableRow>().ToList();

            // Строка заголовков — первая из начальных строк, где есть коды компетенций.
            var headerIndex = -1;
            int? semesterColumn = null;
            var codeColumns = new List<(int Column, CompetenceCode Code)>();
            for (var r = 0; r < Math.Min(3, rows.Count) && headerIndex < 0; r++)
            {
                var grid = WordTables.GridCells(rows[r]);
                for (var c = 0; c < grid.Count; c++)
                {
                    var text = grid[c] is { } cell ? WordTables.CellText(cell) : "";
                    if (TextRules.HeaderKey(text).StartsWith("семестр")) semesterColumn = c;
                    else if (TextRules.TryParseCode(text, out var code, out var tail) && tail.Length == 0)
                        codeColumns.Add((c, code));
                }
                if (codeColumns.Count > 0) headerIndex = r;
                else semesterColumn = null;
            }
            if (headerIndex < 0) continue; // не таблица матрицы

            for (var r = headerIndex + 1; r < rows.Count; r++)
            {
                if (WordTables.IsSectionRow(rows[r])) continue; // «Обязательная часть», «Практика»…
                var grid = WordTables.GridCells(rows[r]);
                var name = grid.Count > 0 && grid[0] is { } first ? WordTables.CellText(first) : "";
                if (name.Length == 0) continue;

                var semesters = semesterColumn is int sc && sc < grid.Count && grid[sc] is { } s ? WordTables.CellText(s) : "";
                var codes = codeColumns
                    .Where(x => x.Column < grid.Count && grid[x.Column] is { } cell && IsMark(WordTables.CellText(cell)))
                    .Select(x => x.Code)
                    .ToList();
                result.Add(new MatrixRow(sheet, r + 1, name, semesters, codes));
            }
        }

        if (result.Count == 0)
            log.Error(F, null, null, "Не найдено ни одной таблицы матрицы (заголовки с кодами компетенций)");
        return (direction.Success ? direction.Value : null, result);
    }

    private static bool IsMark(string text) => text.Length > 0 && text is not ("-" or "–" or "—" or "0");
}
