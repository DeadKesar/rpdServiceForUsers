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

    /// <summary>Поля из плана — JSON Pointer поддеревьев с <c>readOnly</c>.</summary>
    public static IReadOnlyList<string> Paths(JsonElement schema, Func<string, JsonElement?> resolve)
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
                if (IsPlan(p.Value) || child is { } c && IsPlan(c)) result.Add(childPath);
                else Walk(child, childRoot, childPath, depth + 1);
            }
        }
    }

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

    /// <summary>Значение по плану для сообщения: «24», «Экзамен, Зачёт», «—».</summary>
    public static string Text(JsonNode? value) => value switch
    {
        null => "—",
        JsonArray a => a.Count == 0 ? "—" : string.Join(", ", a.Select(Text)),
        JsonObject o => string.Join(", ", o.Select(p => $"{p.Key}: {Text(p.Value)}")),
        JsonValue v when v.TryGetValue(out string? s) => s.Length == 0 ? "—" : s,
        _ => value.ToJsonString()
    };

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
        return list.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.Object).Select(s => new DisciplineSemesterDetails
            {
                Semester = Int(s, "semester"), ContactHours = Int(s, "contact_hours"), Lectures = Int(s, "lectures"),
                Practice = Int(s, "practice"), LabHours = Int(s, "lab_hours"), SelfStudy = Int(s, "self_study"),
                Control = Int(s, "control"), Credits = Int(s, "credits"),
                ControlTypes = s.TryGetProperty("control_types", out var t) && t.ValueKind == JsonValueKind.Array
                    ? t.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                    : []
            })
            .Where(s => s.Semester > 0).OrderBy(s => s.Semester).ToList();
    }

    private static int Int(JsonElement node, string name) =>
        node.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;
}
