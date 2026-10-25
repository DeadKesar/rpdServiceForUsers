using System.Text.Json;
using System.Text.Json.Nodes;
using RpdFos.Web.Models;

namespace RpdFos.Web.Forms;

/// <summary>
/// Общее для полей формы документа: схема из шаблона, редактируемое содержимое, метаданные (поля только для чтения,
/// скрытые планом места, кандидаты в компетенции) и уведомления о правках — от них зависят автосохранение
/// и подтверждение ухода со страницы.
/// </summary>
public sealed class SchemaContext(JsonElement root, JsonObject payload, DocumentMetadata metadata, Action changed, Action edited)
{
    public JsonElement Root { get; } = root;
    public JsonObject Payload { get; } = payload;
    public DocumentMetadata Metadata { get; } = metadata;

    /// <summary>Документ целиком только для чтения: статус не позволяет правку или пользователь — не автор.</summary>
    public bool Locked { get; set; }

    /// <summary>
    /// Значения по умолчанию шаблона — стандартные пункты для подсказок (виды самостоятельной работы). На странице
    /// шаблона это само редактируемое содержимое.
    /// </summary>
    public JsonObject? Defaults { get; set; }

    /// <summary>
    /// Форма значений по умолчанию на странице шаблона: часть полей имеет смысл только в документе (разделы
    /// тематического плана, часы по семестрам) — виджеты их не показывают.
    /// </summary>
    public bool IsTemplate { get; set; }

    /// <summary>Замечания методиста к документу — показываются у полей.</summary>
    public List<CommentItem> Comments { get; set; } = [];

    /// <summary>Методист на проверке оставляет замечания к полям.</summary>
    public bool CanComment { get; set; }

    /// <summary>Автор на доработке отмечает замечания разрешёнными.</summary>
    public bool CanResolve { get; set; }

    /// <summary>Добавить замечание к полю (место, текст); false — не добавлено, причину покажет страница.</summary>
    public Func<string, string, Task<bool>>? AddComment { get; set; }

    public Func<CommentItem, Task>? ResolveComment { get; set; }

    /// <summary>Замечания к самому месту, а для <paramref name="subtree"/> — и к вложенным в него полям.</summary>
    public IEnumerable<CommentItem> CommentsAt(string path, bool subtree) =>
        Comments.Where(c => c.FieldPath == path || subtree && c.FieldPath.StartsWith(path + "/", StringComparison.Ordinal));

    /// <summary>id элемента поля на странице — для перехода к полю из списка замечаний и ошибок.</summary>
    public static string Anchor(string path) => "field" + path.Replace('/', '-');

    /// <summary>Правка завершена (уход из поля, кнопка, флажок): форма перерисовывается целиком — итоги, заполненность.</summary>
    public void Changed() => changed();

    /// <summary>
    /// Ввод в поле, из которого пользователь ещё не ушёл (oninput): правка сразу отмечается несохранённой — иначе
    /// закрытие вкладки или переход на другой сайт не спросили бы подтверждения. Форма на каждую клавишу не перерисовывается.
    /// </summary>
    public void Edited() => edited();

    /// <summary>Пользователь исправляет поля из учебного плана: методист на проверке и после приёма, администратор (этап 5.1).</summary>
    public bool CorrectPlan { get; set; }

    /// <summary>Исправления полей из плана — кто, когда, было, стало.</summary>
    public List<PlanCorrection> Corrections { get; set; } = [];

    // Поле из плана открыто только исправляющему; остальное — по статусу: само место или вложенное в закрытое место.
    public bool IsReadOnly(string path, JsonElement schema)
    {
        if (IsPlanField(path, schema)) return !CorrectPlan;
        return Locked || Metadata.ReadOnlyPaths.Any(p => path == p || path.StartsWith(p + "/", StringComparison.Ordinal));
    }

    private bool IsPlanField(string path, JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("readOnly", out var r) && r.ValueKind == JsonValueKind.True ||
        Metadata.PlanValues is { } values && values.Keys.Any(p => path == p || path.StartsWith(p + "/", StringComparison.Ordinal));

    /// <summary>
    /// Пометка исправленного поля из плана: «изменено методистом, в плане: 24»; значение как в плане — null. В печати
    /// пометок нет.
    /// </summary>
    public string? PlanMark(string path, JsonNode? current)
    {
        if (PlanValueAt(path) is not var (found, plan) || !found) return null;
        if (JsonNode.DeepEquals(Normalize(plan), Normalize(current))) return null;
        return $"изменено методистом, в плане: {PlanText(plan)}";
    }

    /// <summary>Кто и когда последним исправлял это место — для подсказки у пометки.</summary>
    public string? CorrectionInfo(string path) =>
        Corrections.LastOrDefault(c => c.Path == path || c.Path.StartsWith(path + "/", StringComparison.Ordinal)) is { } c
            ? $"{c.ChangedBy ?? "—"}, {c.ChangedAt.ToLocalTime():dd.MM.yyyy HH:mm}"
            : null;

    // Значение по плану на месте path: поле плана целиком или его часть («семестр 1 → лекции»).
    private (bool Found, JsonNode? Value)? PlanValueAt(string path)
    {
        if (Metadata.PlanValues is not { } values) return null;
        var root = values.Keys.FirstOrDefault(p => path == p || path.StartsWith(p + "/", StringComparison.Ordinal));
        if (root is null) return null;
        JsonNode? node = values[root] is { } v ? JsonNode.Parse(v.GetRawText()) : null;
        foreach (var segment in path[root.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries))
            node = node switch
            {
                JsonObject o => o[segment],
                JsonArray a when int.TryParse(segment, out var i) && i < a.Count => a[i],
                _ => null
            };
        return (true, node);
    }

    // Число из формы и из сервера хранятся по-разному — сравнение через текст JSON.
    private static JsonNode? Normalize(JsonNode? node) => node is null ? null : JsonNode.Parse(node.ToJsonString());

    public static string PlanText(JsonNode? value) => value switch
    {
        null => "—",
        JsonArray a => a.Count == 0 ? "—" : string.Join(", ", a.Select(PlanText)),
        JsonValue v when v.TryGetValue(out string? s) => s.Length == 0 ? "—" : s,
        _ => value.ToJsonString()
    };

    /// <summary>
    /// Поле из учебного плана — для пометки источника: закрыто схемой (<c>readOnly</c>). Предзаполненные, но
    /// редактируемые поля (учебный год) пометки не получают — их меняет автор.
    /// </summary>
    public bool FromPlan(string path, JsonElement schema) =>
        schema.TryGetProperty("readOnly", out var r) && r.ValueKind == JsonValueKind.True ||
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
