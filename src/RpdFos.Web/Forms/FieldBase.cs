using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components;

namespace RpdFos.Web.Forms;

/// <summary>
/// Поле формы документа: значение — узел содержимого в контейнере <see cref="Parent"/> под ключом <see cref="Key"/>
/// (имя свойства объекта или номер элемента списка), <see cref="Schema"/> — его подсхема. Так устроены и
/// универсальный обработчик, и виджеты из реестра — новый виджет получает те же параметры.
/// </summary>
public abstract class FieldBase : ComponentBase
{
    [Parameter, EditorRequired] public SchemaContext Context { get; set; } = default!;
    [Parameter, EditorRequired] public JsonElement Schema { get; set; }
    [Parameter, EditorRequired] public JsonNode Parent { get; set; } = default!;
    [Parameter, EditorRequired] public string Key { get; set; } = "";
    [Parameter, EditorRequired] public string Path { get; set; } = "";
    [Parameter] public string? Label { get; set; }

    /// <summary>
    /// Прикрепить контейнер к содержимому перед первой правкой. Пустой раздел-объект не добавляется в содержимое
    /// заранее: пустой «Протокол заседания кафедры» уже считался бы начатым и требовал реквизитов.
    /// </summary>
    [Parameter] public Action? EnsureParent { get; set; }

    protected JsonNode? Value => Parent switch
    {
        JsonObject o => o[Key],
        JsonArray a when int.TryParse(Key, out var i) && i < a.Count => a[i],
        _ => null
    };

    protected void SetValue(JsonNode? value)
    {
        Assign(value);
        Context.Changed();
    }

    /// <summary>
    /// Значение по мере ввода (oninput) — сразу в содержимое: автосохранение во время набора сохранит то, что на экране.
    /// </summary>
    protected void InputValue(JsonNode? value)
    {
        Assign(value);
        Context.Edited();
    }

    private void Assign(JsonNode? value)
    {
        EnsureParent?.Invoke();
        switch (Parent)
        {
            case JsonObject o when value is null: o.Remove(Key); break;
            case JsonObject o: o[Key] = value; break;
            case JsonArray a when int.TryParse(Key, out var i) && i < a.Count: a[i] = value; break;
        }
    }

    protected bool ReadOnly => Context.IsReadOnly(Path, Schema);
    protected bool FromPlan => Context.FromPlan(Path, Schema);
    protected string Title => Label ?? SchemaContext.Text(Schema, "title") ?? SchemaContext.Text(Schema, "description") ?? Key;

    /// <summary>Пояснение под полем, если оно не совпадает с подписью.</summary>
    protected string? Hint => SchemaContext.Text(Schema, "description") is { } d && d != Title ? d : null;

    protected string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : node?.ToString();

    /// <summary>
    /// Целое из содержимого. Значение, пришедшее с сервера, и созданное в клиенте хранятся по-разному
    /// (JsonElement и примитив .NET): чтение через текст одинаково для обоих.
    /// </summary>
    protected static int Number(JsonNode? node) =>
        node is JsonValue && int.TryParse(node.ToString(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var i) ? i : 0;

    /// <summary>Список в содержимом — существующий или новый, привязанный к месту поля.</summary>
    protected JsonArray EnsureArray()
    {
        if (Value is JsonArray existing) return existing;
        var created = new JsonArray();
        SetValue(created);
        return created;
    }

    protected JsonElement Items => Context.Resolve(Schema.TryGetProperty("items", out var items) ? items : default);
}
