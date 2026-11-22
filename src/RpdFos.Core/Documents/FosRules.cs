using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.Documents;

/// <summary>
/// Содержимое ФОС и связанной РПД в том виде, как его читают правила сквозной проверки (этап 7, B8). Правила получают
/// <see cref="RuleContext"/> с содержимым как JSON-элементом; здесь оно разбирается один раз на правило в простые записи.
/// Данные РПД — <see cref="LinkedRpdInfo.Current"/>: нынешнее содержимое РПД в форме снимка (<see cref="FosSnapshot.Take"/>).
/// </summary>
internal static partial class FosContent
{
    /// <summary>Группа банка: название, вид, раздел РПД, семестры, форма контроля и номера её заданий.</summary>
    public sealed record Group(string Name, string Kind, int? Section, IReadOnlyList<int> Semesters, string Control, IReadOnlyList<int> Numbers);

    /// <summary>Ссылка паспорта на задания: место в содержимом, кому принадлежит и что названо.</summary>
    public sealed record TaskReference(string Path, string Owner, string Group, int? From, int? To);

    public static JsonNode? Payload(RuleContext context) =>
        context.Payload.ValueKind == System.Text.Json.JsonValueKind.Object ? JsonNode.Parse(context.Payload.GetRawText()) : null;

    public static List<Group> Bank(JsonNode? payload) =>
        (payload?["task_bank"] as JsonArray ?? []).OfType<JsonObject>().Select(g => new Group(
            Text(g["group"]) ?? "", Text(g["kind"]) ?? "", Number(g["section_number"]),
            Numbers(g["semesters"]), Text(g["control_form"]) ?? "",
            (g["tasks"] as JsonArray ?? []).OfType<JsonObject>().Select(t => Number(t["number"])).OfType<int>().ToList())).ToList();

    /// <summary>Ссылки обеих таблиц паспорта — «раздел → задания» и «индикатор → задания».</summary>
    public static List<TaskReference> References(JsonNode? payload)
    {
        var result = new List<TaskReference>();
        var passport = payload?["passport"];
        foreach (var (list, owner) in new[] { ("sections", "section_number"), ("indicators", "indicator_code") })
        {
            var rows = (passport?[list] as JsonArray ?? []).ToList();
            for (var i = 0; i < rows.Count; i++)
            {
                var who = list == "sections" ? $"Раздел {Text(rows[i]?[owner]) ?? Number(rows[i]?[owner])?.ToString() ?? "?"}"
                    : $"Индикатор {Text(rows[i]?[owner]) ?? "?"}";
                var tasks = (rows[i]?["tasks"] as JsonArray ?? []).ToList();
                for (var j = 0; j < tasks.Count; j++)
                    result.Add(new TaskReference($"/passport/{list}/{i}/tasks/{j}", who, Text(tasks[j]?["group"]) ?? "",
                        Number(tasks[j]?["from"]), Number(tasks[j]?["to"])));
            }
        }
        return result;
    }

    /// <summary>Виды промежуточной аттестации РПД по семестрам: (семестр, вид).</summary>
    public static List<(int Semester, string Type)> Attestations(LinkedRpdInfo rpd) =>
        (rpd.Current["semesters"] as JsonArray ?? []).OfType<JsonObject>()
        .SelectMany(s => Strings(s["control_types"]).Select(t => (Number(s["number"]) ?? 0, t)))
        .Where(a => a.Item1 > 0).ToList();

    public static List<string> IndicatorCodes(LinkedRpdInfo rpd) =>
        FosSnapshot.Indicators(rpd.Current).Select(i => Text(i?["code"])).OfType<string>().Distinct(StringComparer.Ordinal).ToList();

    public static string TypeTitle(string type) => type switch
    {
        ControlTypeCodes.Exam => "экзамен",
        ControlTypeCodes.Credit => "зачёт",
        ControlTypeCodes.GradedCredit => "дифференцированный зачёт",
        ControlTypeCodes.CourseProject => "курсовой проект",
        ControlTypeCodes.CourseWork => "курсовая работа",
        _ => type
    };

    /// <summary>Текст форматированного поля без разметки; <paramref name="hasMedia"/> — в нём есть таблица или рисунок.</summary>
    public static string PlainText(string? html, out bool hasMedia)
    {
        hasMedia = html is not null && Media().IsMatch(html);
        return html is null ? "" : WebUtility.HtmlDecode(Tags().Replace(html, " ")).Replace(' ', ' ').Trim();
    }

    public static string? Text(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    // Число из содержимого: пришедшее с клиента и созданное на сервере хранятся по-разному — читаем через текст.
    public static int? Number(JsonNode? node) =>
        node is JsonValue && int.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    public static List<int> Numbers(JsonNode? node) => (node as JsonArray ?? []).Select(Number).OfType<int>().ToList();

    public static List<string> Strings(JsonNode? node) => (node as JsonArray ?? []).Select(Text).OfType<string>().Select(s => s.Trim()).ToList();

    /// <summary>«3, 5, 8»: номера через запятую, длинный список — с «и ещё N».</summary>
    public static string Enumerate(IReadOnlyList<int> numbers, int limit = 8) => numbers.Count <= limit
        ? string.Join(", ", numbers)
        : $"{string.Join(", ", numbers.Take(limit))} и ещё {numbers.Count - limit}";

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex("<(img|table)\\b", RegexOptions.IgnoreCase)]
    private static partial Regex Media();
}

/// <summary>
/// <c>linked_rpd_must_be_approved_or_under_review</c> (FR-VAL-07; этап 7, B8, решение 8): ФОС отправляется, когда связанная
/// РПД уже на проверке или дальше — «На проверке», «Принят», «На согласовании», «Согласован». Черновик, возврат на доработку,
/// архив и удаление не подходят. У ФОС, который принят и идёт дальше, правило ничего не требует
/// (<see cref="LinkedRpdInfo.EnforceStatus"/>). Ошибка: единственное правило ФОС, которое блокирует отправку.
/// </summary>
public sealed class LinkedRpdMustBeApprovedOrUnderReview : IValidationRule
{
    private static readonly HashSet<string> Allowed =
        [DocumentStatuses.UnderReview, DocumentStatuses.Accepted, DocumentStatuses.Approval, DocumentStatuses.Approved];

    public string Name => "linked_rpd_must_be_approved_or_under_review";
    public string Level => IssueLevels.Error;

    public IEnumerable<DocumentValidationError> Check(RuleContext context)
    {
        var rpd = context.Facts.LinkedRpd;
        if (rpd is null) yield return new(context.Path, "Связанная РПД не найдена: ФОС отправляется, когда его РПД на проверке или дальше");
        else if (!rpd.EnforceStatus) yield break;
        else if (rpd.Deleted) yield return new(context.Path, "Связанная РПД удалена: ФОС можно отправить, когда его РПД на проверке или дальше");
        else if (!Allowed.Contains(rpd.Status))
            yield return new(context.Path,
                $"РПД в статусе «{DocumentStatuses.Title(rpd.Status)}»: ФОС отправляется, когда РПД на проверке, принята, на согласовании или согласована");
    }
}

/// <summary>
/// <c>all_indicators_have_means</c> (FR-VAL-04, FR-VAL-07а): у каждого индикатора РПД — по её нынешнему содержимому, а не
/// по снимку — есть хотя бы одно оценочное средство ФОС. Предупреждение, по замечанию на индикатор.
/// </summary>
public sealed class AllIndicatorsHaveMeans : IValidationRule
{
    public string Name => "all_indicators_have_means";
    public string Level => IssueLevels.Warning;
    public bool WhenAbsent => true;

    public IEnumerable<DocumentValidationError> Check(RuleContext context)
    {
        if (context.Facts.LinkedRpd is not { } rpd) yield break;
        var covered = ((FosContent.Payload(context)?["evaluation_means"] as JsonArray) ?? [])
            .SelectMany(m => FosContent.Strings(m?["indicator_codes"])).ToHashSet(StringComparer.Ordinal);
        foreach (var code in FosContent.IndicatorCodes(rpd).Where(c => !covered.Contains(c)))
            yield return new("/evaluation_means", $"У индикатора {code} нет оценочного средства");
    }
}

/// <summary>
/// <c>fos_passport_consistent</c> (FR-VAL-09): каждая ссылка паспорта указывает на задания, которые есть в банке; каждый индикатор
/// РПД привязан хотя бы к одному заданию; разделы паспорта есть в РПД; в группах банка нет пропусков номеров (после удаления
/// задания «Перенумеровать» убирает их вместе с поправкой ссылок). Предупреждение.
/// </summary>
public sealed class FosPassportConsistent : IValidationRule
{
    public string Name => "fos_passport_consistent";
    public string Level => IssueLevels.Warning;
    public bool WhenAbsent => true;

    // Диапазон длиннее не перебирается поштучно: ссылка «1–100000» — ошибка ввода, а не повод считать сто тысяч номеров.
    private const int MaxRange = 2000;

    public IEnumerable<DocumentValidationError> Check(RuleContext context)
    {
        var payload = FosContent.Payload(context);
        var bank = FosContent.Bank(payload);
        var numbers = bank.GroupBy(g => g.Name).ToDictionary(g => g.Key, g => g.SelectMany(x => x.Numbers).ToHashSet());

        foreach (var reference in FosContent.References(payload))
        {
            if (!numbers.TryGetValue(reference.Group, out var have))
            {
                yield return new(reference.Path, $"{reference.Owner}: в банке заданий нет группы «{reference.Group}»");
                continue;
            }
            var (from, to) = (reference.From, reference.To);
            if (from is null && to is null)
            {
                if (have.Count == 0) yield return new(reference.Path, $"{reference.Owner}: в группе «{reference.Group}» нет заданий");
            }
            else if (from is { } f && to is { } t)
            {
                var missing = t - f > MaxRange ? [] : Enumerable.Range(f, Math.Max(0, t - f + 1)).Where(n => !have.Contains(n)).ToList();
                if (missing.Count > 0)
                    yield return new(reference.Path, $"{reference.Owner}: в группе «{reference.Group}» нет заданий № {FosContent.Enumerate(missing)}");
            }
            else if (!have.Any(n => n >= (from ?? int.MinValue) && n <= (to ?? int.MaxValue)))
                yield return new(reference.Path, $"{reference.Owner}: в группе «{reference.Group}» нет заданий " +
                                                 (from is { } only ? $"начиная с № {only}" : $"до № {to}"));
        }

        if (context.Facts.LinkedRpd is { } rpd)
        {
            var sectionNumbers = (rpd.Current["sections"] as JsonArray ?? []).Select(s => FosContent.Number(s?["number"])).OfType<int>().ToHashSet();
            var rows = (payload?["passport"]?["sections"] as JsonArray ?? []).ToList();
            for (var i = 0; i < rows.Count; i++)
                if (FosContent.Number(rows[i]?["section_number"]) is { } number && !sectionNumbers.Contains(number))
                    yield return new($"/passport/sections/{i}", $"Раздела {number} нет в РПД");

            var bound = ((payload?["passport"]?["indicators"] as JsonArray) ?? [])
                .Where(r => (r?["tasks"] as JsonArray)?.Count > 0).Select(r => FosContent.Text(r?["indicator_code"])).OfType<string>()
                .ToHashSet(StringComparer.Ordinal);
            foreach (var code in FosContent.IndicatorCodes(rpd).Where(c => !bound.Contains(c)))
                yield return new("/passport/indicators", $"Индикатор {code} не привязан ни к одному заданию");
        }

        foreach (var group in bank.Where(g => g.Numbers.Count > 0))
        {
            var gaps = Enumerable.Range(1, group.Numbers.Max()).Except(group.Numbers).ToList();
            if (gaps.Count > 0)
                yield return new("/task_bank", $"В группе «{group.Name}» пропущены номера: {FosContent.Enumerate(gaps)} — «Перенумеровать» уберёт пропуски");
        }
    }
}

/// <summary>
/// <c>scale_matches_control_type</c> (FR-VAL-07б, в): виды промежуточной аттестации РПД представлены средствами промежуточного
/// контроля ФОС, а шкала оценивания им соответствует. Представлен — значит, в банке есть группа промежуточной аттестации этого
/// семестра (без семестров — любого) или оценочное средство нужного типа: вопросы к экзамену, вопросы к зачёту, проект.
/// Зачёт оценивается «зачтено / не зачтено»; экзамен, дифференцированный зачёт и курсовая работа — балльной шкалой. Пустая
/// шкала предупреждения не даёт: в печати она берётся по виду аттестации РПД. Предупреждение.
/// </summary>
public sealed class ScaleMatchesControlType : IValidationRule
{
    public string Name => "scale_matches_control_type";
    public string Level => IssueLevels.Warning;
    public bool WhenAbsent => true;

    private static readonly Dictionary<string, string> ScaleTitles = new()
    {
        ["five_point"] = "Балльная (5)", ["pass_fail"] = "Зачёт / незачёт", ["hundred_point"] = "100-балльная"
    };

    public IEnumerable<DocumentValidationError> Check(RuleContext context)
    {
        if (context.Facts.LinkedRpd is not { } rpd) yield break;
        var payload = FosContent.Payload(context);
        var attestations = FosContent.Attestations(rpd);
        var bank = FosContent.Bank(payload);
        var means = ((payload?["evaluation_means"] as JsonArray) ?? []).Select(m => FosContent.Text(m?["means_type"])).OfType<string>().ToHashSet();

        foreach (var (semester, type) in attestations.Distinct())
        {
            var byBank = bank.Any(g => g.Control == "intermediate" && (g.Semesters.Count == 0 || g.Semesters.Contains(semester)));
            var byMeans = means.Contains(type switch
            {
                ControlTypeCodes.Credit => "credit_questions",
                ControlTypeCodes.CourseProject or ControlTypeCodes.CourseWork => "project",
                _ => "exam_questions"
            });
            if (!byBank && !byMeans)
                yield return new("/task_bank",
                    $"{semester} семестр РПД: {FosContent.TypeTitle(type)} — в ФОС нет средств промежуточной аттестации (группы банка заданий или вопросов)");
        }

        var scale = FosContent.Text(payload?["evaluation_scale"]?["type"]);
        if (scale is null || attestations.Count == 0) yield break;
        var types = attestations.Select(a => a.Type).ToHashSet();
        var allowed = new List<string>();
        if (types.Contains(ControlTypeCodes.Credit)) allowed.Add("pass_fail");
        if (types.Overlaps([ControlTypeCodes.Exam, ControlTypeCodes.GradedCredit, ControlTypeCodes.CourseProject, ControlTypeCodes.CourseWork]))
            allowed.AddRange(["five_point", "hundred_point"]);
        if (allowed.Contains(scale)) yield break;
        yield return new("/evaluation_scale/type",
            $"Шкала «{ScaleTitles.GetValueOrDefault(scale, scale)}» не подходит к виду аттестации РПД ({string.Join(", ", types.Select(FosContent.TypeTitle))}): " +
            $"нужна «{string.Join("» или «", allowed.Select(a => ScaleTitles[a]))}»");
    }
}

/// <summary>
/// <c>control_forms_match</c> (FR-VAL-10): формы текущего контроля разделов тематического плана РПД представлены контрольными
/// мероприятиями ФОС. Сопоставление нестрогое: свободный текст без справочника форм, слова сравниваются по началу («контрольная
/// работа» ~ «Контрольные работы по модулям»), и форма представлена, если все её значимые слова нашлись в одном мероприятии или
/// в одной из форм текущей аттестации. Предупреждение, по замечанию на форму.
/// </summary>
public sealed class ControlFormsMatch : IValidationRule
{
    // Слова короче не значимы («по», «и», «в»); совпадение слов — общее начало не короче четырёх букв или всего короткого слова.
    private const int MinWord = 3, MinPrefix = 4;

    public string Name => "control_forms_match";
    public string Level => IssueLevels.Warning;
    public bool WhenAbsent => true;

    public IEnumerable<DocumentValidationError> Check(RuleContext context)
    {
        if (context.Facts.LinkedRpd is not { } rpd) yield break;
        var control = FosContent.Payload(context)?["current_control"];
        var targets = FosContent.Strings(control?["forms"]).Select(Words).ToList();
        foreach (var activity in (control?["activities"] as JsonArray ?? []).OfType<JsonObject>())
            targets.Add(Words($"{FosContent.Text(activity["module"])} {FosContent.Text(activity["title"])}"));

        var forms = new Dictionary<string, (string Form, List<int> Sections)>();
        foreach (var section in (rpd.Current["sections"] as JsonArray ?? []).OfType<JsonObject>())
            foreach (var form in FosContent.Strings(section["current_control_forms"]))
            {
                var key = string.Join(' ', Words(form));
                if (key.Length == 0) continue;
                if (!forms.TryGetValue(key, out var found)) forms[key] = found = (form, []);
                if (FosContent.Number(section["number"]) is { } number) found.Sections.Add(number);
            }

        foreach (var (form, sections) in forms.Values)
        {
            var words = Words(form);
            if (targets.Any(target => words.All(w => target.Any(t => Same(w, t))))) continue;
            var where = sections.Count == 0 ? "" : sections.Count == 1 ? $" (раздел {sections[0]} РПД)" : $" (разделы РПД: {FosContent.Enumerate(sections.Distinct().Order().ToList())})";
            yield return new("/current_control", $"Форма текущего контроля «{form}»{where} не представлена мероприятиями ФОС");
        }
    }

    // Слова строчными, «ё» как «е», без знаков; короткие служебные слова отброшены.
    internal static List<string> Words(string? text) =>
        Regex.Split((text ?? "").ToLowerInvariant().Replace('ё', 'е'), "[^\\p{L}\\p{Nd}]+")
            .Where(w => w.Length > MinWord - 1).ToList();

    internal static bool Same(string a, string b)
    {
        var need = Math.Min(MinPrefix, Math.Min(a.Length, b.Length));
        var common = 0;
        while (common < a.Length && common < b.Length && a[common] == b[common]) common++;
        return common >= need;
    }
}

/// <summary>
/// <c>fos_regulation_thresholds</c> (этап 7, B8; ответ автора на П12 в B1): пороги Положения о ФОС (П 01-04, 2022). Не менее 20
/// вопросов к каждому виду промежуточной аттестации РПД в каждом семестре; не менее 10 тестовых заданий на раздел, если тесты
/// есть; при экзамене — не менее 25 билетов или группа тестов промежуточной аттестации; у каждого оценочного средства заполнены
/// критерии оценок. Дополнение к проекту (в каталоге правил его нет): предупреждение, как и остальные правила ФОС.
/// </summary>
public sealed class FosRegulationThresholds : IValidationRule
{
    public const int MinQuestions = 20, MinTests = 10, MinTickets = 25;

    public string Name => "fos_regulation_thresholds";
    public string Level => IssueLevels.Warning;
    public bool WhenAbsent => true;

    public IEnumerable<DocumentValidationError> Check(RuleContext context)
    {
        var payload = FosContent.Payload(context);
        var bank = FosContent.Bank(payload);

        if (context.Facts.LinkedRpd is { } rpd)
        {
            var attestations = FosContent.Attestations(rpd);
            var questioned = new[] { ControlTypeCodes.Exam, ControlTypeCodes.Credit, ControlTypeCodes.GradedCredit };
            foreach (var (semester, type) in attestations.Where(a => questioned.Contains(a.Type)).Distinct())
            {
                var count = bank.Where(g => g.Kind == "questions" && g.Control == "intermediate" &&
                                            (g.Semesters.Count == 0 || g.Semesters.Contains(semester))).Sum(g => g.Numbers.Count);
                if (count < MinQuestions)
                    yield return new("/task_bank",
                        $"{semester} семестр, {FosContent.TypeTitle(type)}: вопросов в банке {count}, по Положению не менее {MinQuestions}");
            }

            var tickets = (payload?["exam_tickets"] as JsonArray)?.Count ?? 0;
            var testsInExam = bank.Any(g => g.Kind == "tests" && g.Control == "intermediate");
            if (attestations.Any(a => a.Type == ControlTypeCodes.Exam) && tickets < MinTickets && !testsInExam)
                yield return new("/exam_tickets",
                    $"К экзамену в ФОС билетов {tickets}, по Положению не менее {MinTickets} (или группа тестов промежуточной аттестации)");
        }

        foreach (var section in bank.Where(g => g.Kind == "tests").GroupBy(g => g.Section is { } s ? $"Раздел {s}" : $"Группа «{g.Name}»"))
        {
            var count = section.Sum(g => g.Numbers.Count);
            if (count < MinTests)
                yield return new("/task_bank", $"{section.Key}: тестовых заданий {count}, по Положению не менее {MinTests}");
        }

        var means = (payload?["evaluation_means"] as JsonArray ?? []).ToList();
        for (var i = 0; i < means.Count; i++)
            if (FosContent.PlainText(FosContent.Text(means[i]?["criteria"]), out var media).Length == 0 && !media)
                yield return new($"/evaluation_means/{i}/criteria",
                    $"У оценочного средства «{FosContent.Text(means[i]?["title"]) ?? "без названия"}» не заполнены критерии оценок");
    }
}
