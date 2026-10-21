using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;
using RpdFos.Core.Documents;

namespace RpdFos.Core.Printing;

/// <summary>
/// Построители таблиц DOCX (цели вида <c>table</c>): таблица шаблона оформления остаётся — с её шапкой, ширинами
/// и оформлением; строки данных строятся по образцу строки шаблона. Раздел, не предусмотренный учебным планом,
/// печатается стандартной формулировкой вместо таблицы (<c>x-stub-text</c>, FR-TPL-14).
/// </summary>
internal static class DocxTables
{
    public static void Build(string builder, Table table, IReadOnlyList<OpenXmlElement> range, PrintContext context,
        List<string> warnings, List<string> unfilled, string bookmark)
    {
        switch (builder)
        {
            case "competences":
                Competences(table, context, unfilled, bookmark);
                break;
            case "laboriousness":
                Laboriousness(table, context, warnings, bookmark);
                break;
            case "discipline_partition":
                Partition(table, context, unfilled, bookmark);
                break;
            case "lab_works" or "practical_classes":
                Classes(table, builder, context, unfilled, bookmark);
                break;
            default:
                warnings.Add($"Закладка {bookmark}: построитель таблицы «{builder}» ещё не реализован.");
                break;
        }
    }

    // Компетенции и выбранные индикаторы: «УК-1. Формулировка» | «УК-1.1. Текст индикатора» по абзацу на индикатор.
    private static void Competences(Table table, PrintContext context, List<string> unfilled, string bookmark)
    {
        var header = table.Elements<TableRow>().First();
        var competences = Items(context.At("/competences"));
        if (competences.Count == 0) unfilled.Add($"{bookmark} — компетенции не выбраны");
        foreach (var competence in competences)
        {
            var row = DataRow(header);
            var cells = row.Elements<TableCell>().ToList();
            SetCell(cells[0], [$"{Text(competence, "code")}. {Text(competence, "formulation")}"], bold: false, align: JustificationValues.Left);
            if (cells.Count > 1)
                SetCell(cells[1], Items(Property(competence, "indicators")).Select(i => $"{Text(i, "code")}. {Text(i, "text")}"),
                    bold: false, align: JustificationValues.Left);
            table.AppendChild(row);
        }
    }

    /// <summary>
    /// Трудоёмкость по видам работы: столбец на семестр и «всего». Строки шаблона: общая трудоёмкость, контактная
    /// работа, лекции, практические, лабораторные, промежуточный контроль, самостоятельная работа, перечень видов
    /// самостоятельной работы, вид промежуточного контроля.
    /// </summary>
    private static void Laboriousness(Table table, PrintContext context, List<string> warnings, string bookmark)
    {
        var rows = table.Elements<TableRow>().ToList();
        var semesters = context.Facts.Semesters;
        if (rows.Count < 11 || semesters.Count == 0)
        {
            warnings.Add($"Закладка {bookmark}: таблица трудоёмкости не той формы или в плане нет семестров — не заполнена.");
            return;
        }

        var columns = semesters.Select(s => new
            {
                Title = $"{s.Semester} семестр", Total = s.ContactHours + s.SelfStudy + s.Control, s.ContactHours, s.Lectures, s.Practice,
                s.LabHours, s.Control, s.SelfStudy, Attestation = string.Join(", ", s.ControlTypes.Select(PlanTitles.ControlType))
            })
            .ToList();
        columns.Add(new
        {
            Title = "всего", Total = columns.Sum(c => c.Total), ContactHours = columns.Sum(c => c.ContactHours),
            Lectures = columns.Sum(c => c.Lectures), Practice = columns.Sum(c => c.Practice), LabHours = columns.Sum(c => c.LabHours),
            Control = columns.Sum(c => c.Control), SelfStudy = columns.Sum(c => c.SelfStudy),
            Attestation = string.Join(", ", semesters.SelectMany(s => s.ControlTypes).Distinct().Select(PlanTitles.ControlType))
        });

        // Ширина значений — как у столбца подписей, поровну на столбцы.
        var grid = table.GetFirstChild<TableGrid>()!;
        var labelWidth = int.TryParse(grid.Elements<GridColumn>().First().Width?.Value, out var w) ? w : 4927;
        var width = labelWidth / columns.Count;
        foreach (var _ in columns) grid.AppendChild(new GridColumn { Width = width.ToString() });

        var head = NewCell(rows[0], width * columns.Count);
        head.TableCellProperties!.GridSpan = new GridSpan { Val = columns.Count };
        SetCell(head, ["Трудоёмкость, академических часов"], bold: null, align: JustificationValues.Center);
        rows[0].AppendChild(head);

        foreach (var c in columns)
        {
            Append(1, c.Title);
            Append(2, c.Total.ToString());
            Append(3, c.ContactHours.ToString());
            Append(4, c.Lectures.ToString());
            Append(5, c.Practice.ToString());
            Append(6, c.LabHours.ToString());
            Append(7, c.Control.ToString());
            Append(8, c.SelfStudy.ToString());
            Append(9, "");
            Append(10, c.Attestation);
        }

        void Append(int row, string text)
        {
            var cell = NewCell(rows[row], width);
            SetCell(cell, [text], bold: null, align: JustificationValues.Center);
            rows[row].AppendChild(cell);
        }
    }

    /// <summary>
    /// Разделы дисциплины по тематическому плану: номер, тема, семестр (если он один), неделя, часы по видам занятий,
    /// формируемые индикаторы, формы текущего контроля; затем «Итого» и «Промежуточный контроль».
    /// </summary>
    private static void Partition(Table table, PrintContext context, List<string> unfilled, string bookmark)
    {
        var rows = table.Elements<TableRow>().ToList();
        var prototype = rows[Math.Min(2, rows.Count - 1)];
        var topics = Items(context.At("/thematic_plan"));
        if (topics.Count == 0) unfilled.Add($"{bookmark} — тематический план не заполнен");
        var semester = context.Facts.Semesters.Count == 1 ? context.Facts.Semesters[0].Semester.ToString() : "";

        foreach (var topic in topics)
        {
            var hours = Property(topic, "hours");
            Add(false, Number(topic, "section_number"), Text(topic, "topic_title") ?? "", semester, "",
                Number(hours, "lectures"), Number(hours, "lab_hours"), Number(hours, "practice"), Number(hours, "self_study"),
                string.Join(", ", Items(Property(topic, "competence_indicators")).Select(i => i.GetString())),
                string.Join(", ", Items(Property(topic, "current_control_forms")).Select(i => i.GetString())));
        }
        int Sum(string key) => topics.Sum(t => int.TryParse(Number(Property(t, "hours"), key), out var n) ? n : 0);
        Add(true, "", "Итого", "", "", Sum("lectures").ToString(), Sum("lab_hours").ToString(), Sum("practice").ToString(),
            Sum("self_study").ToString(), "", "");
        Add(true, "", "Промежуточный контроль", "", "", "", "", "", "", "", context.Source("parser.discipline.attestation") ?? "");

        void Add(bool bold, params string[] values)
        {
            var row = DataRow(prototype);
            var cells = row.Elements<TableCell>().ToList();
            for (var i = 0; i < cells.Count && i < values.Length; i++)
                SetCell(cells[i], [values[i]], bold, i is 1 or 8 or 9 ? JustificationValues.Left : JustificationValues.Center);
            table.AppendChild(row);
        }
    }

    // Лабораторные работы и практические занятия: № | № раздела | тема | часы | форма контроля; затем «Итого».
    private static void Classes(Table table, string section, PrintContext context, List<string> unfilled, string bookmark)
    {
        if (context.Stub($"/{section}") is { } stub)
        {
            var paragraph = new Paragraph(new Run(PlainRun(table), new Text(stub) { Space = SpaceProcessingModeValues.Preserve }));
            if (table.NextSibling<Paragraph>()?.ParagraphProperties?.CloneNode(true) is ParagraphProperties properties)
                paragraph.ParagraphProperties = properties;
            table.InsertBeforeSelf(paragraph);
            table.Remove();
            return;
        }

        var rows = table.Elements<TableRow>().ToList();
        var prototype = rows.Count > 1 ? rows[1] : rows[0];
        foreach (var sample in rows.Skip(1)) sample.Remove();

        var items = Items(context.At($"/{section}"));
        if (items.Count == 0) unfilled.Add($"{bookmark} — раздел {section} не заполнен");
        foreach (var item in items)
            Add(false, Number(item, "number"), Number(item, "section_number"), Text(item, "title") ?? "", Number(item, "hours"), "");
        Add(true, "", "", "Итого:", items.Sum(i => int.TryParse(Number(i, "hours"), out var n) ? n : 0).ToString(), "");

        void Add(bool bold, params string[] values)
        {
            var row = DataRow(prototype);
            var cells = row.Elements<TableCell>().ToList();
            for (var i = 0; i < cells.Count && i < values.Length; i++)
                SetCell(cells[i], [values[i]], bold, i == 2 ? JustificationValues.Left : JustificationValues.Center);
            table.AppendChild(row);
        }
    }

    // Строка данных по образцу: без признака повторяемой шапки, объединений и заливки.
    private static TableRow DataRow(TableRow prototype)
    {
        var row = (TableRow)prototype.CloneNode(true);
        row.TableRowProperties?.RemoveAllChildren<TableHeader>();
        foreach (var properties in row.Descendants<TableCellProperties>())
        {
            properties.VerticalMerge = null;
            properties.Shading = null;
        }
        return row;
    }

    // Новая ячейка по образцу первой ячейки строки — с её границами и шрифтом, своей шириной.
    private static TableCell NewCell(TableRow row, int width)
    {
        var cell = (TableCell)row.Elements<TableCell>().First().CloneNode(true);
        var properties = cell.TableCellProperties ??= new TableCellProperties();
        properties.VerticalMerge = null;
        properties.GridSpan = null;
        properties.TableCellWidth = new TableCellWidth { Width = width.ToString(), Type = TableWidthUnitValues.Dxa };
        return cell;
    }

    /// <summary>
    /// Текст ячейки — по абзацу на строку, оформление — как у текста образца. <paramref name="bold"/> null — как в
    /// образце; <paramref name="align"/> — выравнивание абзацев.
    /// </summary>
    private static void SetCell(TableCell cell, IEnumerable<string?> lines, bool? bold, JustificationValues align)
    {
        var first = cell.Elements<Paragraph>().FirstOrDefault();
        var runProperties = first is null ? new RunProperties() : RunPropertiesOf(first);
        var paragraphProperties = first?.ParagraphProperties?.CloneNode(true) as ParagraphProperties ?? new ParagraphProperties();
        paragraphProperties.Justification = new Justification { Val = align };
        foreach (var p in cell.Elements<Paragraph>().ToList()) p.Remove();

        var texts = lines.Select(l => l ?? "").ToList();
        if (texts.Count == 0) texts.Add("");
        foreach (var text in texts)
        {
            var paragraph = new Paragraph { ParagraphProperties = (ParagraphProperties)paragraphProperties.CloneNode(true) };
            if (text.Length > 0)
            {
                var properties = (RunProperties)runProperties.CloneNode(true);
                if (bold is { } b)
                {
                    properties.Bold = b ? new Bold() : null;
                    properties.BoldComplexScript = null;
                }
                paragraph.AppendChild(new Run(properties, new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
            }
            cell.AppendChild(paragraph);
        }
    }

    private static RunProperties RunPropertiesOf(Paragraph paragraph) =>
        paragraph.Descendants<RunProperties>().FirstOrDefault()?.CloneNode(true) as RunProperties
        ?? new RunProperties(paragraph.ParagraphProperties?.ParagraphMarkRunProperties?.ChildElements.Select(c => c.CloneNode(true)) ?? []);

    // Шрифт текста таблицы без полужирного — для заглушки вместо таблицы.
    private static RunProperties PlainRun(Table table)
    {
        var properties = table.Descendants<Paragraph>().FirstOrDefault() is { } p ? RunPropertiesOf(p) : new RunProperties();
        properties.Bold = null;
        properties.BoldComplexScript = null;
        return properties;
    }

    private static List<JsonElement> Items(JsonElement? array) =>
        array is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray().ToList() : [];

    private static JsonElement? Property(JsonElement? node, string name) =>
        node is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) ? v : null;

    private static string? Text(JsonElement node, string name) =>
        Property(node, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    private static string Number(JsonElement? node, string name) =>
        Property(node, name) is { ValueKind: JsonValueKind.Number } v ? v.GetRawText() : "";
}
