using System.Text.Json;
using System.Text.Json.Nodes;

namespace RpdFos.Core.DocumentTypes;

/// <summary>
/// Схема типа для клиента форм (этап 7, B7): ссылки на схему другого типа документа — «https://…#/…», как у титульного листа и
/// листа согласования ФОС, — заменены её содержимым. Клиент разбирает только локальные ссылки «#/…»; без подстановки такой раздел
/// рисуется одним текстовым полем с сырым JSON, и автор не может ни прочитать титул, ни ввести должность составителя. Сервер
/// переходит по ссылкам сам (<see cref="SchemaInspector"/>), поэтому проверка документа и метаданные от подстановки не зависят.
/// </summary>
public static class SchemaBundle
{
    // Ссылки могут вести друг в друга по кругу; глубже схемы типов не бывают.
    private const int MaxDepth = 16;

    /// <summary>Схема типа с подставленными ссылками на схемы других типов; локальные ссылки основной схемы остаются как есть.</summary>
    /// <param name="resolve">Находит схему по её <c>$id</c> (<c>DocumentTypeRegistry.FindSchema</c>).</param>
    public static JsonElement ForClient(string schemaJson, Func<string, JsonElement?> resolve)
    {
        var root = JsonNode.Parse(schemaJson)!;
        Visit(root, null, resolve, 0);
        return JsonDocument.Parse(root.ToJsonString()).RootElement.Clone();
    }

    // foreign — корень схемы, из которой скопирован обходимый кусок: его локальные ссылки «#/…» ведут в его же $defs, а не в
    // основную схему, поэтому внутри подставленного куска подставляются и они. В основной схеме (foreign = null) они не трогаются.
    private static JsonNode? Visit(JsonNode? node, JsonElement? foreign, Func<string, JsonElement?> resolve, int depth)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["$ref"] is JsonValue value && value.TryGetValue(out string? reference) && NeedsInlining(reference, foreign))
                    return Substitute(obj, reference, foreign, resolve, depth);
                foreach (var key in obj.Select(p => p.Key).ToList())
                    if (obj[key] is { } child && Visit(child, foreign, resolve, depth) is { } replaced && !ReferenceEquals(replaced, child))
                        obj[key] = replaced;
                return obj;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                    if (array[i] is { } item && Visit(item, foreign, resolve, depth) is { } replaced && !ReferenceEquals(replaced, item))
                        array[i] = replaced;
                return array;
            default:
                return node;
        }
    }

    private static bool NeedsInlining(string reference, JsonElement? foreign) =>
        !reference.StartsWith('#') || foreign is not null;

    private static JsonNode Substitute(JsonObject refNode, string reference, JsonElement? foreign,
        Func<string, JsonElement?> resolve, int depth)
    {
        if (depth >= MaxDepth) return refNode;

        var hash = reference.IndexOf('#');
        var (id, fragment) = hash < 0 ? (reference, "") : (reference[..hash], reference[(hash + 1)..]);
        JsonElement targetRoot;
        if (id.Length > 0)
        {
            if (resolve(id) is not { } other) return refNode;
            targetRoot = other;
        }
        else
            targetRoot = foreign!.Value;
        if (Navigate(targetRoot, fragment) is not { } target) return refNode;

        var copy = JsonNode.Parse(target.GetRawText())!;
        var inlined = Visit(copy, targetRoot, resolve, depth + 1) ?? copy;
        // Что записано рядом со ссылкой (title, description), важнее подставленного — как в JSON Schema 2019-09 и новее.
        if (inlined is JsonObject result)
            foreach (var (key, sibling) in refNode.ToList())
                if (key != "$ref") result[key] = sibling?.DeepClone();
        return inlined;
    }

    private static JsonElement? Navigate(JsonElement root, string fragment)
    {
        var node = root;
        foreach (var raw in fragment.Split('/', StringSplitOptions.RemoveEmptyEntries))
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(raw.Replace("~1", "/").Replace("~0", "~"), out node))
                return null;
        return node;
    }
}
