using System.Text.Json;

namespace RpdFos.Core.DocumentTypes;

/// <summary>x-расширение схемы с местом, где оно стоит (JSON Pointer внутри схемы).</summary>
public sealed record SchemaExtension(string Location, string Keyword, string Value);

/// <summary>
/// Обход JSON Schema типа документа: x-расширения и поиск поля содержимого по JSON Pointer
/// (с переходом по <c>$ref</c>, в том числе в схему другого типа — ФОС ссылается на РПД).
/// </summary>
public static class SchemaInspector
{
    /// <summary>Все x-source, x-validation-rule (строка или массив) и источники x-visible-if схемы.</summary>
    public static IReadOnlyList<SchemaExtension> Extensions(JsonElement schema)
    {
        var result = new List<SchemaExtension>();
        Walk(schema, "");
        return result;

        void Walk(JsonElement node, string location)
        {
            if (node.ValueKind == JsonValueKind.Array)
            {
                var i = 0;
                foreach (var item in node.EnumerateArray()) Walk(item, $"{location}/{i++}");
                return;
            }
            if (node.ValueKind != JsonValueKind.Object) return;

            foreach (var p in node.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "x-source" when p.Value.ValueKind == JsonValueKind.String:
                        result.Add(new(location, p.Name, p.Value.GetString()!));
                        break;
                    case "x-validation-rule":
                        IEnumerable<JsonElement> rules = p.Value.ValueKind == JsonValueKind.Array ? p.Value.EnumerateArray() : [p.Value];
                        foreach (var rule in rules)
                            result.Add(new(location, p.Name, rule.GetString() ?? ""));
                        break;
                    case "x-visible-if" when p.Value.TryGetProperty("source", out var source):
                        result.Add(new(location, p.Name, source.GetString() ?? ""));
                        break;
                    default:
                        Walk(p.Value, $"{location}/{p.Name}");
                        break;
                }
            }
        }
    }

    /// <summary>
    /// Есть ли в схеме поле содержимого документа с таким JSON Pointer (/title_page/discipline_name).
    /// <paramref name="resolve"/> находит схему по её <c>$id</c> — для ссылок на другой тип документа.
    /// </summary>
    public static bool FieldExists(JsonElement schema, string pointer, Func<string, JsonElement?> resolve)
    {
        if (!pointer.StartsWith('/')) return false;
        var root = schema;
        var node = Deref(schema, ref root, resolve);
        foreach (var segment in pointer[1..].Split('/'))
        {
            if (node is not { } n || !n.TryGetProperty("properties", out var props) ||
                !props.TryGetProperty(segment.Replace("~1", "/").Replace("~0", "~"), out var next))
                return false;
            node = Deref(next, ref root, resolve);
        }
        return node is not null;
    }

    // Переход по $ref: «#/…» — внутри текущей схемы, «https://…#/…» — в схеме с таким $id.
    private static JsonElement? Deref(JsonElement node, ref JsonElement root, Func<string, JsonElement?> resolve)
    {
        for (var depth = 0; depth < 16 && node.TryGetProperty("$ref", out var reference); depth++)
        {
            var value = reference.GetString() ?? "";
            var hash = value.IndexOf('#');
            var (id, fragment) = hash < 0 ? (value, "") : (value[..hash], value[(hash + 1)..]);
            if (id.Length > 0)
            {
                if (resolve(id) is not { } other) return null;
                root = other;
            }
            if (Navigate(root, fragment) is not { } target) return null;
            node = target;
        }
        return node;
    }

    private static JsonElement? Navigate(JsonElement root, string fragment)
    {
        var node = root;
        foreach (var segment in fragment.Split('/', StringSplitOptions.RemoveEmptyEntries))
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(segment, out node))
                return null;
        return node;
    }
}
