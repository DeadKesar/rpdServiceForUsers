using System.Text.Json;
using Microsoft.Extensions.Options;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.Documents;

/// <summary>
/// <c>topic_hours_match_total</c> (FR-VAL-01): суммы часов тематического плана по видам занятий — лекции,
/// практические, лабораторные, самостоятельная работа — равны часам трудоёмкости документа. Трудоёмкость берётся
/// из плана; исправленную методистом (этап 5.1) правило сверяет уже с исправленными часами. Ошибка.
/// </summary>
public sealed class TopicHoursMatchTotal : IValidationRule
{
    private static readonly (string Key, string Title, Func<DisciplineSemesterDetails, int> Plan)[] Kinds =
    [
        ("lectures", "Лекции", s => s.Lectures),
        ("practice", "Практические занятия", s => s.Practice),
        ("lab_hours", "Лабораторные работы", s => s.LabHours),
        ("self_study", "Самостоятельная работа", s => s.SelfStudy)
    ];

    public string Name => "topic_hours_match_total";
    public string Level => IssueLevels.Error;

    public IEnumerable<DocumentValidationError> Check(RuleContext context)
    {
        if (context.Value.ValueKind != JsonValueKind.Array) yield break;
        foreach (var (key, title, plan) in Kinds)
        {
            var byTopics = context.Value.EnumerateArray().Sum(topic => Hours(topic, key));
            var byPlan = DocumentSemesters.Of(context.Payload, context.Facts).Sum(plan);
            if (byTopics != byPlan)
                yield return new(context.Path, $"{title}: по темам {byTopics} ч, по распределению часов {byPlan} ч");
        }
    }

    private static int Hours(JsonElement topic, string key) =>
        topic.ValueKind == JsonValueKind.Object && topic.TryGetProperty("hours", out var hours) &&
        hours.ValueKind == JsonValueKind.Object && hours.TryGetProperty(key, out var value) && value.TryGetInt32(out var n)
            ? n
            : 0;
}

/// <summary>
/// <c>self_study_kinds_match_plan</c>: в каждом семестре сумма часов видов самостоятельной работы равна
/// самостоятельной работе трудоёмкости документа; часы в семестре, которого у дисциплины нет, — тоже ошибка. Правило висит
/// на трудоёмкости, а не на списке видов: пустой список при часах СРС в плане тоже должен остановить отправку. Ошибка.
/// </summary>
public sealed class SelfStudyKindsMatchPlan : IValidationRule
{
    public string Name => "self_study_kinds_match_plan";
    public string Level => IssueLevels.Error;

    public IEnumerable<DocumentValidationError> Check(RuleContext context)
    {
        var path = $"{context.Path}/self_study_kinds";
        var byKinds = Hours(context.Value);
        var semesters = DocumentSemesters.FromHours(context.Value) is { Count: > 0 } own ? own : context.Facts.Semesters;
        foreach (var semester in semesters)
        {
            var sum = byKinds.GetValueOrDefault(semester.Semester);
            if (sum != semester.SelfStudy)
                yield return new(path, $"{semester.Semester} семестр: по видам {sum} ч, по распределению часов {semester.SelfStudy} ч");
        }
        foreach (var (semester, sum) in byKinds.Where(h => h.Value > 0 && semesters.All(s => s.Semester != h.Key)))
            yield return new(path, $"{semester} семестр: по видам {sum} ч, а у дисциплины такого семестра нет");
    }

    /// <summary>Часы видов самостоятельной работы по семестрам.</summary>
    public static Dictionary<int, int> Hours(JsonElement hoursDistribution)
    {
        var result = new Dictionary<int, int>();
        if (hoursDistribution.ValueKind != JsonValueKind.Object ||
            !hoursDistribution.TryGetProperty("self_study_kinds", out var kinds) || kinds.ValueKind != JsonValueKind.Array) return result;
        foreach (var kind in kinds.EnumerateArray())
            if (kind.ValueKind == JsonValueKind.Object && kind.TryGetProperty("hours", out var hours) && hours.ValueKind == JsonValueKind.Array)
                foreach (var h in hours.EnumerateArray())
                    if (h.ValueKind == JsonValueKind.Object && h.TryGetProperty("semester", out var s) && s.TryGetInt32(out var semester) &&
                        h.TryGetProperty("hours", out var v) && v.TryGetInt32(out var n))
                        result[semester] = result.GetValueOrDefault(semester) + n;
        return result;
    }
}

/// <summary>
/// <c>competences_match_matrix</c> (FR-VAL-03): у каждой компетенции дисциплины из матрицы выбран хотя бы один
/// индикатор, в документе нет компетенций не из матрицы и индикаторов чужих компетенций. Ошибка.
/// </summary>
public sealed class CompetencesMatchMatrix : IValidationRule
{
    public string Name => "competences_match_matrix";
    public string Level => IssueLevels.Error;

    public IEnumerable<DocumentValidationError> Check(RuleContext context)
    {
        if (context.Value.ValueKind != JsonValueKind.Array) yield break;
        var chosen = context.Value.EnumerateArray().ToList();

        // Компетенция без индикаторов в списке компетенций — выбрать нечего, это вопрос к исходным данным.
        foreach (var candidate in context.Facts.Competences.Where(c => c.Indicators.Count > 0))
            if (!chosen.Any(c => Text(c, "code") == candidate.Code && Indicators(c).Any()))
                yield return new(context.Path, $"{candidate.Code}: не выбран ни один индикатор");

        for (var i = 0; i < chosen.Count; i++)
        {
            var code = Text(chosen[i], "code");
            var candidate = context.Facts.Competences.FirstOrDefault(c => c.Code == code);
            if (candidate is null)
            {
                yield return new($"{context.Path}/{i}", $"Компетенции {code} нет в матрице компетенций дисциплины");
                continue;
            }
            var own = candidate.Indicators.Select(x => x.Code).ToHashSet();
            var indicators = Indicators(chosen[i]).ToList();
            for (var j = 0; j < indicators.Count; j++)
                if (Text(indicators[j], "code") is { } indicator && !own.Contains(indicator))
                    yield return new($"{context.Path}/{i}/indicators/{j}", $"Индикатор {indicator} не относится к компетенции {code}");
        }
    }

    private static IEnumerable<JsonElement> Indicators(JsonElement competence) =>
        competence.ValueKind == JsonValueKind.Object && competence.TryGetProperty("indicators", out var list) &&
        list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray()
            : [];

    private static string? Text(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}

/// <summary>
/// <c>literature_main_recent</c>: издание основной литературы старше заданного числа лет (настройка
/// <see cref="ValidationRuleSettings.LiteratureMaxAgeYears"/>). Предупреждение: отправку не блокирует —
/// классический учебник бывает и старым. Издание без года и отмеченное автором как фундаментальное не проверяется.
/// </summary>
public sealed class LiteratureMainRecent(IOptions<ValidationRuleSettings> settings) : IValidationRule
{
    public string Name => "literature_main_recent";
    public string Level => IssueLevels.Warning;

    public IEnumerable<DocumentValidationError> Check(RuleContext context)
    {
        if (context.Value.ValueKind != JsonValueKind.Array) yield break;
        var maxAge = settings.Value.LiteratureMaxAgeYears;
        var items = context.Value.EnumerateArray().ToList();
        for (var i = 0; i < items.Count; i++)
            if (LiteratureYear.Of(items[i]) is { } year && !LiteratureYear.IsFundamental(items[i]) && context.Now.Year - year > maxAge)
                yield return new($"{context.Path}/{i}", $"Издание {year} года — старше {maxAge} лет", IssueLevels.Warning);
    }
}

/// <summary>
/// <c>literature_year_not_future</c>: год издания — не позже текущего. JSON Schema «текущий год» не выражает,
/// поэтому это правило, а не <c>maximum</c> схемы; нижняя граница (1800) — в схеме. Ошибка в данных — проверяется
/// и при сохранении.
/// </summary>
public sealed class LiteratureYearNotFuture : IValidationRule
{
    public string Name => "literature_year_not_future";
    public string Level => IssueLevels.Error;
    public bool OnSave => true;

    public IEnumerable<DocumentValidationError> Check(RuleContext context)
    {
        if (context.Value.ValueKind != JsonValueKind.Array) yield break;
        var items = context.Value.EnumerateArray().ToList();
        for (var i = 0; i < items.Count; i++)
            if (LiteratureYear.Of(items[i]) is { } year && year > context.Now.Year)
                yield return new($"{context.Path}/{i}/year", $"Год издания {year} — позже текущего ({context.Now.Year})");
    }
}

internal static class LiteratureYear
{
    public static int? Of(JsonElement item) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty("year", out var y) && y.TryGetInt32(out var year) ? year : null;

    public static bool IsFundamental(JsonElement item) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty("fundamental", out var f) && f.ValueKind == JsonValueKind.True;
}
