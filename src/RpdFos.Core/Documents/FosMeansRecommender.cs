using System.Text.Json.Nodes;
using RpdFos.Core.DocumentTypes;

namespace RpdFos.Core.Documents;

/// <summary>
/// Что предложено оценочным средствам: <paramref name="Rows"/> — строки раздела <c>evaluation_means</c> (тип, наименование,
/// заготовка описания, индикаторы); <paramref name="UnmatchedForms"/> — формы текущего контроля РПД, для которых в файле
/// соответствий типового средства нет; <paramref name="Indicators"/> — сколько индикаторов получили предложение;
/// <paramref name="WithoutSource"/> — индикаторы, для которых ничего не нашлось (нет ни разделов, ни аттестации).
/// </summary>
public sealed record MeansSuggestion(
    IReadOnlyList<JsonObject> Rows, IReadOnlyList<string> UnmatchedForms, int Indicators, IReadOnlyList<string> WithoutSource)
{
    /// <summary>Сообщение автору: что добавлено и что осталось ему.</summary>
    public string Notice
    {
        get
        {
            if (Rows.Count == 0 && WithoutSource.Count == 0)
                return "У всех индикаторов уже есть оценочные средства — предлагать нечего.";
            var parts = new List<string>();
            if (Rows.Count > 0)
                parts.Add($"Добавлено оценочных средств: {Rows.Count} — для индикаторов: {Indicators}. Описания — заготовки, критерии оценок допишите.");
            if (UnmatchedForms.Count > 0)
                parts.Add($"Для форм текущего контроля нет типового средства: {string.Join(", ", UnmatchedForms.Select(f => $"«{f}»"))}.");
            if (WithoutSource.Count > 0)
                parts.Add($"Индикаторы без разделов РПД и аттестации, предложить им нечего: {string.Join(", ", WithoutSource)}.");
            return string.Join(' ', parts);
        }
    }
}

/// <summary>
/// Упрощённый подбор типовых оценочных средств (FR-VAL-08; этап 7, B6). По проекту подбор идёт по справочнику «компетенция —
/// типовое средство»; справочника в системе нет, поэтому подбор — по тому, как индикатор формируется в РПД: форма текущего
/// контроля, вид занятий и вид промежуточной аттестации разделов, где он встречается. Соответствия — файл
/// <c>fos.means-suggestions.json</c> рядом со схемой.
/// <para>
/// Подбор добавляет только то, чего нет: индикаторам, у которых уже есть средство, ничего не предлагается, введённое автором не
/// трогается — решает вызывающий, подставляя <c>covered</c>. Одно и то же средство разных индикаторов складывается в одну строку.
/// Строка — заготовка: критерии пусты, их пишет автор.
/// </para>
/// </summary>
public static class FosMeansRecommender
{
    private static readonly Lazy<JsonNode> Map = new(() => JsonNode.Parse(DocumentTypeRegistry.ReadResource("fos.means-suggestions.json"))!);

    /// <param name="linkedRpd">Раздел <c>linked_rpd</c> содержимого ФОС: снимок РПД.</param>
    /// <param name="covered">Индикаторы, у которых средство уже есть (в том числе в несохранённой форме).</param>
    public static MeansSuggestion Suggest(JsonNode? linkedRpd, IEnumerable<string> covered) => Suggest(linkedRpd, covered, Map.Value);

    internal static MeansSuggestion Suggest(JsonNode? linkedRpd, IEnumerable<string> covered, JsonNode map)
    {
        var have = covered.ToHashSet(StringComparer.Ordinal);
        var sections = (linkedRpd?["sections"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var snapshotSemesters = (linkedRpd?["semesters"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var indicators = (linkedRpd?["competences"] as JsonArray ?? []).OfType<JsonObject>()
            .SelectMany(c => (c["indicators"] as JsonArray ?? []).OfType<JsonObject>())
            .Select(i => Text(i["code"])).OfType<string>().Distinct(StringComparer.Ordinal).Where(c => !have.Contains(c)).ToList();

        // Строки по средству: ключ — тип и наименование; индикаторы в порядке РПД.
        var rows = new Dictionary<(string Type, string Title), JsonObject>();
        var unmatched = new List<string>();
        var withoutSource = new List<string>();
        var suggested = 0;

        foreach (var code in indicators)
        {
            var own = sections.Where(s => Strings(s["indicator_codes"]).Contains(code)).ToList();
            var found = new List<JsonNode>();

            foreach (var form in own.SelectMany(s => Strings(s["current_control_forms"])).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (ByForm(map, form) is { } entry) found.Add(entry);
                else if (!unmatched.Contains(form, StringComparer.OrdinalIgnoreCase)) unmatched.Add(form);
            }

            // Вид занятий — запасной вход: для тех типов, которые формы контроля не дали.
            foreach (var kind in own.SelectMany(s => Strings(s["class_kinds"])).Distinct())
                if (map["class_kinds"]?[kind] is { } byKind && found.All(f => Text(f["means_type"]) != Text(byKind["means_type"])))
                    found.Add(byKind);

            // Семестры раздела; у индикатора без разделов — все семестры РПД.
            var semesters = own.Count > 0
                ? own.SelectMany(s => (s["semesters"] as JsonArray ?? []).Select(n => n?.GetValue<int>() ?? 0)).ToHashSet()
                : snapshotSemesters.Select(s => s["number"]?.GetValue<int>() ?? 0).ToHashSet();
            foreach (var type in snapshotSemesters.Where(s => semesters.Contains(s["number"]?.GetValue<int>() ?? 0))
                         .SelectMany(s => Strings(s["control_types"])).Distinct())
                if (map["attestation"]?[type] is { } byType) found.Add(byType);

            if (found.Count == 0)
            {
                withoutSource.Add(code);
                continue;
            }
            suggested++;
            foreach (var entry in found)
            {
                var key = (Text(entry["means_type"]) ?? "", Text(entry["title"]) ?? "");
                if (!rows.TryGetValue(key, out var row))
                    rows[key] = row = new JsonObject
                    {
                        ["means_type"] = key.Item1, ["title"] = key.Item2, ["description"] = Text(entry["description"]) ?? "",
                        ["indicator_codes"] = new JsonArray()
                    };
                var codes = (JsonArray)row["indicator_codes"]!;
                if (!codes.Any(c => c?.GetValue<string>() == code)) codes.Add(code);
            }
        }

        return new MeansSuggestion(rows.Values.ToList(), unmatched, suggested, withoutSource);
    }

    // Первая запись, у которой название формы содержит ключевое слово; регистр не важен.
    private static JsonNode? ByForm(JsonNode map, string form) =>
        (map["control_forms"] as JsonArray ?? []).FirstOrDefault(e =>
            Strings(e?["keywords"]).Any(k => form.Contains(k, StringComparison.OrdinalIgnoreCase)));

    private static IEnumerable<string> Strings(JsonNode? node) =>
        (node as JsonArray ?? []).Select(n => n?.GetValue<string>()).OfType<string>().Where(s => s.Length > 0);

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}
