using System.Text.Json;
using System.Text.Json.Nodes;
using RpdFos.Web.Models;

namespace RpdFos.Web.Forms;

/// <summary>
/// Общее для полей формы документа: схема из шаблона, редактируемое содержимое, метаданные (поля только для чтения,
/// скрытые планом места, кандидаты в компетенции) и уведомление об изменении — от него зависит автосохранение.
/// </summary>
public sealed class SchemaContext(JsonElement root, JsonObject payload, DocumentMetadata metadata, Action changed)
{
    public JsonElement Root { get; } = root;
    public JsonObject Payload { get; } = payload;
    public DocumentMetadata Metadata { get; } = metadata;

    /// <summary>Документ целиком только для чтения: статус не позволяет правку или пользователь — не автор.</summary>
    public bool Locked { get; set; }

    public void Changed() => changed();

    // Поле из плана или закрытое статусом: само место или вложенное в такое место.
    public bool IsReadOnly(string path, JsonElement schema) =>
        Locked || schema.TryGetProperty("readOnly", out var r) && r.ValueKind == JsonValueKind.True ||
        Metadata.ReadOnlyPaths.Any(p => path == p || path.StartsWith(p + "/", StringComparison.Ordinal));

    /// <summary>Поле из учебного плана — для пометки источника.</summary>
    public bool FromPlan(string path, JsonElement schema) =>
        schema.TryGetProperty("x-source", out var s) && s.GetString() is { } source &&
        (source.StartsWith("parser.") || source.StartsWith("reference.")) ||
        Metadata.ReadOnlyPaths.Any(p => path == p || path.StartsWith(p + "/", StringComparison.Ordinal));

    public bool IsHidden(string path) => Metadata.HiddenPaths.Contains(path);

    /// <summary>Переход по локальным ссылкам схемы «#/$defs/…».</summary>
    public JsonElement Resolve(JsonElement node)
    {
        for (var depth = 0; depth < 16 && node.ValueKind == JsonValueKind.Object && node.TryGetProperty("$ref", out var reference); depth++)
        {
            var value = reference.GetString() ?? "";
            if (!value.StartsWith("#/")) break;
            var target = Root;
            foreach (var segment in value[2..].Split('/'))
                if (!target.TryGetProperty(segment, out target)) return node;
            node = target;
        }
        return node;
    }

    /// <summary>
    /// Подпись места в содержимом для пользователя: «Протокол заседания кафедры → Дата заседания», элементы
    /// списков — по номеру. Первый сегмент — раздел: он и так виден в заголовке.
    /// </summary>
    public string FieldTitle(string path)
    {
        var node = Root;
        var titles = new List<string>();
        foreach (var raw in path.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(raw, out var index) && node.ValueKind == JsonValueKind.Object && node.TryGetProperty("items", out var items))
            {
                node = Resolve(items);
                titles.Add($"№ {index + 1}");
            }
            else if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty("properties", out var props) &&
                     props.TryGetProperty(raw, out var next))
            {
                node = Resolve(next);
                titles.Add(Text(node, "title") ?? raw);
            }
            else
            {
                titles.Add(raw);
                break;
            }
        }
        return titles.Count > 1 ? string.Join(" → ", titles.Skip(1)) : titles.FirstOrDefault() ?? path;
    }

    public static string? Text(JsonElement schema, string name) =>
        schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public static string Type(JsonElement schema) => Text(schema, "type") ?? "string";

    /// <summary>Подпись значения перечисления: <c>x-enum-titles</c> схемы или сам код.</summary>
    public static string EnumTitle(JsonElement schema, string value) =>
        schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("x-enum-titles", out var titles) &&
        titles.TryGetProperty(value, out var title)
            ? title.GetString() ?? value
            : value;
}
