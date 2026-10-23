using System.Text.Json;
using RpdFos.Core.Documents;
using RpdFos.Core.DocumentTypes;

namespace RpdFos.Core.Printing;

/// <summary>
/// Всё, из чего печатается документ: содержимое и снимок схемы шаблона, данные плана и справочников, подписанты,
/// год документа, места, не предусмотренные планом. Здесь же — значения целей вида <c>source</c> и <c>field</c>.
/// </summary>
public sealed class PrintContext(
    JsonElement payload, JsonElement schema, Func<string, JsonElement?> resolve, DisciplineFacts facts,
    SignatoryReport signatories, int year, IReadOnlyList<string> hiddenPaths)
{
    private int _lists;

    public JsonElement Payload { get; } = payload;
    public DisciplineFacts Facts { get; } = facts;
    public SignatoryReport Signatories { get; } = signatories;

    /// <summary>Номер для нового списка: нумерация у каждого списка своя.</summary>
    public int NextListId() => ++_lists;

    /// <summary>Значение источника данных плана и справочников (каталог <see cref="DocumentSources"/>) — строкой.</summary>
    public string? Source(string source)
    {
        var d = Facts.Discipline;
        var semesters = Facts.Semesters.Select(s => s.Semester).ToList();
        return source switch
        {
            "parser.discipline.name" => d.Name,
            "parser.discipline.ind" => d.Code,
            "parser.discipline.block" => PlanTitles.Block(d.Block),
            "parser.discipline.part_type" => PlanTitles.Part(d.PartType),
            "parser.discipline.semesters" => string.Join(", ", semesters),
            "parser.discipline.course" => string.Join(", ", semesters.Select(s => (s + 1) / 2).Distinct()),
            "parser.discipline.ze_total" => d.TotalCredits.ToString(),
            "parser.discipline.hours_total" => d.TotalHours.ToString(),
            "parser.discipline.course_work_kind" => PlanTitles.CourseWorkKind(Facts.Semesters.SelectMany(s => s.ControlTypes)) is { } kind
                ? PlanTitles.ControlType(kind)
                : null,
            "parser.discipline.attestation" =>
                string.Join(", ", Facts.Semesters.SelectMany(s => s.ControlTypes).Distinct().Select(PlanTitles.ControlType)),
            "reference.direction.code" => Facts.Direction.Code,
            "reference.direction.name" => Facts.Direction.Name,
            "reference.direction.level" => PlanTitles.EducationLevel(Facts.Direction.EducationLevel),
            "reference.direction.qualification" => PlanTitles.Qualification(Facts.Direction.EducationLevel),
            "reference.profile.name" => Facts.Profile.Name,
            "reference.curriculum_plan.education_form" => PlanTitles.StudyForm(Facts.Plan.StudyForm),
            "reference.curriculum_plan.academic_year" => PlanTitles.AcademicYear(Facts.Plan.Year, semesters.DefaultIfEmpty(1).Min()),
            "reference.department.name" => Facts.Department?.Name,
            "document.year" => year.ToString(),
            _ => null
        };
    }

    /// <summary>Место в содержимом документа по JSON Pointer; нет такого места — null.</summary>
    public JsonElement? At(string pointer)
    {
        var current = Payload;
        foreach (var raw in pointer.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = raw.Replace("~1", "/").Replace("~0", "~");
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(segment, out var next)) current = next;
            else if (current.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var i) && i < current.GetArrayLength()) current = current[i];
            else return null;
        }
        return current;
    }

    public JsonElement? SchemaAt(string pointer) => SchemaInspector.SubschemaAt(schema, pointer, resolve);

    /// <summary>
    /// Стандартная формулировка вместо места, не предусмотренного учебным планом (<c>x-stub-text</c>, FR-TPL-14);
    /// место предусмотрено — null.
    /// </summary>
    public string? Stub(string pointer)
    {
        var hidden = hiddenPaths.FirstOrDefault(h => pointer == h || pointer.StartsWith(h + "/", StringComparison.Ordinal));
        if (hidden is null) return null;
        return SchemaAt(hidden) is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty("x-stub-text", out var stub)
            ? stub.GetString()
            : "";
    }

    /// <summary>
    /// Поле документа для печати — по подсхеме: форматированный текст — абзацами, списки — списками, компетенции —
    /// перечнем по категориям, перечисления — названиями из x-enum-titles.
    /// </summary>
    public PrintValue Field(string pointer)
    {
        if (Stub(pointer) is { } stub) return PrintValue.Text(stub);
        return At(pointer) is { } value && SchemaAt(pointer) is { } schema ? Render(value, schema) : PrintValue.Empty;
    }

    private PrintValue Render(JsonElement value, JsonElement schema)
    {
        // Подсхемы может не быть (содержимое шире схемы) — тогда значение печатается как есть.
        if (schema.ValueKind != JsonValueKind.Object) schema = Empty;
        var widget = Text(schema, "x-ui-widget");
        switch (value.ValueKind)
        {
            case JsonValueKind.String when widget == "richtext":
                return new PrintValue(RichTextBlocks.Parse(value.GetString() ?? "", NextListId));
            case JsonValueKind.String when Text(schema, "format") == "date" && DateOnly.TryParse(value.GetString(), out var date):
                return PrintValue.Text($"«{date.Day:00}» {MonthsGenitive[date.Month - 1]} {date.Year} г.");
            case JsonValueKind.String:
                // Для печати — x-print-titles («предусмотрен курсовой проект»), иначе названия из формы (x-enum-titles).
                var text = value.GetString() ?? "";
                return PrintValue.Text(
                    schema.TryGetProperty("x-print-titles", out var phrases) && phrases.TryGetProperty(text, out var phrase) ? phrase.GetString()
                    : schema.TryGetProperty("x-enum-titles", out var titles) && titles.TryGetProperty(text, out var title) ? title.GetString()
                    : text);
            // Библиографическая запись уже содержит год и издательство; интернет-ресурс — название и адрес.
            case JsonValueKind.Array when widget == "literature_list":
                var list = NextListId();
                return new PrintValue(value.EnumerateArray()
                    .Select(item => Text(item, "citation") ?? (Text(item, "title") is { } name
                        ? Text(item, "url") is { } url ? $"{name} // Режим доступа: {url}" : name
                        : null))
                    .OfType<string>().Where(t => t.Trim().Length > 0)
                    .Select(t => new PrintParagraph([new PrintRun(t.Trim())], PrintListKind.Numbered, list)).ToList());
            // Пре- и постреквизиты — списком «индекс название», как в РПД вуза.
            case JsonValueKind.Array when widget == "discipline_picker":
                var disciplines = NextListId();
                return new PrintValue(value.EnumerateArray()
                    .Select(item => $"{Text(item, "code")} {Text(item, "name")}".Trim())
                    .Where(t => t.Length > 0)
                    .Select(t => new PrintParagraph([new PrintRun(t)], PrintListKind.Bullet, disciplines)).ToList());
            case JsonValueKind.Number:
                return PrintValue.Text(value.GetRawText());
            case JsonValueKind.Array when widget == "competence_picker":
                return Competences(value);
            case JsonValueKind.Array:
                var items = SchemaAtItems(schema);
                var id = NextListId();
                return new PrintValue(value.EnumerateArray()
                    .Select(item => Render(item, items).Inline)
                    .Where(t => t.Length > 0)
                    .Select(t => new PrintParagraph([new PrintRun(t)], PrintListKind.Bullet, id)).ToList());
            case JsonValueKind.Object:
                var fields = schema.TryGetProperty("properties", out var props)
                    ? props.EnumerateObject()
                        .Select(p => (Name: p.Name, Schema: Resolve(p.Value)))
                        .Where(p => Text(p.Schema, "format") != "uuid" && value.TryGetProperty(p.Name, out _))
                        .Select(p => (p.Schema, Text: Render(value.GetProperty(p.Name), p.Schema).Inline))
                        .Where(p => p.Text.Length > 0).ToList()
                    : [];
                // Раздел с фразой для печати (вид курсового) — предложениями: фраза, затем «Поле: значение».
                if (fields.Any(f => f.Schema.TryGetProperty("x-print-titles", out _)))
                    return PrintValue.Text(string.Join(". ", fields.Select(f =>
                        f.Schema.TryGetProperty("x-print-titles", out _) ? f.Text : $"{Text(f.Schema, "title")}: {f.Text}")).TrimEnd('.') + ".");
                // Запись — значения её полей по порядку схемы через запятую.
                return PrintValue.Text(string.Join(", ", fields.Select(f => f.Text)));
            default:
                return PrintValue.Empty;
        }
    }

    // Компетенции — по категориям, как в шаблонах МГТУ «СТАНКИН»: «Универсальные компетенции:», затем «УК-1. …;».
    private PrintValue Competences(JsonElement competences)
    {
        var paragraphs = new List<PrintParagraph>();
        foreach (var (prefix, title) in new[] { ("УК", "Универсальные компетенции"), ("ОПК", "Общепрофессиональные компетенции"), ("ПК", "Профессиональные компетенции") })
        {
            var group = competences.EnumerateArray()
                .Where(c => Text(c, "code") is { } code && code.StartsWith(prefix + "-", StringComparison.Ordinal)).ToList();
            if (group.Count == 0) continue;
            paragraphs.Add(PrintParagraph.Plain(title + ":", bold: true));
            for (var i = 0; i < group.Count; i++)
            {
                var formulation = (Text(group[i], "formulation") ?? "").Trim().TrimEnd('.', ';');
                paragraphs.Add(new PrintParagraph([
                    new PrintRun(Text(group[i], "code") + ". ", Bold: true),
                    new PrintRun(formulation + (i == group.Count - 1 ? "." : ";"))
                ]));
            }
        }
        return new PrintValue(paragraphs);
    }

    private static readonly JsonElement Empty = JsonDocument.Parse("{}").RootElement;

    // Дата в документах МГТУ «СТАНКИН» — «15» октября 2026 г.
    private static readonly string[] MonthsGenitive =
        ["января", "февраля", "марта", "апреля", "мая", "июня", "июля", "августа", "сентября", "октября", "ноября", "декабря"];

    private JsonElement SchemaAtItems(JsonElement schema) =>
        schema.TryGetProperty("items", out var items) ? Resolve(items) : Empty;

    // Локальная ссылка схемы «#/$defs/…» или ссылка на схему другого типа.
    private JsonElement Resolve(JsonElement node)
    {
        var root = schema;
        return SchemaInspector.Deref(node, ref root, resolve) ?? node;
    }

    private static string? Text(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
