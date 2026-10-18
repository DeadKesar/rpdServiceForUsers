using ClosedXML.Excel;

namespace DisciplineDataExtractor.Internal;

/// <summary>
/// Должности сотрудников (.xlsx, лист «Сотрудники»): номер, официальное название подразделения,
/// текст для документа, должность, ФИО, институт. Строки без ФИО (вакансии) и шаблонные строки
/// для направлений («отображение», «должность», «Фио из файла») — не сотрудники и пропускаются.
/// </summary>
internal sealed class EmployeesReader(SourceFile file, IssueLog log)
{
    private const string PositionPlaceholder = "должность", NamePlaceholder = "фио из файла";

    private string F => file.FileName;

    public IReadOnlyList<Employee> Read()
    {
        using var workbook = new XLWorkbook(new MemoryStream(file.Content));
        var ws = workbook.Worksheets.FirstOrDefault(s => s.Name.StartsWith("Сотрудники", StringComparison.OrdinalIgnoreCase));
        if (ws is null)
        {
            ws = workbook.Worksheets.First();
            log.Warning(F, ws.Name, null, "Нет листа «Сотрудники» — читается первый лист");
        }

        var header = ws.RowsUsed().Take(10).FirstOrDefault(r =>
            r.CellsUsed().Any(c => TextRules.HeaderKey(c.GetString()) == "фио"));
        if (header is null)
        {
            log.Error(F, ws.Name, null, "Не найдена строка заголовков с колонкой «ФИО»");
            return [];
        }

        int? Column(Func<string, bool> match) =>
            header.CellsUsed().FirstOrDefault(c => match(TextRules.HeaderKey(c.GetString())))?.Address.ColumnNumber;
        var numberCol = Column(k => k is "номер" or "№");
        var officialCol = Column(k => k.StartsWith("офиц"));
        var documentCol = Column(k => k.Contains("документ"));
        var positionCol = Column(k => k == "должность");
        var nameCol = Column(k => k == "фио")!.Value;
        var instituteCol = Column(k => k.StartsWith("институт"));
        if (officialCol is null)
        {
            log.Error(F, ws.Name, header.RowNumber(), "Нет колонки «Офиц. название»");
            return [];
        }

        string? Value(IXLRow row, int? col) =>
            col is int c && ExcelCells.Text(row.Cell(c)) is { Length: > 0 } text ? text : null;

        var result = new List<Employee>();
        foreach (var row in ws.RowsUsed().Where(r => r.RowNumber() > header.RowNumber()))
        {
            // Строки без номера — примечания под таблицей.
            if (numberCol is int nc && !int.TryParse(ExcelCells.Text(row.Cell(nc)), out _)) continue;

            var number = row.RowNumber();
            var official = Value(row, officialCol);
            var fullName = Value(row, nameCol);
            var position = Value(row, positionCol);
            if (official is null) continue;
            if (position == PositionPlaceholder || string.Equals(fullName, NamePlaceholder, StringComparison.OrdinalIgnoreCase))
                continue; // шаблонная строка, а не сотрудник
            if (fullName is null)
            {
                if (position is not null && position != PositionPlaceholder)
                    log.Warning(F, ws.Name, number, $"«{official}»: не указано ФИО — строка пропущена");
                continue;
            }
            if (position is null)
                log.Warning(F, ws.Name, number, $"«{official}»: не указана должность");

            // Институт в колонке — только для подразделений институтов; у руководства вуза там «УПР».
            var institute = Value(row, instituteCol) is { } i && i.StartsWith("Институт", StringComparison.OrdinalIgnoreCase) ? i : null;
            // Кафедра — подразделение внутри института, а не сам институт.
            var department = institute is not null && !official.StartsWith("Институт", StringComparison.OrdinalIgnoreCase)
                ? official
                : null;

            result.Add(new Employee(fullName, position, Value(row, documentCol), official, department, institute));
        }

        if (result.Count == 0)
            log.Error(F, ws.Name, null, "Не найдено ни одного сотрудника");
        return result;
    }
}
