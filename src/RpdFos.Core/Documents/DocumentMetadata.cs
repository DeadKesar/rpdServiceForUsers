using System.Text.Json;
using System.Text.Json.Nodes;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.DocumentTypes;

namespace RpdFos.Core.Documents;

/// <summary>
/// Заполненность раздела по обязательным элементам режима отправки: всё (complete), часть (incomplete), ни одного
/// (empty, этап 5.2); not_applicable — раздел не предусмотрен или обязательных элементов у него нет.
/// </summary>
public static class SectionFill
{
    public const string Complete = "complete", Incomplete = "incomplete", Empty = "empty", NotApplicable = "not_applicable";

    /// <summary>
    /// Обязательные элементы раздела и сколько из них заполнено. Раздел-список — один элемент, если он обязателен
    /// в корне схемы или условный (предусмотрен — значит, нужна хотя бы одна запись): заполнен, когда в нём есть записи.
    /// Раздел-объект — его обязательные поля (<c>required</c>), если объект обязателен или уже начат: необязательный
    /// пустой объект ничего не требует, как и проверка схемы.
    /// </summary>
    public static (int Total, int Filled) Required(JsonElement section, bool requiredAtRoot, JsonElement? value)
    {
        var present = value is { } v && !DocumentSubmission.IsEmpty(v);
        var mandatory = requiredAtRoot || section.TryGetProperty("x-visible-if", out _);
        // Правило проверки на разделе или его полях (сверка часов) — тоже обязательный элемент: раздел, где его
        // ошибку исправили, — выполнен, а не «обязательного нет» (найдено при проверке B4 в браузере).
        var rule = HasRule(section) ? (1, present ? 1 : 0) : (0, 0);
        if (section.TryGetProperty("type", out var type) && type.GetString() == "array")
            return mandatory ? (1, present ? 1 : 0) : rule;
        if (!section.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.Array || !mandatory && !present)
            return rule;
        var names = required.EnumerateArray().Select(n => n.GetString() ?? "").ToList();
        var filled = names.Count(n => value is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(n, out var field) &&
            !DocumentSubmission.IsEmpty(field));
        return (names.Count + rule.Item1, filled + rule.Item2);
    }

    private static bool HasRule(JsonElement section) =>
        section.TryGetProperty("x-validation-rule", out _) ||
        section.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object &&
        props.EnumerateObject().Any(p => p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("x-validation-rule", out _));

    /// <summary>
    /// Состояние по ошибкам полной проверки и обязательным элементам: ошибок нет — всё заполнено (или требовать нечего);
    /// есть — ни одного элемента или часть. Ошибка правила при заполненных элементах — тоже часть.
    /// </summary>
    public static string Of(bool visible, int errors, (int Total, int Filled) required) =>
        !visible ? NotApplicable
        : errors == 0 ? required.Total == 0 ? NotApplicable : Complete
        : required is { Total: > 0, Filled: 0 } ? Empty
        : Incomplete;
}

/// <summary>
/// Раздел документа для редактора: видимость по трудоёмкости документа (<c>x-visible-if</c>, FR-TPL-14), заглушка для
/// скрытого раздела, заполненность, чего не хватает для отправки (ошибки) и предупреждения правил.
/// </summary>
public sealed record SectionState(
    string Key, string Title, bool Visible, string? StubText, string Fill, IReadOnlyList<DocumentValidationError> Missing,
    IReadOnlyList<DocumentValidationError> Warnings);

/// <summary>Кандидаты в пре- и постреквизиты по подсказке <c>x-plan-disciplines</c> поля: раньше (<c>earlier</c>) и позже (<c>later</c>).</summary>
public sealed record PlanDisciplineChoices(IReadOnlyList<PlanDisciplineCandidate> Earlier, IReadOnlyList<PlanDisciplineCandidate> Later);

/// <summary>
/// Метаданные документа для редактора: разделы, поля только для чтения при текущем статусе (JSON Pointer поддеревьев)
/// и кандидаты: компетенции и индикаторы из матрицы, дисциплины плана до и после дисциплины — для пре- и постреквизитов.
/// <paramref name="PlanValues"/> — поля из плана и их значения по плану: исправленное методистом поле редактор помечает
/// «изменено методистом, в плане: …».
/// </summary>
public sealed record DocumentMetadata(
    IReadOnlyList<SectionState> Sections, IReadOnlyList<string> ReadOnlyPaths, IReadOnlyList<string> HiddenPaths,
    IReadOnlyList<CompetenceCandidate> CompetenceCandidates, PlanDisciplineChoices PlanDisciplines,
    IReadOnlyDictionary<string, JsonNode?> PlanValues, IReadOnlyDictionary<string, string>? NoCandidatesTexts = null)
{
    /// <summary>
    /// Места, не предусмотренные учебным планом (<c>x-visible-if</c>): разделы и поля внутри разделов —
    /// например, методические указания к лабораторным работам.
    /// </summary>
    public static IReadOnlyList<string> Hidden(JsonElement schema, IReadOnlyList<DisciplineSemesterDetails> semesters,
        Func<string, JsonElement?> resolve)
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
                if (child is { } c && !ConditionalSections.IsVisible(c, semesters)) result.Add(childPath);
                else Walk(child, childRoot, childPath, depth + 1);
            }
        }
    }

    public static DocumentMetadata Build(string schemaJson, JsonElement payload, string status, DisciplineFacts facts,
        IDocumentValidator validator, ValidationRuleSet rules, Func<string, JsonElement?> resolve,
        IReadOnlyDictionary<string, BookmarkTarget>? printMapping = null)
    {
        var schema = JsonDocument.Parse(schemaJson).RootElement;
        var issues = DocumentSubmission.Check(schemaJson, payload, facts, validator, rules, resolve);
        var semesters = DocumentSemesters.Of(payload, facts);
        var missing = issues.Where(e => e.Level == IssueLevels.Error).ToLookup(e => DocumentSubmission.Section(e.Path));
        var warnings = issues.Where(e => e.Level == IssueLevels.Warning).ToLookup(e => DocumentSubmission.Section(e.Path));

        var rootRequired = schema.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(n => n.GetString()).ToHashSet()
            : [];
        var sections = new List<SectionState>();
        if (schema.TryGetProperty("properties", out var properties))
            foreach (var p in properties.EnumerateObject())
            {
                var root = schema;
                if (SchemaInspector.Deref(p.Value, ref root, resolve) is not { ValueKind: JsonValueKind.Object } section) continue;
                var visible = ConditionalSections.IsVisible(section, semesters);
                var errors = visible ? missing[p.Name].ToList() : [];
                var required = SectionFill.Required(section, rootRequired.Contains(p.Name),
                    payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(p.Name, out var value) ? value : null);
                sections.Add(new SectionState(p.Name, Text(section, "title") ?? p.Name, visible,
                    visible ? null : Text(section, "x-stub-text"), SectionFill.Of(visible, errors.Count, required),
                    errors, visible ? warnings[p.Name].ToList() : []));
            }

        return new DocumentMetadata(sections, ReadOnly(schema, status, resolve), Hidden(schema, semesters, resolve), facts.Competences,
            new PlanDisciplineChoices(facts.Earlier, facts.Later), PlanFields.Values(schema, facts, resolve),
            NoCandidates(schema, facts, resolve, printMapping));
    }

    /// <summary>
    /// Что напечатается вместо пустого поля выбора из плана, когда выбирать не из чего: поле → формулировка из
    /// сопоставления шаблона. Редактор показывает её подсказкой — та же, что в печати.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? NoCandidates(JsonElement schema, DisciplineFacts facts,
        Func<string, JsonElement?> resolve, IReadOnlyDictionary<string, BookmarkTarget>? printMapping)
    {
        if (printMapping is null) return null;
        var result = new Dictionary<string, string>();
        foreach (var target in printMapping.Values.Where(t => t.Kind == BookmarkTargetKinds.Field && t.NoCandidates is not null))
            if (SchemaInspector.SubschemaAt(schema, target.Target, resolve) is { } field &&
                facts.NoCandidatesText(field, target.NoCandidates) is { } text)
                result[target.Target] = text;
        return result;
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
/// Условные разделы (<c>x-visible-if</c>, FR-TPL-14) по трудоёмкости документа (<see cref="DocumentSemesters"/>): после
/// исправления методистом состав разделов следует за данными документа, а не плана (этап 5.2). Практические и лабораторные —
/// по часам, курсовой проект или работа — по видам аттестации. Неизвестное условие раздел не скрывает.
/// </summary>
public static class ConditionalSections
{
    public static bool IsVisible(JsonElement section, IReadOnlyList<DisciplineSemesterDetails> semesters)
    {
        if (!section.TryGetProperty("x-visible-if", out var condition) || condition.ValueKind != JsonValueKind.Object) return true;
        var source = condition.TryGetProperty("source", out var s) ? s.GetString() : null;
        return source switch
        {
            "parser.discipline.details" when condition.TryGetProperty("any", out var any) =>
                semesters.Any(semester => Values(any).Any(field => Hours(semester, field) > 0)),
            "parser.discipline.attestation" when condition.TryGetProperty("includes", out var includes) =>
                semesters.Any(semester => semester.ControlTypes.Intersect(Values(includes)).Any()),
            _ => true
        };
    }

    private static IEnumerable<string> Values(JsonElement value) => value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray().Select(v => v.GetString() ?? "")
        : [value.GetString() ?? ""];

    private static int Hours(DisciplineSemesterDetails s, string field) => field switch
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
