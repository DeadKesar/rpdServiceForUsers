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

    /// <summary>
    /// Подсхема места в содержимом документа (JSON Pointer экземпляра: /competences/0/code) — по properties,
    /// items и $ref. Схемы типов не используют oneOf/anyOf, поэтому место однозначно; нет такого места — null.
    /// </summary>
    public static JsonElement? SubschemaAt(JsonElement schema, string instancePointer, Func<string, JsonElement?> resolve)
    {
        var root = schema;
        var node = Deref(schema, ref root, resolve);
        if (instancePointer.Length == 0) return node;
        foreach (var raw in instancePointer.TrimStart('/').Split('/'))
        {
            if (node is not { ValueKind: JsonValueKind.Object } n) return null;
            var segment = raw.Replace("~1", "/").Replace("~0", "~");
            if (n.TryGetProperty("properties", out var props) && props.TryGetProperty(segment, out var next))
                node = Deref(next, ref root, resolve);
            else if (int.TryParse(segment, out _) && n.TryGetProperty("items", out var items))
                node = Deref(items, ref root, resolve);
            else
                return null;
        }
        return node;
    }

    /// <summary>
    /// Поля с <c>x-required-for-status</c>: шаблон пути (элемент массива — «*») и статус, при переходе в который
    /// поле обязательно.
    /// </summary>
    public static IReadOnlyList<(string Path, string Status)> RequiredForStatus(JsonElement schema, Func<string, JsonElement?> resolve)
    {
        var result = new List<(string, string)>();
        var root = schema;
        Walk(Deref(schema, ref root, resolve), root, "", 0);
        return result;

        void Walk(JsonElement? node, JsonElement currentRoot, string path, int depth)
        {
            // Глубина ограничена: $defs могут ссылаться сами на себя.
            if (node is not { ValueKind: JsonValueKind.Object } n || depth > 32) return;
            if (n.TryGetProperty("properties", out var props))
                foreach (var p in props.EnumerateObject())
                {
                    var childRoot = currentRoot;
                    var child = Deref(p.Value, ref childRoot, resolve);
                    var childPath = $"{path}/{p.Name.Replace("~", "~0").Replace("/", "~1")}";
                    var status = Status(p.Value) ?? (child is { } c ? Status(c) : null);
                    if (status is not null) result.Add((childPath, status));
                    Walk(child, childRoot, childPath, depth + 1);
                }
            if (n.TryGetProperty("items", out var items))
            {
                var itemsRoot = currentRoot;
                Walk(Deref(items, ref itemsRoot, resolve), itemsRoot, $"{path}/*", depth + 1);
            }
        }

        static string? Status(JsonElement node) =>
            node.TryGetProperty("x-required-for-status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
    }

    // Переход по $ref: «#/…» — внутри текущей схемы, «https://…#/…» — в схеме с таким $id.
    internal static JsonElement? Deref(JsonElement node, ref JsonElement root, Func<string, JsonElement?> resolve)
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
