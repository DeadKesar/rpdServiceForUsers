using System.Globalization;
using System.Text.Json.Nodes;

namespace RpdFos.Web.Forms.Widgets;

/// <summary>
/// Выбор заданий банка для ссылки паспорта ФОС (этап 7, B6): чистые функции над содержимым. Ссылка — <c>{ group, from?, to? }</c>:
/// без <c>from</c> и <c>to</c> — вся группа, один номер — <c>from</c> равен <c>to</c>. Новую ссылку можно собрать только на то,
/// что есть в банке: группа, существующий номер, существующий конец диапазона. Уже сохранённая ссылка на исчезнувшее задание
/// остаётся (её нельзя потерять молча), но <see cref="Problem"/> её называет. Файл подключён и к тестам: в браузерном проекте
/// иначе его не проверить.
/// </summary>
public static class TaskRefChoices
{
    /// <summary>Названия групп банка в порядке банка, без пустых и повторов.</summary>
    public static List<string> Groups(JsonNode? bank) =>
        Blocks(bank).Select(b => Text(b["group"])).Where(g => !string.IsNullOrEmpty(g)).Distinct().ToList()!;

    /// <summary>Номера заданий группы по возрастанию; в блоках с одинаковым названием — общие.</summary>
    public static List<int> Numbers(JsonNode? bank, string group) =>
        Blocks(bank).Where(b => Text(b["group"]) == group)
            .SelectMany(b => (b["tasks"] as JsonArray ?? []).OfType<JsonObject>().Select(t => Number(t["number"])))
            .Where(n => n > 0).Distinct().Order().ToList();

    /// <summary>
    /// Ссылка на задания группы. <paramref name="from"/> и <paramref name="to"/> пусты — вся группа (в ней должны быть задания);
    /// указан только <paramref name="from"/> — одно задание; <paramref name="to"/> без <paramref name="from"/> не бывает.
    /// Отказ — null и <paramref name="error"/>: группы или номера нет в банке, конец раньше начала.
    /// </summary>
    public static JsonObject? Build(JsonNode? bank, string? group, int? from, int? to, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(group) || !Groups(bank).Contains(group))
        {
            error = "Выберите группу из банка заданий.";
            return null;
        }
        var numbers = Numbers(bank, group);
        if (from is null && to is null)
        {
            if (numbers.Count == 0) error = $"В группе «{group}» нет заданий.";
            else return new JsonObject { ["group"] = group };
            return null;
        }
        if (from is null)
        {
            error = "Укажите первое задание диапазона.";
            return null;
        }
        var last = to ?? from.Value;
        if (!numbers.Contains(from.Value) || !numbers.Contains(last))
        {
            error = $"В группе «{group}» нет задания № {(numbers.Contains(from.Value) ? last : from.Value)}.";
            return null;
        }
        if (last < from.Value)
        {
            error = "Конец диапазона раньше его начала.";
            return null;
        }
        return new JsonObject { ["group"] = group, ["from"] = from.Value, ["to"] = last };
    }

    /// <summary>Чем сохранённая ссылка не сходится с банком (группы или заданий нет); у верной — null.</summary>
    public static string? Problem(JsonNode? bank, JsonNode? reference)
    {
        if (reference is not JsonObject r || Text(r["group"]) is not { Length: > 0 } group) return "Не указана группа.";
        if (!Groups(bank).Contains(group)) return $"Группы «{group}» нет в банке.";
        var numbers = Numbers(bank, group);
        var (from, to) = (OptionalNumber(r["from"]), OptionalNumber(r["to"]));
        if (from is null && to is null) return numbers.Count == 0 ? $"В группе «{group}» нет заданий." : null;
        var missing = new[] { from, to }.OfType<int>().Distinct().Where(n => !numbers.Contains(n)).ToList();
        if (missing.Count > 0) return $"В группе «{group}» нет задания № {string.Join(", ", missing)}.";
        return from is not null && to is not null && to < from ? "Конец диапазона раньше его начала." : null;
    }

    /// <summary>Подпись ссылки: «Вопросы к зачёту, № 1–5», «Вопросы к зачёту, № 3», «Вопросы к зачёту (все)».</summary>
    public static string Describe(JsonNode? reference)
    {
        if (reference is not JsonObject r) return "";
        var group = Text(r["group"]) ?? "";
        var (from, to) = (OptionalNumber(r["from"]), OptionalNumber(r["to"]));
        return (from, to) switch
        {
            (null, null) => $"{group} (все)",
            ({ } a, { } b) when a != b => $"{group}, № {a}–{b}",
            ({ } a, _) => $"{group}, № {a}",
            (_, { } b) => $"{group}, № {b}",
        };
    }

    /// <summary>Та же ссылка уже есть в списке — повторять её не нужно.</summary>
    public static bool Contains(JsonArray references, JsonObject candidate) =>
        references.OfType<JsonObject>().Any(r => Text(r["group"]) == Text(candidate["group"]) &&
            OptionalNumber(r["from"]) == OptionalNumber(candidate["from"]) && (OptionalNumber(r["to"]) ?? OptionalNumber(r["from"])) ==
            (OptionalNumber(candidate["to"]) ?? OptionalNumber(candidate["from"])));

    private static IEnumerable<JsonObject> Blocks(JsonNode? bank) => (bank as JsonArray ?? []).OfType<JsonObject>();

    // Значение с сервера и созданное в клиенте хранятся по-разному (JsonElement и примитив): чтение через текст одинаково.
    private static int? OptionalNumber(JsonNode? node) =>
        node is JsonValue && int.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;

    private static int Number(JsonNode? node) => OptionalNumber(node) ?? 0;

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : node?.ToString();
}
