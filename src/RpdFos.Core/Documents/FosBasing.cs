using System.Text.Json.Nodes;

namespace RpdFos.Core.Documents;

/// <summary>
/// «Создать на основе» для ФОС (этап 7, B10; решение 4): после общего переноса ввода автора (<see cref="DocumentBasing"/>) банк
/// заданий, билеты, оценочные средства, текущий контроль, шкала и паспорт уже лежат в новом ФОС, а наследуемое — снимок —
/// собрано из РПД цели. Здесь ссылки переносимого приводятся к этой РПД: индикаторы заданий и средств и строки паспорта
/// остаются по существующим кодам, а то, чего в РПД цели нет, убирается и попадает в список неперенесённого — иначе паспорт
/// ссылался бы на несуществующие разделы и индикаторы. Ссылки паспорта на задания банка (группа, диапазон номеров) не
/// трогаются: банк переносится целиком, и они остаются верными. Правило без базы — проверяется на содержимом.
/// </summary>
public static class FosBasing
{
    /// <param name="payload">Содержимое нового ФОС: снимок РПД цели уже записан, ввод автора основы перенесён.</param>
    /// <returns>Что убрано из перенесённого, — строками для списка неперенесённого.</returns>
    public static IReadOnlyList<string> Adapt(JsonObject payload)
    {
        var notes = new List<string>();
        var link = payload[FosSnapshot.LinkSection];
        var indicators = Strings(payload[FosSnapshot.IndicatorsSection], "code").ToHashSet(StringComparer.Ordinal);
        var competences = Strings(link?["competences"], "code").ToHashSet(StringComparer.Ordinal);
        var sections = Numbers(link?["sections"], "number").ToHashSet();

        if (payload["task_bank"] is JsonArray bank)
            foreach (var group in bank.OfType<JsonObject>())
            {
                if (group["tasks"] is not JsonArray tasks) continue;
                var gone = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var task in tasks.OfType<JsonObject>()) Filter(task, "indicator_codes", indicators, gone);
                if (gone.Count > 0)
                    notes.Add($"Банк заданий → «{Text(group["group"])}»: индикаторы {string.Join(", ", gone)} убраны из заданий — их нет в РПД");
            }

        if (payload["evaluation_means"] is JsonArray means)
            foreach (var item in means.OfType<JsonObject>())
            {
                var gone = new SortedSet<string>(StringComparer.Ordinal);
                Filter(item, "indicator_codes", indicators, gone);
                if (gone.Count > 0)
                    notes.Add($"Оценочные средства → «{Text(item["title"])}»: индикаторы {string.Join(", ", gone)} убраны — их нет в РПД");
            }

        if (payload["passport"] is JsonObject passport)
        {
            if (passport["sections"] is JsonArray rows)
                foreach (var row in rows.OfType<JsonObject>().ToList())
                {
                    var number = Number(row["section_number"]);
                    if (number is null || !sections.Contains(number.Value))
                    {
                        rows.Remove(row);
                        notes.Add($"Паспорт → разделы: раздел {number} не перенесён — его нет в РПД");
                        continue;
                    }
                    var gone = new SortedSet<string>(StringComparer.Ordinal);
                    Filter(row, "competence_codes", competences, gone, keepEmpty: true);
                    if (gone.Count > 0)
                        notes.Add($"Паспорт → раздел {number}: компетенции {string.Join(", ", gone)} убраны — их нет в РПД");
                }

            if (passport["indicators"] is JsonArray lines)
                foreach (var line in lines.OfType<JsonObject>().ToList())
                {
                    var code = Text(line["indicator_code"]);
                    if (!indicators.Contains(code))
                    {
                        lines.Remove(line);
                        notes.Add($"Паспорт → индикаторы: строка {code} не перенесена — индикатора нет в РПД");
                        continue;
                    }
                    if (line["rpd_section_numbers"] is JsonArray numbers)
                    {
                        var absent = numbers.Select(Number).OfType<int>().Where(n => !sections.Contains(n)).Distinct().Order().ToList();
                        foreach (var node in numbers.Where(n => Number(n) is int v && absent.Contains(v)).ToList()) numbers.Remove(node);
                        if (absent.Count > 0)
                            notes.Add($"Паспорт → индикатор {code}: разделы РПД {string.Join(", ", absent)} убраны — их нет в РПД");
                    }
                }
        }
        return notes;
    }

    // Оставляет в списке только коды, которые есть в РПД; убранные — в gone. Пустой список задания лучше его отсутствия.
    private static void Filter(JsonObject owner, string name, ISet<string> allowed, ISet<string> gone, bool keepEmpty = false)
    {
        if (owner[name] is not JsonArray codes) return;
        foreach (var node in codes.ToList())
        {
            var code = Text(node);
            if (allowed.Contains(code)) continue;
            gone.Add(code);
            codes.Remove(node);
        }
        if (codes.Count == 0 && !keepEmpty) owner.Remove(name);
    }

    private static IEnumerable<string> Strings(JsonNode? list, string key) =>
        list is JsonArray a ? a.Select(i => Text(i?[key])).Where(s => s.Length > 0) : [];

    private static IEnumerable<int> Numbers(JsonNode? list, string key) =>
        list is JsonArray a ? a.Select(i => Number(i?[key])).OfType<int>() : [];

    private static string Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static int? Number(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;
}
