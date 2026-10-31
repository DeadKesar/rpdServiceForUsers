using System.Text.Json;
using System.Text.Json.Nodes;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.DocumentTypes;

namespace RpdFos.Core.Documents;

/// <summary>Отличие поля документа от учебного плана: место (лист — число, строка) и значение по плану.</summary>
public sealed record PlanDifference(string Path, JsonNode? PlanValue);

/// <summary>Изменение поля из плана при сохранении: место, было, стало — для журнала аудита.</summary>
public sealed record PlanFieldChange(string Path, JsonNode? Before, JsonNode? After);

/// <summary>
/// Поля документа из учебного плана (<c>readOnly</c> в схеме): автор их не меняет, методист и администратор
/// исправляют на проверке и после приёма (этап 5.1). Значение по плану — то, что дало бы предзаполнение сейчас.
/// </summary>
public static class PlanFields
{
    public const string DiffersFromPlan = "Значение отличается от учебного плана";

    /// <summary>
    /// Источник производного поля: вид курсового проектирования вычисляется из видов аттестации документа. Своей
    /// пометки и предупреждения «отличается от плана» у него нет — о правке говорит пометка у видов аттестации; руками
    /// он не правится (этап 5.2).
    /// </summary>
    public const string CourseWorkKindSource = "parser.discipline.course_work_kind";

    /// <summary>Поля из плана — JSON Pointer поддеревьев с <c>readOnly</c>, без производных.</summary>
    public static IReadOnlyList<string> Paths(JsonElement schema, Func<string, JsonElement?> resolve) =>
        Walk(schema, resolve, derived: false);

    /// <summary>Производные поля плана (<see cref="CourseWorkKindSource"/>).</summary>
    public static IReadOnlyList<string> DerivedPaths(JsonElement schema, Func<string, JsonElement?> resolve) =>
        Walk(schema, resolve, derived: true);

    private static List<string> Walk(JsonElement schema, Func<string, JsonElement?> resolve, bool derived)
    {
        var result = new List<string>();
        var root = schema;
        Visit(SchemaInspector.Deref(schema, ref root, resolve), root, "", 0);
        return result;

        void Visit(JsonElement? node, JsonElement currentRoot, string path, int depth)
        {
            if (node is not { ValueKind: JsonValueKind.Object } n || depth > 16 || !n.TryGetProperty("properties", out var props)) return;
            foreach (var p in props.EnumerateObject())
            {
                var childRoot = currentRoot;
                var child = SchemaInspector.Deref(p.Value, ref childRoot, resolve);
                var childPath = $"{path}/{p.Name}";
                if (IsPlan(p.Value) || child is { } c && IsPlan(c))
                {
                    if (IsDerived(child ?? p.Value) == derived) result.Add(childPath);
                }
                else Visit(child, childRoot, childPath, depth + 1);
            }
        }
    }

    private static bool IsDerived(JsonElement field) =>
        field.TryGetProperty("x-source", out var source) && source.GetString() == CourseWorkKindSource;

    /// <summary>Значения полей из плана по плану: поле → значение предзаполнения (нет в плане — null).</summary>
    public static Dictionary<string, JsonNode?> Values(JsonElement schema, DisciplineFacts facts, Func<string, JsonElement?> resolve)
    {
        // Автор предзаполнению нужен только для составителей — это не поле плана.
        var prefill = DocumentPrefill.Build(schema, facts, new DocumentAuthor(Guid.Empty, "", null), resolve);
        return Paths(schema, resolve).ToDictionary(p => p, p => At(prefill, p)?.DeepClone());
    }

    /// <summary>
    /// Чем содержимое отличается от плана — по листьям: «Лекции 1 семестра», а не вся трудоёмкость. Поле, которого
    /// нет ни в документе, ни в плане, не отличается.
    /// </summary>
    public static IReadOnlyList<PlanDifference> Differences(JsonElement schema, JsonElement payload, DisciplineFacts facts,
        Func<string, JsonElement?> resolve)
    {
        var document = JsonNode.Parse(payload.GetRawText());
        var result = new List<PlanDifference>();
        foreach (var (path, plan) in Values(schema, facts, resolve))
            foreach (var change in Leaves(path, plan, At(document, path)))
                result.Add(new PlanDifference(change.Path, change.Before));
        return result;
    }

    /// <summary>Что поменялось в полях из плана между двумя содержимыми — по листьям.</summary>
    public static IReadOnlyList<PlanFieldChange> Changes(JsonNode before, JsonNode after, IEnumerable<string> paths) =>
        paths.SelectMany(p => Leaves(p, At(before, p), At(after, p))).ToList();

    /// <summary>
    /// Вид курсового проектирования (поле с источником <c>parser.discipline.course_work_kind</c>) следует за видами
    /// аттестации документа: исправление добавило курсовой проект — вид ставится в раздел, при необходимости раздел
    /// появляется. Курсового проектирования не осталось — раздел скрыт и остаётся в документе как есть.
    /// </summary>
    /// <returns>Изменения вида — для журнала аудита, как у исправлений.</returns>
    public static IReadOnlyList<PlanFieldChange> FollowCourseWorkKind(JsonElement schema, JsonNode before, JsonObject after,
        DisciplineFacts facts, Func<string, JsonElement?> resolve)
    {
        string? Kind(JsonNode payload) => PlanTitles.CourseWorkKind(
            DocumentSemesters.Of(JsonDocument.Parse(payload.ToJsonString()).RootElement, facts).SelectMany(s => s.ControlTypes));
        var kind = Kind(after);
        if (kind is null || kind == Kind(before)) return [];

        var result = new List<PlanFieldChange>();
        foreach (var path in DerivedPaths(schema, resolve))
        {
            var segments = path.TrimStart('/').Split('/');
            JsonObject parent = after;
            foreach (var segment in segments[..^1])
            {
                if (parent[segment] is not JsonObject next) parent[segment] = next = new JsonObject();
                parent = next;
            }
            var previous = parent[segments[^1]]?.DeepClone();
            if (previous is JsonValue v && v.TryGetValue(out string? s) && s == kind) continue;
            parent[segments[^1]] = kind;
            result.Add(new PlanFieldChange(path, previous, JsonValue.Create(kind)));
        }
        return result;
    }

    /// <summary>
    /// Контактная работа следует за исправлением лекций, практических и лабораторных: меняется на ту же разницу,
    /// прочая контактная работа (КСР, ИКР) сохраняется. Если контактную работу семестра исправили в том же сохранении
    /// сами — она не трогается (этап 5.2).
    /// </summary>
    /// <returns>Изменения контактной работы — для журнала аудита.</returns>
    public static IReadOnlyList<PlanFieldChange> FollowContactHours(JsonNode before, JsonObject after)
    {
        const string semestersPath = "/hours_distribution/semesters";
        if (At(before, semestersPath) is not JsonArray old || At(after, semestersPath) is not JsonArray rows) return [];
        var result = new List<PlanFieldChange>();
        for (var i = 0; i < rows.Count && i < old.Count; i++)
        {
            if (rows[i] is not JsonObject row || old[i] is not JsonObject was || Int(row, "semester") != Int(was, "semester") ||
                Int(row, "contact_hours") != Int(was, "contact_hours")) continue;
            var delta = Classes(row) - Classes(was);
            if (delta == 0) continue;
            var contact = Math.Max(0, Int(was, "contact_hours") + delta);
            result.Add(new PlanFieldChange($"{semestersPath}/{i}/contact_hours", was["contact_hours"]?.DeepClone(), JsonValue.Create(contact)));
            row["contact_hours"] = contact;
        }
        return result;

        static int Classes(JsonObject s) => Int(s, "lectures") + Int(s, "practice") + Int(s, "lab_hours");
        static int Int(JsonObject s, string key) => s[key] is JsonValue v && v.TryGetValue(out int n) ? n : 0;
    }

    /// <summary>
    /// Значение по плану для сообщения: «24», «Экзамен, Зачёт», «—». Коды перечислений — названиями из
    /// <c>x-enum-titles</c> подсхемы поля (у списка — его элементов), если она дана.
    /// </summary>
    public static string Text(JsonNode? value, JsonElement? field = null)
    {
        var titles = field is { ValueKind: JsonValueKind.Object } f
            ? f.TryGetProperty("x-enum-titles", out var own) ? own
            : f.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object &&
              items.TryGetProperty("x-enum-titles", out var inner) ? inner : (JsonElement?)null
            : null;
        return Render(value);

        string Render(JsonNode? node) => node switch
        {
            null => "—",
            JsonArray a => a.Count == 0 ? "—" : string.Join(", ", a.Select(Render)),
            JsonObject o => string.Join(", ", o.Select(p => $"{p.Key}: {Render(p.Value)}")),
            JsonValue v when v.TryGetValue(out string? s) => s.Length == 0 ? "—"
                : titles is { ValueKind: JsonValueKind.Object } t && t.TryGetProperty(s, out var title) ? title.GetString() ?? s : s,
            _ => node.ToJsonString()
        };
    }

    private static IEnumerable<PlanFieldChange> Leaves(string path, JsonNode? a, JsonNode? b)
    {
        if (a is JsonObject oa && b is JsonObject ob)
        {
            foreach (var key in oa.Select(p => p.Key).Union(ob.Select(p => p.Key)))
                foreach (var change in Leaves($"{path}/{key}", oa[key], ob[key]))
                    yield return change;
        }
        else if (a is JsonArray aa && b is JsonArray ab && aa.Count == ab.Count && aa.All(x => x is JsonObject))
        {
            // Семестры трудоёмкости — по номеру строки: список той же длины сравнивается построчно.
            for (var i = 0; i < aa.Count; i++)
                foreach (var change in Leaves($"{path}/{i}", aa[i], ab[i]))
                    yield return change;
        }
        else if (!JsonNode.DeepEquals(a, b))
            yield return new PlanFieldChange(path, a?.DeepClone(), b?.DeepClone());
    }

    private static bool IsPlan(JsonElement field) =>
        field.ValueKind == JsonValueKind.Object && field.TryGetProperty("readOnly", out var r) && r.ValueKind == JsonValueKind.True;

    private static JsonNode? At(JsonNode? node, string pointer)
    {
        foreach (var raw in pointer.TrimStart('/').Split('/'))
        {
            var segment = raw.Replace("~1", "/").Replace("~0", "~");
            node = node switch
            {
                JsonObject o => o[segment],
                JsonArray a when int.TryParse(segment, out var i) && i < a.Count => a[i],
                _ => null
            };
            if (node is null) return null;
        }
        return node;
    }
}

/// <summary>
/// Семестры трудоёмкости документа — из содержимого, а не из плана: после исправления методистом правила сверки
/// и печать работают с исправленными часами. Трудоёмкости в содержимом нет — данные плана.
/// </summary>
public static class DocumentSemesters
{
    public static IReadOnlyList<DisciplineSemesterDetails> Of(JsonElement payload, DisciplineFacts facts)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("hours_distribution", out var hours)) return facts.Semesters;
        var semesters = FromHours(hours);
        return semesters.Count > 0 ? semesters : facts.Semesters;
    }

    /// <summary>Семестры из раздела трудоёмкости (значение <c>hours_distribution</c>).</summary>
    public static IReadOnlyList<DisciplineSemesterDetails> FromHours(JsonElement hours)
    {
        if (hours.ValueKind != JsonValueKind.Object || !hours.TryGetProperty("semesters", out var list) ||
            list.ValueKind != JsonValueKind.Array) return [];
        return list.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.Object).Select(Row)
            .Where(s => s.Semester > 0).OrderBy(s => s.Semester).ToList();
    }

    /// <summary>Одна строка трудоёмкости (элемент <c>hours_distribution/semesters</c>).</summary>
    public static DisciplineSemesterDetails Row(JsonElement s) => new()
    {
        Semester = Int(s, "semester"), ContactHours = Int(s, "contact_hours"), Lectures = Int(s, "lectures"),
        Practice = Int(s, "practice"), LabHours = Int(s, "lab_hours"), SelfStudy = Int(s, "self_study"),
        Control = Int(s, "control"), Credits = Int(s, "credits"),
        ControlTypes = s.TryGetProperty("control_types", out var t) && t.ValueKind == JsonValueKind.Array
            ? t.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
            : []
    };

    private static int Int(JsonElement node, string name) =>
        node.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;
}
