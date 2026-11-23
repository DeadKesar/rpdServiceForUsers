using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RpdFos.Web.Forms;

/// <summary>
/// № занятия в таблицах практических занятий и лабораторных работ (этап 7.1). У нового шаблона номер — текст: «3», диапазон
/// «1–2» (одна работа на несколько занятий) или перечень «4, 5»; у прежних шаблонов — целое число.
/// </summary>
public static partial class ClassNumbers
{
    /// <summary>
    /// Номер новой строки — следующий за последним числом номера предыдущей записи: «1–2» → 3, «4, 5» → 6. Если у предыдущей
    /// записи номера нет или он не разбирается, берётся ближайшая выше; ни одного номера — по порядку (число строк плюс один).
    /// </summary>
    public static int Next(JsonArray rows)
    {
        for (var i = rows.Count - 1; i >= 0; i--)
            if (LastNumber(rows[i] is JsonObject row ? row["number"] : null) is { } last)
                return last + 1;
        return rows.Count + 1;
    }

    // Последнее число в записи: у «1–2» это 2, у «4, 5» — 5, у прежнего целого числа — оно само.
    private static int? LastNumber(JsonNode? value)
    {
        var text = value switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonValue v when v.TryGetValue<int>(out var n) => n.ToString(),
            _ => null
        };
        if (text is null) return null;
        var found = Digits().Matches(text);
        return found.Count > 0 && int.TryParse(found[^1].Value, out var last) ? last : null;
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();
}
