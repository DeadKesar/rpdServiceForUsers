using System.Text.Json;
using RpdFos.Core.DocumentTypes;

namespace RpdFos.Core.Documents;

/// <summary>Заполненность раздела по обязательным полям режима отправки.</summary>
public static class SectionFill
{
    public const string Complete = "complete", Incomplete = "incomplete", NotApplicable = "not_applicable";
}

/// <summary>
/// Раздел документа для редактора: видимость по данным дисциплины (<c>x-visible-if</c>, FR-TPL-14), заглушка для
/// скрытого раздела, заполненность и чего не хватает для отправки.
/// </summary>
public sealed record SectionState(
    string Key, string Title, bool Visible, string? StubText, string Fill, IReadOnlyList<DocumentValidationError> Missing);

/// <summary>
/// Метаданные документа для редактора: разделы, поля только для чтения при текущем статусе (JSON Pointer поддеревьев)
/// и кандидаты в компетенции и индикаторы из матрицы.
/// </summary>
public sealed record DocumentMetadata(
    IReadOnlyList<SectionState> Sections, IReadOnlyList<string> ReadOnlyPaths, IReadOnlyList<CompetenceCandidate> CompetenceCandidates)
{
    public static DocumentMetadata Build(string schemaJson, JsonElement payload, string status, DisciplineFacts facts,
        IDocumentValidator validator, Func<string, JsonElement?> resolve)
    {
        var schema = JsonDocument.Parse(schemaJson).RootElement;
        var missing = DocumentSubmission.Check(schemaJson, payload, facts, validator, resolve)
            .ToLookup(e => DocumentSubmission.Section(e.Path));

        var sections = new List<SectionState>();
        if (schema.TryGetProperty("properties", out var properties))
            foreach (var p in properties.EnumerateObject())
            {
                var root = schema;
                if (SchemaInspector.Deref(p.Value, ref root, resolve) is not { ValueKind: JsonValueKind.Object } section) continue;
                var visible = ConditionalSections.IsVisible(section, facts);
                var errors = visible ? missing[p.Name].ToList() : [];
                sections.Add(new SectionState(p.Name, Text(section, "title") ?? p.Name, visible,
                    visible ? null : Text(section, "x-stub-text"),
                    !visible ? SectionFill.NotApplicable : errors.Count == 0 ? SectionFill.Complete : SectionFill.Incomplete,
                    errors));
            }

        return new DocumentMetadata(sections, ReadOnly(schema, status, resolve), facts.Competences);
    }

    /// <summary>
    /// Поля только для чтения: из учебного плана (<c>readOnly</c>) — всегда, с <c>x-readonly-after</c> — когда документ
    /// продвинулся дальше указанного статуса. Черновик, сохранённый черновик и возврат на доработку — одна ступень:
    /// автор редактирует.
    /// </summary>
    public static IReadOnlyList<string> ReadOnly(JsonElement schema, string status, Func<string, JsonElement?> resolve)
    {
        var result = new List<string>();
        var root = schema;
        Walk(SchemaInspector.Deref(schema, ref root, resolve), root, "", 0);
        return result;

        void Walk(JsonElement? node, JsonElement currentRoot, string path, int depth)
        {
            if (node is not { ValueKind: JsonValueKind.Object } n || depth > 16 || !n.TryGetProperty("properties", out var props)) return;
            foreach (var p in props.EnumerateObject())
            {
                var childRoot = currentRoot;
                var child = SchemaInspector.Deref(p.Value, ref childRoot, resolve);
                var childPath = $"{path}/{p.Name}";
                if (IsReadOnly(p.Value, status) || child is { } c && IsReadOnly(c, status)) result.Add(childPath);
                else Walk(child, childRoot, childPath, depth + 1);
            }
        }
    }

    private static bool IsReadOnly(JsonElement field, string status) =>
        field.TryGetProperty("readOnly", out var r) && r.ValueKind == JsonValueKind.True ||
        field.TryGetProperty("x-readonly-after", out var after) && after.GetString() is { } s &&
        DocumentStatuses.Progress(status) > DocumentStatuses.Progress(s);

    private static string? Text(JsonElement node, string name) =>
        node.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>
/// Условные разделы (<c>x-visible-if</c>, FR-TPL-14) по данным дисциплины из плана: практические и лабораторные —
/// по часам, курсовой проект или работа — по видам аттестации. Неизвестное условие раздел не скрывает.
/// </summary>
public static class ConditionalSections
{
    public static bool IsVisible(JsonElement section, DisciplineFacts facts)
    {
        if (!section.TryGetProperty("x-visible-if", out var condition) || condition.ValueKind != JsonValueKind.Object) return true;
        var source = condition.TryGetProperty("source", out var s) ? s.GetString() : null;
        return source switch
        {
            "parser.discipline.details" when condition.TryGetProperty("any", out var any) =>
                facts.Semesters.Any(semester => Values(any).Any(field => Hours(semester, field) > 0)),
            "parser.discipline.attestation" when condition.TryGetProperty("includes", out var includes) =>
                facts.Semesters.Any(semester => semester.ControlTypes.Intersect(Values(includes)).Any()),
            _ => true
        };
    }

    private static IEnumerable<string> Values(JsonElement value) => value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray().Select(v => v.GetString() ?? "")
        : [value.GetString() ?? ""];

    private static int Hours(Data.Entities.DisciplineSemesterDetails s, string field) => field switch
    {
        "contact_hours" => s.ContactHours,
        "lectures" => s.Lectures,
        "practice" => s.Practice,
        "lab_hours" => s.LabHours,
        "self_study" => s.SelfStudy,
        "control" => s.Control,
        _ => 0
    };
}
