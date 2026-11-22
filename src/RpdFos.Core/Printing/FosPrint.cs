using System.Text.Json;
using RpdFos.Core.Documents;

namespace RpdFos.Core.Printing;

/// <summary>
/// Печатная форма ФОС (этап 7, B9): разделы содержимого — в значения закладок шаблона оформления v2. Здесь же части банка
/// заданий (заголовок части — по виду аттестации РПД из снимка), строки двух таблиц паспорта с числом заданий по банку
/// и билеты. Только чтение содержимого: ничего не вычисляется заново в документе и не меняется.
/// </summary>
internal static class FosPrint
{
    /// <summary>Группа банка: название, подзаголовок в печати, вид, форма контроля, раздел РПД, семестры, задания.</summary>
    public sealed record BankGroup(
        string Name, string Subtitle, string Kind, string Control, int? Section, IReadOnlyList<int> Semesters,
        IReadOnlyList<JsonElement> Tasks);

    /// <summary>Часть банка — приложение шаблона: заголовок и группы, печатаемые подзаголовками.</summary>
    public sealed record BankPart(string Title, IReadOnlyList<BankGroup> Groups);

    public sealed record SectionRow(int Number, string Title, string Competences, int Questions, int Tasks, int Tests);

    public sealed record PassportRow(string Competence, string Indicator, IReadOnlyList<string> Sections, IReadOnlyList<string> Tasks);

    // Заглушки необязательных разделов (FR-VAL: раздел может быть пуст, печатается формулировка, а не пустое место).
    private const string NoCurrentControl = "Мероприятия текущего контроля в фонде оценочных средств не предусмотрены.";
    private const string NoMeans = "Оценочные средства, кроме заданий банка, в фонде не предусмотрены.";

    private static readonly Dictionary<string, string> Dative = new()
    {
        ["exam"] = "экзамену", ["credit"] = "зачёту", ["graded_credit"] = "дифференцированному зачёту"
    };

    private static readonly Dictionary<string, string> Genitive = new()
    {
        ["exam"] = "экзамена", ["credit"] = "зачёта", ["graded_credit"] = "дифференцированного зачёта"
    };

    /// <summary>Места содержимого, которые печатает этот класс, а не общий разбор по схеме.</summary>
    public static bool Handles(string pointer) =>
        pointer is "/current_control" or "/evaluation_means" or "/evaluation_scale" or "/task_bank" or "/approval_sheet/developers"
            or "/exam_tickets/0/questions";

    public static PrintValue Render(PrintContext c, string pointer) => pointer switch
    {
        "/current_control" => CurrentControl(c),
        "/evaluation_means" => Means(c),
        "/evaluation_scale" => Scale(c),
        "/task_bank" => c.PartIndex is { } index && Parts(c) is var parts && index < parts.Count ? PartContent(c, parts[index]) : PrintValue.Empty,
        "/approval_sheet/developers" => Developers(c),
        _ => TicketQuestions(c, pointer)
    };

    // ---------- Банк заданий ----------

    public static IReadOnlyList<BankGroup> Groups(PrintContext c) =>
        Items(c.At("/task_bank")).Select(g =>
        {
            var name = Text(g, "group") ?? "";
            return new BankGroup(name, Text(g, "title") is { Length: > 0 } title ? title : name, Text(g, "kind") ?? "",
                Text(g, "control_form") ?? "", Int(g, "section_number"),
                Items(Prop(g, "semesters")).Select(s => s.TryGetInt32(out var n) ? n : 0).Where(n => n > 0).ToList(), Items(Prop(g, "tasks")));
        }).ToList();

    /// <summary>
    /// Части банка по порядку появления. Часть — вид заданий и вид аттестации (или текущий контроль); вид аттестации — из РПД
    /// по семестрам группы (снимок в <c>linked_rpd</c>), у группы нескольких семестров — по последнему: итог дисциплины
    /// подводится в нём. Группа без вида аттестации (нет семестров, нет их в РПД) — своя часть с названием группы.
    /// </summary>
    public static IReadOnlyList<BankPart> Parts(PrintContext c)
    {
        var parts = new List<(string Key, string Title, List<BankGroup> Groups)>();
        foreach (var group in Groups(c).Where(g => g.Tasks.Count > 0))
        {
            var attestation = group.Control == "current" ? null : Attestation(c, group.Semesters);
            var (key, title) = group.Control == "current" ? ($"{group.Kind}|current", Title(group, "текущего контроля"))
                : attestation is not null ? ($"{group.Kind}|{attestation}", Title(group, null, attestation))
                : ($"{group.Kind}|group|{group.Name}", group.Subtitle);
            var found = parts.FindIndex(p => p.Key == key);
            if (found < 0) parts.Add((key, title, [group]));
            else parts[found].Groups.Add(group);
        }
        return parts.Select(p => new BankPart(p.Title, p.Groups)).ToList();
    }

    // «Вопросы для подготовки к экзамену»; «Комплект практических (тестовых) заданий, используемых при проведении экзамена».
    private static string Title(BankGroup group, string? current, string? attestation = null) =>
        group.Kind == "questions"
            ? current is not null ? "Вопросы для текущего контроля" : $"Вопросы для подготовки к {Dative[attestation!]}"
            : $"Комплект {(group.Kind == "tests" ? "тестовых" : "практических")} заданий, используемых при проведении " +
              (current ?? Genitive[attestation!]);

    private static string? Attestation(PrintContext c, IReadOnlyList<int> semesters)
    {
        var rpd = Items(c.At("/linked_rpd/semesters"));
        foreach (var semester in semesters.OrderByDescending(s => s))
        {
            var types = rpd.Where(s => Int(s, "number") == semester).SelectMany(s => Strings(Prop(s, "control_types"))).ToList();
            foreach (var type in new[] { "exam", "graded_credit", "credit" })
                if (types.Contains(type)) return type;
        }
        return null;
    }

    public static string? PartTitle(PrintContext c, int index) => Parts(c) is var parts && index < parts.Count ? parts[index].Title : null;

    // Подзаголовок группы и её задания с номерами; у теста номер — «раздел.номер», как в ссылках паспорта.
    private static PrintValue PartContent(PrintContext c, BankPart part)
    {
        var blocks = new List<PrintBlock>();
        foreach (var group in part.Groups)
        {
            blocks.Add(PrintParagraph.Plain(group.Subtitle, bold: true));
            foreach (var task in group.Tasks)
                blocks.AddRange(Lead(new PrintRun(Label(group, Int(task, "number")) + ". "), Rich(c, Text(task, "text"))));
        }
        return new PrintValue(blocks);
    }

    private static string Label(BankGroup group, int? number) =>
        group.Kind == "tests" && group.Section is { } section ? $"{section}.{number}" : number?.ToString() ?? "";

    // ---------- Билеты ----------

    // Вопросы билета нумерованными абзацами «1.», «2.»: вопрос — форматированный текст, он может содержать таблицу и рисунок.
    private static PrintValue TicketQuestions(PrintContext c, string pointer)
    {
        var questions = Items(c.At(pointer));
        var blocks = new List<PrintBlock>();
        for (var i = 0; i < questions.Count; i++)
            blocks.AddRange(Lead(new PrintRun($"{i + 1}. "), Rich(c, questions[i].ValueKind == JsonValueKind.String ? questions[i].GetString() : null)));
        return new PrintValue(blocks);
    }

    // ---------- Подпись составителей ----------

    // «Составитель доцент ______ И. О. Фамилия» по строке на составителя; нет составителей — бланк шаблона остаётся.
    private static PrintValue Developers(PrintContext c) =>
        new(Items(c.At("/approval_sheet/developers"))
            .Where(d => !string.IsNullOrWhiteSpace(Text(d, "position")) || !string.IsNullOrWhiteSpace(Text(d, "name_for_document")))
            .Select(d => (PrintBlock)PrintParagraph.Plain(
                $"Составитель {Text(d, "position")?.Trim()} ____________________ {Text(d, "name_for_document")?.Trim()}"))
            .ToList());

    // ---------- Текущий контроль и оценочные средства ----------

    private static PrintValue CurrentControl(PrintContext c)
    {
        var blocks = new List<PrintBlock>();
        var forms = Strings(c.At("/current_control/forms"));
        if (forms.Count > 0)
            blocks.Add(new PrintParagraph([new PrintRun("Формы текущей аттестации: ", Bold: true), new PrintRun(string.Join("; ", forms) + ".")]));

        string? module = null;
        foreach (var activity in Items(c.At("/current_control/activities")))
        {
            if (Text(activity, "module") is { Length: > 0 } name && name != module)
                blocks.Add(PrintParagraph.Plain(module = name, bold: true));
            blocks.AddRange(Lead(new PrintRun((Text(activity, "title") ?? "").TrimEnd('.') + ". ", Bold: true), Rich(c, Text(activity, "description"))));
        }

        var variants = Items(c.At("/current_control/variants")).Select(v => Rich(c, v.ValueKind == JsonValueKind.String ? v.GetString() : null))
            .Where(b => b.Count > 0).ToList();
        if (variants.Count > 0)
        {
            blocks.Add(PrintParagraph.Plain("Программы и типовые варианты", bold: true));
            foreach (var variant in variants) blocks.AddRange(variant);
        }

        if (Rich(c, RawString(c.At("/current_control/rating"))) is { Count: > 0 } rating)
        {
            blocks.Add(PrintParagraph.Plain("Модульно-рейтинговые атрибуты", bold: true));
            blocks.AddRange(rating);
        }
        return blocks.Count == 0 ? PrintValue.Text(NoCurrentControl) : new PrintValue(blocks);
    }

    // «Методические материалы, определяющие процедуры оценивания»: наименование, описание, критерии и проверяемые индикаторы.
    private static PrintValue Means(PrintContext c)
    {
        var blocks = new List<PrintBlock>();
        var number = 0;
        foreach (var means in Items(c.At("/evaluation_means")))
        {
            blocks.Add(PrintParagraph.Plain($"{++number}. {Text(means, "title")}", bold: true));
            blocks.AddRange(Rich(c, Text(means, "description")));
            var criteria = Rich(c, Text(means, "criteria"));
            if (criteria.Count > 0)
            {
                blocks.Add(PrintParagraph.Plain("Критерии оценки", bold: true));
                blocks.AddRange(criteria);
            }
            if (Strings(Prop(means, "indicator_codes")) is { Count: > 0 } codes)
                blocks.Add(new PrintParagraph([new PrintRun("Проверяемые индикаторы: ", Bold: true), new PrintRun(string.Join(", ", codes) + ".")]));
        }
        return blocks.Count == 0 ? PrintValue.Text(NoMeans) : new PrintValue(blocks);
    }

    // ---------- Критерии формирования оценок ----------

    // Типовые критерии разд. 6 Положения о порядке формирования ФОС (2022): баллов в них нет.
    private static readonly (string Grade, string Description)[] FivePoint =
    [
        ("Отлично", "Обучающийся овладел элементами компетенции «знать», «уметь» и «владеть», проявил всесторонние и глубокие знания " +
            "программного материала по дисциплине, освоил основную и дополнительную литературу, обнаружил творческие способности в понимании, " +
            "изложении и практическом использовании усвоенных знаний."),
        ("Хорошо", "Обучающийся овладел элементами компетенции «знать» и «уметь», проявил полное знание программного материала по дисциплине, " +
            "освоил основную рекомендованную литературу, обнаружил стабильный характер знаний и умений и способен к их самостоятельному " +
            "применению и обновлению в ходе последующего обучения и практической деятельности."),
        ("Удовлетворительно", "Обучающийся овладел элементами компетенции «знать», проявил знания основного программного материала по " +
            "дисциплине в объёме, необходимом для последующего обучения и предстоящей практической деятельности, знаком с основной " +
            "рекомендованной литературой, но допустил неполные или слабо аргументированные ответы, испытывает затруднения в выполнении " +
            "практических заданий."),
        ("Неудовлетворительно", "Обучающийся не овладел ни одним из элементов компетенции, обнаружил значительные пробелы в знании " +
            "программного материала по дисциплине, допустил принципиальные ошибки при применении теоретических знаний, которые не " +
            "позволяют ему продолжить обучение или приступить к практической деятельности без дополнительной подготовки по данной дисциплине.")
    ];

    private static readonly (string Grade, string Description)[] PassFail =
    [
        ("Зачтено", "Соответствует критериям оценок «отлично», «хорошо» и «удовлетворительно»."),
        ("Не зачтено", "Соответствует критерию оценки «неудовлетворительно».")
    ];

    // Пустая шкала — типовые критерии по виду аттестации РПД: один зачёт — «зачтено / не зачтено», иначе пятибалльная.
    private static PrintValue Scale(PrintContext c)
    {
        var custom = Items(c.At("/evaluation_scale/criteria"))
            .Select(i => (Grade: Text(i, "grade") ?? "", Description: Text(i, "description") ?? "", Min: Num(i, "min_score"), Max: Num(i, "max_score")))
            .Where(i => i.Grade.Length > 0 || i.Description.Length > 0).ToList();
        if (custom.Count == 0)
        {
            var type = Text(c.At("/evaluation_scale"), "type");
            var types = Items(c.At("/linked_rpd/semesters")).SelectMany(s => Strings(Prop(s, "control_types"))).ToList();
            var passFail = type == "pass_fail" || (string.IsNullOrEmpty(type) && types.Count > 0 && types.All(t => t == "credit"));
            custom = (passFail ? PassFail : FivePoint).Select(t => (t.Grade, t.Description, (double?)null, (double?)null)).ToList();
        }

        var scored = custom.Any(i => i.Min is not null || i.Max is not null);
        var rows = new List<PrintTableRow> { Row(true, "Оценка", scored ? "Баллы" : null, "Критерии оценки") };
        rows.AddRange(custom.Select(i => Row(false, i.Grade, scored ? Scores(i.Min, i.Max) : null, i.Description)));
        return new PrintValue([new PrintTable(rows)]);

        static PrintTableRow Row(bool header, string grade, string? scores, string description) =>
            new(new[] { grade, scores, description }.Where(t => t is not null)
                .Select(t => new PrintTableCell([PrintParagraph.Plain(t!)], Header: header)).ToList());
    }

    private static string Scores(double? min, double? max) => (min, max) switch
    {
        (null, null) => "",
        (null, _) => $"до {max}",
        (_, null) => $"от {min}",
        _ => $"{min}–{max}"
    };

    // ---------- Паспорт ----------

    /// <summary>Таблица 1 паспорта: разделы, компетенции и число вопросов, заданий и тестов — по банку, а не по вводу.</summary>
    public static IReadOnlyList<SectionRow> SectionRows(PrintContext c)
    {
        var groups = Groups(c);
        var rpdSections = Items(c.At("/linked_rpd/sections"));
        return Items(c.At("/passport/sections")).Select(row =>
        {
            var number = Int(row, "section_number") ?? 0;
            var title = Text(row, "section_title") is { Length: > 0 } own ? own
                : Text(rpdSections.FirstOrDefault(s => Int(s, "number") == number), "title") ?? $"Раздел {number}";
            var taken = new HashSet<(string, int)>();
            foreach (var reference in Items(Prop(row, "tasks")))
                foreach (var found in Referenced(groups, reference)) taken.Add((found.Group.Name, found.Number));
            int Count(string kind) => taken.Count(t => groups.First(g => g.Name == t.Item1).Kind == kind);
            return new SectionRow(number, title, string.Join(", ", Strings(Prop(row, "competence_codes"))), Count("questions"), Count("tasks"), Count("tests"));
        }).ToList();
    }

    /// <summary>Таблица 2 паспорта: компетенция, индикатор, разделы РПД и ссылки на задания по порядку строк паспорта.</summary>
    public static IReadOnlyList<PassportRow> PassportRows(PrintContext c)
    {
        var groups = Groups(c);
        var competences = Items(c.At("/linked_rpd/competences"));
        var indicators = Items(c.At("/competence_indicators"));
        var rpdSections = Items(c.At("/linked_rpd/sections"));
        return Items(c.At("/passport/indicators")).Select(row =>
        {
            var competenceCode = Text(row, "competence_code") ?? "";
            var indicatorCode = Text(row, "indicator_code") ?? "";
            var formulation = Text(competences.FirstOrDefault(x => Text(x, "code") == competenceCode), "formulation");
            var text = Text(indicators.FirstOrDefault(x => Text(x, "code") == indicatorCode), "text")
                ?? Text(competences.SelectMany(x => Items(Prop(x, "indicators"))).FirstOrDefault(x => Text(x, "code") == indicatorCode), "text");
            var sections = Items(Prop(row, "rpd_section_numbers")).Select(n => n.TryGetInt32(out var number) ? number : 0).Where(n => n > 0)
                .Select(n => Text(rpdSections.FirstOrDefault(s => Int(s, "number") == n), "title") is { Length: > 0 } title ? $"{n}. {title}" : $"Раздел {n}")
                .ToList();
            var tasks = Items(Prop(row, "tasks")).Select(r => Describe(groups, r)).Where(t => t.Length > 0).ToList();
            return new PassportRow($"{competenceCode}. {formulation}".TrimEnd('.', ' '), $"{indicatorCode}. {text}".TrimEnd('.', ' '), sections, tasks);
        }).ToList();
    }

    // Задания группы по ссылке: без from и to — вся группа, один номер — from равен to, одна граница — другая берётся по ней.
    private static IEnumerable<(BankGroup Group, int Number)> Referenced(IReadOnlyList<BankGroup> groups, JsonElement reference)
    {
        var group = groups.FirstOrDefault(g => g.Name == Text(reference, "group"));
        if (group is null) yield break;
        var low = Int(reference, "from") ?? Int(reference, "to") ?? 1;
        var high = Int(reference, "to") ?? Int(reference, "from") ?? int.MaxValue;
        foreach (var number in group.Tasks.Select(t => Int(t, "number")).OfType<int>())
            if (number >= low && number <= high)
                yield return (group, number);
    }

    // «Вопросы к зачёту: 1–20»; у теста — «раздел.номер», как в самом банке.
    private static string Describe(IReadOnlyList<BankGroup> groups, JsonElement reference)
    {
        var name = Text(reference, "group") ?? "";
        var group = groups.FirstOrDefault(g => g.Name == name);
        var low = Int(reference, "from") ?? Int(reference, "to");
        var high = Int(reference, "to") ?? Int(reference, "from");
        if (low is null) return name;
        string Number(int? n) => group is null ? $"{n}" : Label(group, n);
        return low == high ? $"{name}: {Number(low)}" : $"{name}: {Number(low)}–{Number(high)}";
    }

    // ---------- Общее ----------

    // Абзац с выделенным началом: «1. Текст» — номер в первом абзаце; список или таблица в начале — номер отдельным абзацем.
    private static IEnumerable<PrintBlock> Lead(PrintRun lead, IReadOnlyList<PrintBlock> blocks)
    {
        if (blocks.Count > 0 && blocks[0] is PrintParagraph { List: PrintListKind.None } first)
            return blocks.Skip(1).Prepend(first with { Runs = first.Runs.Prepend(lead).ToList() });
        return blocks.Prepend(new PrintParagraph([lead with { Text = lead.Text.TrimEnd() }]));
    }

    private static IReadOnlyList<PrintBlock> Rich(PrintContext c, string? html) =>
        string.IsNullOrWhiteSpace(html) ? [] : RichTextBlocks.Parse(html, c.NextListId);

    private static string? RawString(JsonElement? node) => node is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;

    private static List<JsonElement> Items(JsonElement? array) =>
        array is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray().ToList() : [];

    private static List<string> Strings(JsonElement? array) =>
        Items(array).Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()!.Trim()).Where(s => s.Length > 0).ToList();

    private static JsonElement? Prop(JsonElement? node, string name) =>
        node is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) ? v : null;

    private static string? Text(JsonElement? node, string name) =>
        name.Length > 0 && Prop(node, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    private static int? Int(JsonElement? node, string name) =>
        Prop(node, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var n) ? n : null;

    private static double? Num(JsonElement? node, string name) =>
        Prop(node, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetDouble(out var n) ? n : null;
}
