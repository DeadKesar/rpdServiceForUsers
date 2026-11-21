using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.Documents;

/// <summary>Что изменилось в наследуемых данных между двумя снимками РПД: коды индикаторов и номера разделов.</summary>
public sealed record SnapshotChanges(
    IReadOnlyList<string> AddedIndicators, IReadOnlyList<string> RemovedIndicators,
    IReadOnlyList<int> AddedSections, IReadOnlyList<int> RemovedSections)
{
    public bool Any => AddedIndicators.Count + RemovedIndicators.Count + AddedSections.Count + RemovedSections.Count > 0;
}

/// <summary>
/// Снимок данных РПД, которые наследует ФОС (FR-DOC-19; этап 7, B3, решение 8): компетенции с выбранными индикаторами, разделы
/// тематического плана (номер, название, семестры, индикаторы, формы текущего контроля) и виды промежуточной аттестации по
/// семестрам. Это копия на момент создания ФОС или «Обновить из РПД»: РПД меняется независимо, и ФОС узнаёт об этом
/// сравнением данных, а не номера версии — он растёт и при смене статуса (<see cref="Differs"/>).
/// <para>
/// Состав и вид полей — раздел <c>linked_rpd</c> схемы ФОС. Значения, которых схема не примет (код вне образца, семестр вне
/// 1–12, вид аттестации не из списка), в снимок не попадают: иначе любое сохранение ФОС отказывало бы из-за чужого значения.
/// Множества (семестры раздела, коды индикаторов и компетенций) идут по порядку номеров, а не по порядку ввода; названия тем и
/// формы контроля — в порядке РПД, как их читает автор.
/// </para>
/// </summary>
public static partial class FosSnapshot
{
    /// <summary>Раздел связи с РПД и раздел индикаторов ФОС, который ведёт снимок (<c>x-source: linked_rpd.competences.indicators</c>).</summary>
    public const string LinkSection = "linked_rpd", IndicatorsSection = "competence_indicators";

    // Данные снимка. Ссылка на РПД, её версия и время снимка в сравнение не входят: версия растёт и при смене статуса РПД.
    private static readonly string[] Data = ["competences", "sections", "semesters"];

    private static readonly string[] CodePrefixes = ["УК", "ОПК", "ПК"];

    // [0-9], а не \d: схема проверяет по правилам ECMAScript, где \d — только ASCII-цифры.
    [GeneratedRegex("^(УК|ОПК|ПК)-[0-9]+$")]
    private static partial Regex CompetenceCode();

    [GeneratedRegex("^(УК|ОПК|ПК)-[0-9]+(\\.[0-9]+){1,2}$")]
    private static partial Regex IndicatorCode();

    /// <summary>
    /// Наследуемые данные содержимого РПД — поля <c>competences</c>, <c>sections</c> и <c>semesters</c> раздела
    /// <c>linked_rpd</c>. Чего в РПД ещё нет (пустой тематический план у черновика), в снимке пустые списки.
    /// </summary>
    public static JsonObject Take(JsonNode? rpdPayload)
    {
        var (competences, owners) = Competences(Get(rpdPayload, "competences"));
        return new JsonObject
        {
            ["competences"] = competences,
            ["sections"] = Sections(Get(rpdPayload, "thematic_plan"), owners),
            ["semesters"] = Semesters(Get(Get(rpdPayload, "hours_distribution"), "semesters"))
        };
    }

    /// <summary>
    /// Записывает связь и снимок в содержимое ФОС: раздел <c>linked_rpd</c> и производный от него список индикаторов. Остальные
    /// разделы — паспорт, оценочные средства, банк заданий — не трогает: их ведёт автор (обновление из РПД ничего в них не удаляет).
    /// </summary>
    public static void Apply(JsonObject fos, Guid rpdId, int rpdVersion, DateTimeOffset at, JsonObject snapshot)
    {
        var link = new JsonObject
        {
            ["rpd_document_id"] = rpdId.ToString(),
            ["rpd_version"] = rpdVersion,
            ["snapshot_at"] = at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
        };
        foreach (var name in Data) link[name] = Get(snapshot, name)?.DeepClone() ?? new JsonArray();
        fos[LinkSection] = link;
        fos[IndicatorsSection] = Indicators(snapshot);
    }

    /// <summary>
    /// Индикаторы всех компетенций снимка одним списком — <c>competence_indicators</c> ФОС: код и формулировка, в порядке РПД.
    /// </summary>
    public static JsonArray Indicators(JsonNode? snapshot)
    {
        var result = new JsonArray();
        if (Get(snapshot, "competences") is JsonArray competences)
            foreach (var competence in competences)
                if (Get(competence, "indicators") is JsonArray indicators)
                    foreach (var indicator in indicators.OfType<JsonObject>())
                        result.Add(indicator.DeepClone());
        return result;
    }

    /// <summary>
    /// «РПД изменилась»: наследуемые данные РПД сейчас отличаются от снимка в ФОС. Сравниваются данные, а не номер версии
    /// (решение 8). Объекты сравниваются без учёта порядка свойств — хранилище jsonb его не сохраняет.
    /// </summary>
    /// <param name="stored">Раздел <c>linked_rpd</c> содержимого ФОС.</param>
    /// <param name="fresh">Снимок по нынешнему содержимому РПД (<see cref="Take"/>).</param>
    public static bool Differs(JsonNode? stored, JsonObject fresh) =>
        Data.Any(name => !JsonNode.DeepEquals(Get(stored, name) ?? new JsonArray(), Get(fresh, name) ?? new JsonArray()));

    /// <summary>Что в снимке <paramref name="fresh"/> добавилось и исчезло против сохранённого: индикаторы и разделы.</summary>
    public static SnapshotChanges Changes(JsonNode? stored, JsonObject fresh)
    {
        var (oldIndicators, newIndicators) = (IndicatorCodes(stored), IndicatorCodes(fresh));
        var (oldSections, newSections) = (SectionNumbers(stored), SectionNumbers(fresh));
        return new(
            newIndicators.Except(oldIndicators).ToList(), oldIndicators.Except(newIndicators).ToList(),
            newSections.Except(oldSections).ToList(), oldSections.Except(newSections).ToList());
    }

    /// <summary>Версия РПД, с которой снят снимок, и время снимка — из раздела <c>linked_rpd</c>.</summary>
    public static (int Version, DateTimeOffset? At) Taken(JsonNode? stored)
    {
        var version = Int(Get(stored, "rpd_version")) ?? 0;
        DateTimeOffset? at = null;
        if (Text(Get(stored, "snapshot_at")) is { } text &&
            DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            at = parsed;
        return (version, at);
    }

    /// <summary>РПД, по которой создан ФОС, — из раздела <c>linked_rpd</c> содержимого; null — связи нет (или содержимое повреждено).</summary>
    public static Guid? RpdIdOf(string payload)
    {
        try
        {
            return Text(Get(Get(JsonNode.Parse(payload), LinkSection), "rpd_document_id")) is { } text && Guid.TryParse(text, out var id)
                ? id
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Компетенции с индикаторами и, для разделов, — какой компетенции принадлежит индикатор по самой РПД.
    private static (JsonArray Competences, Dictionary<string, string> Owners) Competences(JsonNode? node)
    {
        var result = new JsonArray();
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (node is not JsonArray list) return (result, owners);

        foreach (var competence in list.OfType<JsonObject>())
        {
            if (Text(competence["code"])?.Trim() is not { } code || !CompetenceCode().IsMatch(code) || !seen.Add(code)) continue;
            var item = new JsonObject { ["code"] = code };
            if (Text(competence["formulation"]) is { } formulation) item["formulation"] = formulation;

            var indicators = new JsonArray();
            var own = new HashSet<string>(StringComparer.Ordinal);
            if (Get(competence, "indicators") is JsonArray found)
                foreach (var indicator in found.OfType<JsonObject>())
                {
                    if (Text(indicator["code"])?.Trim() is not { } indicatorCode || !IndicatorCode().IsMatch(indicatorCode) ||
                        !own.Add(indicatorCode)) continue;
                    var entry = new JsonObject { ["code"] = indicatorCode };
                    if (Text(indicator["text"]) is { } text) entry["text"] = text;
                    indicators.Add(entry);
                    owners.TryAdd(indicatorCode, code);
                }
            item["indicators"] = indicators;
            result.Add(item);
        }
        return (result, owners);
    }

    // Разделы по номеру из тем тематического плана; название раздела — темы через запятую, как в паспорте ФОС.
    private static JsonArray Sections(JsonNode? plan, IReadOnlyDictionary<string, string> owners)
    {
        var sections = new SortedDictionary<int, SectionDraft>();
        if (plan is JsonArray topics)
            foreach (var topic in topics.OfType<JsonObject>())
            {
                if (Int(topic["section_number"]) is not int number || number < 1) continue;
                if (!sections.TryGetValue(number, out var section)) sections[number] = section = new SectionDraft();

                if (Text(topic["topic_title"])?.Trim() is { } title && !section.Titles.Contains(title)) section.Titles.Add(title);
                if (Int(topic["semester"]) is int semester && semester is >= 1 and <= 12) section.Semesters.Add(semester);
                foreach (var code in Strings(topic["competence_indicators"]).Where(c => IndicatorCode().IsMatch(c)))
                    section.Indicators.Add(code);
                foreach (var form in Strings(topic["current_control_forms"]))
                    if (!section.Forms.Contains(form)) section.Forms.Add(form);
            }

        var result = new JsonArray();
        foreach (var (number, section) in sections)
        {
            var indicators = section.Indicators.OrderBy(CodeKey, StringComparer.Ordinal).ToList();
            var item = new JsonObject { ["number"] = number };
            if (section.Titles.Count > 0) item["title"] = string.Join(", ", section.Titles);
            item["semesters"] = new JsonArray(section.Semesters.Select(s => (JsonNode)s).ToArray());
            // Компетенция раздела — по самой РПД; индикатор, которого в её компетенциях нет, называет её своим кодом.
            item["competence_codes"] = new JsonArray(indicators.Select(i => owners.GetValueOrDefault(i) ?? i[..i.IndexOf('.')])
                .Where(c => CompetenceCode().IsMatch(c)).Distinct().OrderBy(CodeKey, StringComparer.Ordinal)
                .Select(c => (JsonNode)c).ToArray());
            item["indicator_codes"] = new JsonArray(indicators.Select(i => (JsonNode)i).ToArray());
            item["current_control_forms"] = new JsonArray(section.Forms.Select(f => (JsonNode)f).ToArray());
            result.Add(item);
        }
        return result;
    }

    // Семестры РПД с видами аттестации: первая запись семестра выигрывает, как в нормализованных часах.
    private static JsonArray Semesters(JsonNode? node)
    {
        var found = new SortedDictionary<int, List<string>>();
        if (node is JsonArray rows)
            foreach (var row in rows.OfType<JsonObject>())
                if (Int(row["semester"]) is int number && number is >= 1 and <= 12 && !found.ContainsKey(number))
                    found[number] = Strings(row["control_types"]).Where(t => ControlTypeCodes.All.Contains(t)).Distinct()
                        .OrderBy(t => Array.IndexOf(ControlTypeCodes.All, t)).ToList();

        return new JsonArray(found.Select(s => (JsonNode)new JsonObject
        {
            ["number"] = s.Key,
            ["control_types"] = new JsonArray(s.Value.Select(t => (JsonNode)t).ToArray())
        }).ToArray());
    }

    private static List<string> IndicatorCodes(JsonNode? snapshot) =>
        Indicators(snapshot).Select(i => Text(Get(i, "code"))).OfType<string>().Distinct().OrderBy(CodeKey, StringComparer.Ordinal).ToList();

    private static List<int> SectionNumbers(JsonNode? snapshot) =>
        (Get(snapshot, "sections") as JsonArray ?? new JsonArray()).Select(s => Int(Get(s, "number"))).OfType<int>().Distinct()
        .Order().ToList();

    // «УК-2.10» после «УК-2.9»: категория по порядку «УК, ОПК, ПК», номера сравниваются как числа.
    private static string CodeKey(string code)
    {
        var dash = code.IndexOf('-');
        var rank = Array.IndexOf(CodePrefixes, dash < 0 ? code : code[..dash]);
        var numbers = dash < 0 ? [] : code[(dash + 1)..].Split('.');
        return $"{(rank < 0 ? CodePrefixes.Length : rank)}-" +
               string.Join('.', numbers.Select(n => int.TryParse(n, out var i) ? i.ToString("D4") : n));
    }

    // Свойство объекта; у значения иного вида (повреждённое содержимое) — пусто, а не исключение индексатора.
    private static JsonNode? Get(JsonNode? node, string name) => node is JsonObject o ? o[name] : null;

    private static string? Text(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static int? Int(JsonNode? node) => node is JsonValue v && v.TryGetValue(out int number) ? number : null;

    private static IEnumerable<string> Strings(JsonNode? node) =>
        node is JsonArray list ? list.Select(Text).OfType<string>().Select(s => s.Trim()) : [];

    private sealed class SectionDraft
    {
        public List<string> Titles { get; } = [];
        public SortedSet<int> Semesters { get; } = [];
        public HashSet<string> Indicators { get; } = new(StringComparer.Ordinal);
        public List<string> Forms { get; } = [];
    }
}
