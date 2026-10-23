using System.Text.Json;
using System.Text.Json.Nodes;
using RpdFos.Core.Documents;
using RpdFos.Core.DocumentTypes;

namespace RpdFos.Core.Templates;

/// <summary>Поле, для которого шаблон задаёт значение по умолчанию: JSON Pointer и подпись «Раздел → Поле».</summary>
public sealed record DefaultField(string Pointer, string Title);

/// <summary>
/// Значения по умолчанию шаблона документа (<c>default_payload</c>, раздел 2.4 проекта): постоянные пункты вуза —
/// интернет-ресурсы, программное обеспечение, материально-техническое обеспечение, стандартные виды самостоятельной
/// работы. Задаются только для полей с пометкой <c>x-template-default</c> в схеме; новый документ получает их
/// предзаполненными, дальше автор дополняет и удаляет пункты.
/// </summary>
public static class TemplateDefaults
{
    public const string Marker = "x-template-default";

    /// <summary>Поля схемы, разрешённые для умолчаний, — в порядке схемы. Внутрь списков обход не заходит.</summary>
    public static IReadOnlyList<DefaultField> Fields(JsonElement schema, Func<string, JsonElement?> resolve)
    {
        var result = new List<DefaultField>();
        var root = schema;
        Walk(SchemaInspector.Deref(schema, ref root, resolve), root, "", [], 0);
        return result;

        void Walk(JsonElement? node, JsonElement currentRoot, string path, List<string> titles, int depth)
        {
            if (node is not { ValueKind: JsonValueKind.Object } n || depth > 16 || !n.TryGetProperty("properties", out var props)) return;
            foreach (var p in props.EnumerateObject())
            {
                var childRoot = currentRoot;
                if (SchemaInspector.Deref(p.Value, ref childRoot, resolve) is not { ValueKind: JsonValueKind.Object } child) continue;
                var childTitles = titles.Append(Text(child, "title") ?? p.Name).ToList();
                if (IsMarked(p.Value) || IsMarked(child))
                    result.Add(new DefaultField($"{path}/{p.Name}", string.Join(" → ", childTitles)));
                else
                    Walk(child, childRoot, $"{path}/{p.Name}", childTitles, depth + 1);
            }
        }
    }

    /// <summary>
    /// Умолчания только из разрешённых полей; пустые и <c>null</c> значения выпадают. <paramref name="unknown"/> —
    /// места, которые не разрешены для умолчаний: их не сохраняют, а показывают методисту.
    /// </summary>
    public static JsonObject Normalize(JsonNode? defaults, IReadOnlyList<DefaultField> fields, out IReadOnlyList<string> unknown)
    {
        var result = new JsonObject();
        foreach (var field in fields)
            if (At(defaults, field.Pointer) is { } value && !IsEmpty(value))
                Set(result, field.Pointer, value.DeepClone());

        var outside = new List<string>();
        Collect(defaults, "");
        unknown = outside;
        return result;

        void Collect(JsonNode? node, string path)
        {
            if (node is not JsonObject o) return;
            foreach (var (name, child) in o)
            {
                var pointer = $"{path}/{name}";
                if (fields.Any(f => f.Pointer == pointer)) continue;
                if (fields.Any(f => f.Pointer.StartsWith(pointer + "/", StringComparison.Ordinal))) Collect(child, pointer);
                else if (child is not null && !IsEmpty(child)) outside.Add(pointer);
            }
        }
    }

    /// <summary>
    /// Проверка умолчаний перед сохранением в шаблон: только разрешённые поля, и каждый пункт полон — полная проверка
    /// по схеме (обязательные поля пункта, длины), но лишь внутри разрешённых полей: остального документа
    /// в умолчаниях нет. Пусто — можно сохранять <paramref name="normalized"/>.
    /// </summary>
    public static IReadOnlyList<DocumentValidationError> Check(string schemaJson, JsonNode? defaults, Func<string, JsonElement?> resolve,
        IDocumentValidator validator, out JsonObject normalized)
    {
        var fields = Fields(JsonDocument.Parse(schemaJson).RootElement, resolve);
        normalized = Normalize(defaults, fields, out var unknown);
        var errors = unknown.Select(p => new DocumentValidationError(p, NotAllowed)).ToList();
        var payload = JsonDocument.Parse(normalized.ToJsonString()).RootElement;
        errors.AddRange(validator.Validate(schemaJson, payload, ValidationMode.Submit)
            .Where(e => fields.Any(f => e.Path == f.Pointer || e.Path.StartsWith(f.Pointer + "/", StringComparison.Ordinal))));
        return errors;
    }

    public const string NotAllowed = "Для этого поля значения по умолчанию не задаются";

    /// <summary>
    /// Умолчания — в содержимое нового документа: значение по умолчанию ставится на место поля поверх
    /// предзаполненного. Места, не предусмотренные учебным планом (<paramref name="hidden"/>), пропускаются.
    /// </summary>
    public static void Apply(JsonObject payload, string? defaultsJson, IReadOnlyList<DefaultField> fields, IReadOnlyList<string> hidden)
    {
        if (string.IsNullOrWhiteSpace(defaultsJson) || JsonNode.Parse(defaultsJson) is not JsonObject defaults) return;
        foreach (var field in fields)
        {
            if (hidden.Any(h => field.Pointer == h || field.Pointer.StartsWith(h + "/", StringComparison.Ordinal))) continue;
            if (At(defaults, field.Pointer) is { } value && !IsEmpty(value)) Set(payload, field.Pointer, value.DeepClone());
        }
    }

    private static bool IsMarked(JsonElement node) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(Marker, out var m) && m.ValueKind == JsonValueKind.True;

    private static bool IsEmpty(JsonNode value) => value switch
    {
        JsonArray a => a.Count == 0,
        JsonObject o => o.Count == 0,
        JsonValue v => v.TryGetValue(out string? s) && string.IsNullOrWhiteSpace(s),
        _ => false
    };

    private static JsonNode? At(JsonNode? node, string pointer)
    {
        foreach (var segment in pointer.TrimStart('/').Split('/'))
            node = node is JsonObject o && o.TryGetPropertyValue(segment, out var next) ? next : null;
        return node;
    }

    private static void Set(JsonObject target, string pointer, JsonNode value)
    {
        var segments = pointer.TrimStart('/').Split('/');
        var current = target;
        foreach (var segment in segments[..^1])
        {
            if (current[segment] is not JsonObject next) current[segment] = next = new JsonObject();
            current = next;
        }
        current[segments[^1]] = value;
    }

    private static string? Text(JsonElement node, string name) =>
        node.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
