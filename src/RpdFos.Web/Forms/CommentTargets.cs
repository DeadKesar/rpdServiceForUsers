using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RpdFos.Web.Forms;

/// <summary>
/// Записи списков внутри виджета, к которым можно оставить замечание (этап 8, A15). Виджет рисует записи сам, поэтому запись
/// выбирается в форме замечания: одно место вместо кнопки у каждой записи каждого виджета. Записи вложенных списков (задания
/// группы банка) — тоже; списки простых значений внутри записи (коды, формы контроля) — часть записи.
/// </summary>
public static partial class CommentTargets
{
    private const int TitleLength = 70;

    private static readonly string[] Names = ["citation", "topic_title", "title", "name", "group", "text"];

    /// <param name="Path">Место записи в содержимом (JSON Pointer).</param>
    /// <param name="Title">Номера по порядку вложенности и начало текста записи.</param>
    /// <param name="Node">Сама запись: выбор держится её, а не номера — список могут переставить до отправки замечания.</param>
    public sealed record Target(string Path, string Title, JsonNode Node);

    public static IReadOnlyList<Target> Records(JsonObject payload, string path)
    {
        JsonNode? node = payload;
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            node = node switch
            {
                JsonObject o => o[segment],
                JsonArray a when int.TryParse(segment, out var i) && i >= 0 && i < a.Count => a[i],
                _ => null
            };
        var result = new List<Target>();
        if (node is JsonArray list) AddRecords(list, path, "", result);
        else if (node is JsonObject) AddNested(node, path, "", result);
        return result;
    }

    private static void AddRecords(JsonArray list, string path, string prefix, List<Target> result)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is not { } record) continue;
            var (at, number) = ($"{path}/{i}", $"{prefix}№ {i + 1}");
            var text = Title(record);
            result.Add(new(at, text == "" ? number : $"{number} — {text}", record));
            AddNested(record, at, number + " → ", result);
        }
    }

    private static void AddNested(JsonNode node, string path, string prefix, List<Target> result)
    {
        if (node is not JsonObject record) return;
        foreach (var (name, value) in record)
            if (value is JsonArray nested && nested.Any(n => n is JsonObject)) AddRecords(nested, $"{path}/{name}", prefix, result);
    }

    // Подпись записи — её название; у записей без такого поля — самый длинный текст (порядок свойств хранилище не сохраняет).
    private static string Title(JsonNode record)
    {
        var text = record is JsonObject o
            ? Names.Select(n => Text(o[n])).FirstOrDefault(t => t != "") ?? o.Select(p => Text(p.Value)).MaxBy(t => t.Length) ?? ""
            : Text(record);
        return text.Length <= TitleLength ? text : text[..TitleLength].TrimEnd() + "…";
    }

    private static string Text(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s)
            ? Spaces().Replace(WebUtility.HtmlDecode(Tags().Replace(s, " ")), " ").Trim()
            : "";

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
