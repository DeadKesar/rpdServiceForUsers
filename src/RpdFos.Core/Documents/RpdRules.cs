using System.Text.Json;
using Microsoft.Extensions.Options;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Printing;

namespace RpdFos.Core.Documents;

/// <summary>
/// <c>topic_hours_match_total</c> (FR-VAL-01): суммы часов тематического плана по видам занятий — лекции,
/// практические, лабораторные, самостоятельная работа — равны часам трудоёмкости документа. Трудоёмкость берётся
/// из плана; исправленную методистом (этап 5.1) правило сверяет уже с исправленными часами. У тем с семестром
/// (этап 5.2) — по каждому семестру отдельно, тема в семестре, которого у дисциплины нет, — тоже ошибка; без
/// семестра (шаблоны до этапа 5.2) — общей суммой. Ошибка.
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
        var topics = context.Value.EnumerateArray().ToList();
        var semesters = DocumentSemesters.Of(context.Payload, context.Facts);
        if (!topics.Any(t => Semester(t) is not null))
        {
            foreach (var (key, title, plan) in Kinds)
            {
                var byTopics = topics.Sum(topic => Hours(topic, key));
                var byPlan = semesters.Sum(plan);
                if (byTopics != byPlan)
                    yield return new(context.Path, $"{title}: по темам {byTopics} ч, по распределению часов {byPlan} ч");
            }
            yield break;
        }

        // Тема без семестра — ошибка схемы (поле обязательно), в суммы она не входит.
        for (var i = 0; i < topics.Count; i++)
            if (Semester(topics[i]) is { } s && semesters.All(x => x.Semester != s))
                yield return new($"{context.Path}/{i}/semester", $"Тема в {s} семестре, а у дисциплины такого семестра нет");
        foreach (var semester in semesters)
            foreach (var (key, title, plan) in Kinds)
            {
                var byTopics = topics.Where(t => Semester(t) == semester.Semester).Sum(topic => Hours(topic, key));
                if (byTopics != plan(semester))
                    yield return new(context.Path,
                        $"{semester.Semester} семестр: {title}: по темам {byTopics} ч, по распределению часов {plan(semester)} ч");
            }
    }

    /// <summary>Семестр темы (этап 5.2); у тем шаблонов до него семестра нет.</summary>
    public static int? Semester(JsonElement topic) =>
        topic.ValueKind == JsonValueKind.Object && topic.TryGetProperty("semester", out var s) && s.TryGetInt32(out var n) ? n : null;

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
/// <c>class_hours_match_total</c> (этап 5.2): сумма часов таблицы лабораторных работ (3.3) или практических занятий
/// (3.4) равна часам документа этого вида за все семестры. Вид — по месту правила в схеме. Ошибка.
/// </summary>
public sealed class ClassHoursMatchTotal : IValidationRule
{
    public string Name => "class_hours_match_total";
    public string Level => IssueLevels.Error;

    public IEnumerable<DocumentValidationError> Check(RuleContext context)
    {
        if (context.Value.ValueKind != JsonValueKind.Array) yield break;
        (string Title, Func<DisciplineSemesterDetails, int> Hours)? kind = context.Path switch
        {
            "/lab_works" => ("Лабораторные работы", s => s.LabHours),
            "/practical_classes" => ("Практические занятия", s => s.Practice),
            _ => null
        };
        if (kind is not var (title, hours)) yield break;
        var byTable = context.Value.EnumerateArray().Sum(item =>
            item.ValueKind == JsonValueKind.Object && item.TryGetProperty("hours", out var h) && h.TryGetInt32(out var n) ? n : 0);
        var byPlan = DocumentSemesters.Of(context.Payload, context.Facts).Sum(hours);
        if (byTable != byPlan)
            yield return new(context.Path, $"{title}: по таблице {byTable} ч, по распределению часов {byPlan} ч");
    }
}

/// <summary>
/// <c>hours_match_curriculum</c>: в каждом семестре трудоёмкости документа контактная работа, контроль и
/// самостоятельная работа в сумме равны з. е. × 36. Предупреждение, а не ошибка: эти часы правит только методист,
/// а ошибка вернула бы документ автору, который исправить их не может (этап 5.2).
/// </summary>
public sealed class HoursMatchCurriculum : IValidationRule
{
    public const int HoursPerCredit = 36;

    public string Name => "hours_match_curriculum";
    public string Level => IssueLevels.Warning;

    public IEnumerable<DocumentValidationError> Check(RuleContext context)
    {
        if (context.Value.ValueKind != JsonValueKind.Object || !context.Value.TryGetProperty("semesters", out var list) ||
            list.ValueKind != JsonValueKind.Array) yield break;
        // Место — строка содержимого, поэтому семестры по одному, в порядке массива.
        var rows = list.EnumerateArray().ToList();
        // Дисциплина без з. е. (элективы по физической культуре: 328 ч при 0 з. е.) сверяется не с з. е. × 36,
        // а с часами семестра по плану (этап 5.4, B4).
        var zeroCredits = context.Facts.Discipline.TotalCredits == 0 && context.Facts.Discipline.TotalHours > 0;
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].ValueKind != JsonValueKind.Object) continue;
            var s = DocumentSemesters.Row(rows[i]);
            var sum = s.ContactHours + s.Control + s.SelfStudy;
            if (zeroCredits)
            {
                var plan = context.Facts.Semesters.FirstOrDefault(p => p.Semester == s.Semester);
                var planned = plan is null ? 0 : plan.ContactHours + plan.Control + plan.SelfStudy;
                if (sum != planned)
                    yield return new($"{context.Path}/semesters/{i}",
                        $"{s.Semester} семестр: контактная работа {s.ContactHours} + контроль {s.Control} + самостоятельная работа " +
                        $"{s.SelfStudy} = {sum} ч, а по плану {planned} ч (дисциплина без з. е.)",
                        IssueLevels.Warning);
                continue;
            }
            if (sum != s.Credits * HoursPerCredit)
                yield return new($"{context.Path}/semesters/{i}",
                    $"{s.Semester} семестр: контактная работа {s.ContactHours} + контроль {s.Control} + самостоятельная работа " +
                    $"{s.SelfStudy} = {sum} ч, а {s.Credits} з. е. × {HoursPerCredit} = {s.Credits * HoursPerCredit} ч",
                    IssueLevels.Warning);
        }
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
/// <c>literature_main_count</c> (FR-VAL-02, этап 8, B1): в основной литературе меньше записей, чем требует настройка
/// <see cref="ValidationRuleSettings.LiteratureMainMin"/> (3 — Положение о РП 2022, п. 4.1). Предупреждение: у новой дисциплины
/// литературы бывает мало, и решать, достаточно ли, — методисту, а не автоматике; в проекте это ошибка (отступление, решение 8).
/// Место — список целиком: недостающей записи нет, указывать не на что.
/// </summary>
public sealed class LiteratureMainCount(IOptions<ValidationRuleSettings> settings) : IValidationRule
{
    public string Name => "literature_main_count";
    public string Level => IssueLevels.Warning;

    public IEnumerable<DocumentValidationError> Check(RuleContext context)
    {
        if (context.Value.ValueKind != JsonValueKind.Array) yield break;
        var min = settings.Value.LiteratureMainMin;
        var count = context.Value.GetArrayLength();
        if (count < min)
            yield return new(context.Path, $"В основной литературе {count} {Entries(count)}, а по Положению о рабочих программах нужно не меньше {min}",
                IssueLevels.Warning);
    }

    // «1 запись», «2 записи», «0 записей».
    private static string Entries(int n) => n % 10 == 1 && n % 100 != 11 ? "запись"
        : n % 10 is >= 2 and <= 4 && n % 100 is not (>= 12 and <= 14) ? "записи" : "записей";
}

/// <summary>
/// <c>unique_competence_codes</c> (FR-VAL-02, этап 8, B1): один код компетенции не встречается в списке дважды. Правило
/// «любого документа» — висит на списке компетенций РПД, а у ФОС на разделах, где коды перечислены (снимок РПД, паспорт):
/// список целиком или объект, внутри которого ищутся списки <c>competences</c> (записи с <c>code</c>) и <c>competence_codes</c>
/// (строки). Повтор в разных строках паспорта — не ошибка, поэтому списки сверяются каждый сам с собой. Ошибка стоит у
/// повторной записи.
/// </summary>
public sealed class UniqueCompetenceCodes : IValidationRule
{
    public string Name => "unique_competence_codes";
    public string Level => IssueLevels.Error;

    public IEnumerable<DocumentValidationError> Check(RuleContext context)
    {
        var name = context.Path[(context.Path.LastIndexOf('/') + 1)..];
        return Scan(context.Value, context.Path, name, 0);
    }

    private static IEnumerable<DocumentValidationError> Scan(JsonElement node, string path, string name, int depth)
    {
        if (depth > 6) yield break;
        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in node.EnumerateObject())
                foreach (var e in Scan(p.Value, $"{path}/{p.Name}", p.Name, depth + 1)) yield return e;
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var i = -1;
            foreach (var item in node.EnumerateArray())
            {
                i++;
                var code = name switch
                {
                    "competences" when item.ValueKind == JsonValueKind.Object && item.TryGetProperty("code", out var c) => Code(c),
                    "competence_codes" => Code(item),
                    _ => null
                };
                if (code is not null && !seen.Add(code))
                    yield return new($"{path}/{i}", $"Код компетенции {code} повторяется");
                // Записи вложенных списков — со своим счётом повторов (разделы паспорта).
                if (item.ValueKind == JsonValueKind.Object)
                    foreach (var e in Scan(item, $"{path}/{i}", "", depth + 1)) yield return e;
            }
        }
    }

    private static string? Code(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && value.GetString()?.Trim() is { Length: > 0 } code ? code : null;
}

/// <summary>
/// <c>all_indicators_covered</c> (FR-VAL-02, этап 8, B1): каждый индикатор, закреплённый за дисциплиной, встречается хотя бы
/// в одной теме тематического плана. Закреплённые — выбранные автором из матрицы в разделе «Компетенции»: индикаторы матрицы,
/// которых автор не выбрал, дисциплина не формирует. Предупреждение (отступление от проекта, решение 8): тему можно не привязать
/// сознательно, а блокировать отправку из-за неполной привязки — перебор. Место — тематический план, в тексте — коды.
/// </summary>
public sealed class AllIndicatorsCovered : IValidationRule
{
    private const int Shown = 8;

    public string Name => "all_indicators_covered";
    public string Level => IssueLevels.Warning;

    public IEnumerable<DocumentValidationError> Check(RuleContext context)
    {
        if (context.Value.ValueKind != JsonValueKind.Array) yield break;
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var topic in context.Value.EnumerateArray())
            if (topic.ValueKind == JsonValueKind.Object && topic.TryGetProperty("competence_indicators", out var list) &&
                list.ValueKind == JsonValueKind.Array)
                foreach (var code in list.EnumerateArray())
                    if (code.ValueKind == JsonValueKind.String) covered.Add(code.GetString()!.Trim());

        var missing = Chosen(context.Payload).Where(c => !covered.Contains(c)).ToList();
        if (missing.Count == 0) yield break;
        var codes = missing.Count <= Shown
            ? string.Join(", ", missing)
            : $"{string.Join(", ", missing.Take(Shown))} и ещё {missing.Count - Shown}";
        yield return new(context.Path, $"Индикаторы, которых нет ни в одной теме тематического плана: {codes}", IssueLevels.Warning);
    }

    /// <summary>Коды выбранных индикаторов в порядке раздела «Компетенции», без повторов.</summary>
    private static IEnumerable<string> Chosen(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("competences", out var competences) ||
            competences.ValueKind != JsonValueKind.Array) return [];
        return competences.EnumerateArray()
            .Where(c => c.ValueKind == JsonValueKind.Object && c.TryGetProperty("indicators", out var i) && i.ValueKind == JsonValueKind.Array)
            .SelectMany(c => c.GetProperty("indicators").EnumerateArray())
            .Select(i => i.ValueKind == JsonValueKind.Object && i.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                ? code.GetString()!.Trim()
                : null)
            .OfType<string>().Where(c => c.Length > 0).Distinct(StringComparer.Ordinal);
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

/// <summary>
/// <c>guideline_text_or_url</c> (этап 7.1): запись методических указаний — текст, ссылка или то и другое; запись, в которой
/// нет ни того ни другого, — ошибка у самой записи. Правило висит на записи, а не на поле: «хотя бы одно из двух»
/// JSON Schema выражает через anyOf, а его отказы валидатор не привязывает к месту. Запись, которой в содержимом нет, —
/// не ошибка: указания необязательны. Не проверяется при сохранении: начатую запись можно оставить пустой до отправки.
/// </summary>
public sealed class GuidelineTextOrUrl : IValidationRule
{
    public string Name => "guideline_text_or_url";
    public string Level => IssueLevels.Error;

    public IEnumerable<DocumentValidationError> Check(RuleContext context)
    {
        if (context.Value.ValueKind != JsonValueKind.Object) yield break;
        if (!Filled(context.Value, "text") && !Filled(context.Value, "url"))
            yield return new(context.Path, "Укажите текст или ссылку на методические указания");
    }

    private static bool Filled(JsonElement record, string name) =>
        record.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString());
}

internal static class LiteratureYear
{
    public static int? Of(JsonElement item) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty("year", out var y) && y.TryGetInt32(out var year) ? year : null;

    public static bool IsFundamental(JsonElement item) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty("fundamental", out var f) && f.ValueKind == JsonValueKind.True;
}

/// <summary>
/// <c>section_title_for_text</c> (этап 7.1): у необязательного раздела с заголовком автора — текст без заголовка — ошибка поля
/// «Заголовок»: раздел напечатался бы с пустым названием. Раздел без текста не проверяется (в печать он не попадёт), заголовок
/// без текста — тоже не ошибка. Не проверяется при сохранении: заголовок можно вписать после текста.
/// </summary>
public sealed class SectionTitleForText : IValidationRule
{
    public string Name => "section_title_for_text";
    public string Level => IssueLevels.Error;

    public IEnumerable<DocumentValidationError> Check(RuleContext context)
    {
        if (context.Value.ValueKind != JsonValueKind.Object) yield break;
        var title = context.Value.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        if (HasText(context.Value) && string.IsNullOrWhiteSpace(title))
            yield return new($"{context.Path}/title", "Укажите заголовок раздела: текст без заголовка не напечатать");
    }

    /// <summary>Есть ли у раздела что печатать: абзац, список, таблица или рисунок — «пустой» редактор оставляет только пустые абзацы.</summary>
    public static bool HasText(JsonElement section) =>
        section.ValueKind == JsonValueKind.Object && section.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String &&
        RichTextBlocks.Parse(text.GetString() ?? "", () => 0).Count > 0;
}

/// <summary>
/// <c>section_title_same_in_section</c> (этап 8, A12): у тем одного раздела (одинаковый номер) одно наименование раздела.
/// Наименование вводится у каждой темы, а в таблицах 3.1 и 3.2 и в паспорте ФОС раздел назван один раз: расхождение
/// напечаталось бы выбором «первого попавшегося». Ошибка стоит у поля темы, чьё наименование отличается от наименования
/// первой темы раздела. Пустое наименование — ошибка схемы (поле обязательно), здесь не повторяется.
/// </summary>
public sealed class SectionTitleSameInSection : IValidationRule
{
    public string Name => "section_title_same_in_section";
    public string Level => IssueLevels.Error;

    public IEnumerable<DocumentValidationError> Check(RuleContext context)
    {
        if (context.Value.ValueKind != JsonValueKind.Array) yield break;
        var first = new Dictionary<int, (string Title, int Index)>();
        var index = -1;
        foreach (var topic in context.Value.EnumerateArray())
        {
            index++;
            if (topic.ValueKind != JsonValueKind.Object || !topic.TryGetProperty("section_number", out var number) || !number.TryGetInt32(out var n)) continue;
            var title = topic.TryGetProperty("section_title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()?.Trim() : null;
            if (string.IsNullOrEmpty(title)) continue;
            if (!first.TryGetValue(n, out var known)) first[n] = (title, index);
            else if (!string.Equals(known.Title, title, StringComparison.Ordinal))
                yield return new($"{context.Path}/{index}/section_title", $"У раздела {n} уже указано наименование «{known.Title}»: у всех его тем оно должно быть одинаковым");
        }
    }
}
