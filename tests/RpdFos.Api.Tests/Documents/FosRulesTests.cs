using System.Text.Json;
using System.Text.Json.Nodes;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;
using RpdFos.Core.DocumentTypes;
using Xunit;
using S = RpdFos.Core.Documents.DocumentStatuses;

namespace RpdFos.Api.Tests.Documents;

/// <summary>
/// Правила ФОС и сквозная проверка РПД ↔ ФОС (этап 7, B8; FR-VAL-04, FR-VAL-07, FR-VAL-09, FR-VAL-10): каждое правило — на паре
/// РПД и ФОС с расхождением и без. Ошибка одна — статус РПД; остальные — предупреждения, отправку не блокируют.
/// </summary>
public class FosRulesTests
{
    private static readonly DocumentTypeRegistry Registry = new();
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    // РПД: две компетенции с тремя индикаторами; два раздела (форма контроля у каждого); экзамен в 1 семестре и зачёт во 2-м.
    private const string RpdPayload = """
        {
          "competences": [
            { "code": "УК-1", "formulation": "Способен", "indicators": [ { "code": "УК-1.1", "text": "а" }, { "code": "УК-1.2", "text": "б" } ] },
            { "code": "ПК-2", "formulation": "Способен", "indicators": [ { "code": "ПК-2.1", "text": "в" } ] }
          ],
          "thematic_plan": [
            { "section_number": 1, "topic_title": "Введение", "semester": 1, "competence_indicators": ["УК-1.1", "УК-1.2"],
              "current_control_forms": ["Контрольная работа"], "hours": { "lectures": 4 } },
            { "section_number": 2, "topic_title": "Основы", "semester": 2, "competence_indicators": ["ПК-2.1"],
              "current_control_forms": ["Тест", "Лабораторная работа"], "hours": { "lab_hours": 8 } }
          ],
          "hours_distribution": { "semesters": [
            { "semester": 1, "control_types": ["exam"] },
            { "semester": 2, "control_types": ["credit"] } ] }
        }
        """;

    private static DisciplineFacts Facts(string status = S.UnderReview, bool enforce = true, bool deleted = false, string rpd = RpdPayload) =>
        new(new Discipline { Name = "Дисциплина", Code = "Б1.О.99" }, new CurriculumPlan { Year = 2026 }, new StudyDirection(),
            new StudyProfile(), null, [],
            LinkedRpd: new LinkedRpdInfo(Guid.NewGuid(), status, deleted, FosSnapshot.Take(JsonNode.Parse(rpd)), enforce));

    private static List<DocumentValidationError> Run(IValidationRule rule, string fos, DisciplineFacts? facts = null, string path = "/x") =>
        rule.Check(new RuleContext(path, default, JsonDocument.Parse(fos).RootElement, facts ?? Facts(), Now)).ToList();

    // Группа банка из заданий с номерами 1..count (или заданными).
    private static string Group(string name, string kind, string control, int count, string extra = "") =>
        Group(name, kind, control, Enumerable.Range(1, count), extra);

    private static string Group(string name, string kind, string control, IEnumerable<int> numbers, string extra = "") =>
        $$"""
        { "group": "{{name}}", "kind": "{{kind}}", "control_form": "{{control}}" {{extra}},
          "tasks": [ {{string.Join(", ", numbers.Select(n => $$"""{ "number": {{n}}, "text": "Задание {{n}}" }"""))}} ] }
        """;

    private static string Fos(string bank = "", string passport = "", string means = "", string current = "", string scale = "",
        string tickets = "")
    {
        var parts = new List<string>();
        if (bank != "") parts.Add($"\"task_bank\": [{bank}]");
        if (passport != "") parts.Add($"\"passport\": {passport}");
        if (means != "") parts.Add($"\"evaluation_means\": [{means}]");
        if (current != "") parts.Add($"\"current_control\": {current}");
        if (scale != "") parts.Add($"\"evaluation_scale\": {scale}");
        if (tickets != "") parts.Add($"\"exam_tickets\": [{tickets}]");
        return "{" + string.Join(",", parts) + "}";
    }

    // ───── linked_rpd_must_be_approved_or_under_review ─────

    [Theory]
    [InlineData(S.UnderReview)]
    [InlineData(S.Accepted)]
    [InlineData(S.Approval)]
    [InlineData(S.Approved)]
    public void The_fos_may_be_submitted_when_the_rpd_is_under_review_or_further(string status)
    {
        Assert.Empty(Run(new LinkedRpdMustBeApprovedOrUnderReview(), "{}", Facts(status), "/linked_rpd"));
    }

    [Theory]
    [InlineData(S.Draft)]
    [InlineData(S.SavedDraft)]
    [InlineData(S.Returned)]
    [InlineData(S.Archived)]
    public void The_fos_is_refused_while_the_rpd_is_a_draft_returned_or_archived(string status)
    {
        var rule = new LinkedRpdMustBeApprovedOrUnderReview();
        var issue = Assert.Single(Run(rule, "{}", Facts(status), "/linked_rpd"));

        Assert.Equal(("/linked_rpd", IssueLevels.Error), (issue.Path, rule.Level));
        Assert.Contains($"«{S.Title(status)}»", issue.Message);
    }

    [Fact]
    public void A_deleted_or_missing_rpd_refuses_the_fos_and_a_fos_past_review_is_not_held_by_the_rpd_status()
    {
        var rule = new LinkedRpdMustBeApprovedOrUnderReview();

        Assert.Contains("удалена", Assert.Single(Run(rule, "{}", Facts(S.Approved, deleted: true), "/linked_rpd")).Message);
        var missing = new DisciplineFacts(new Discipline(), new CurriculumPlan(), new StudyDirection(), new StudyProfile(), null, []);
        Assert.Contains("не найдена", Assert.Single(Run(rule, "{}", missing, "/linked_rpd")).Message);
        // ФОС принят и идёт дальше: РПД, ушедшая в архив, согласование не останавливает.
        Assert.Empty(Run(rule, "{}", Facts(S.Archived, enforce: false), "/linked_rpd"));
    }

    // ───── all_indicators_have_means ─────

    [Fact]
    public void Every_indicator_of_the_rpd_needs_a_means_and_the_missing_ones_are_named()
    {
        var rule = new AllIndicatorsHaveMeans();
        var all = Fos(means: """
            { "means_type": "test", "title": "Тест", "indicator_codes": ["УК-1.1", "УК-1.2"] },
            { "means_type": "essay", "title": "Реферат", "indicator_codes": ["ПК-2.1"] }
            """);
        var some = Fos(means: """{ "means_type": "test", "title": "Тест", "indicator_codes": ["УК-1.1"] }""");

        Assert.Empty(Run(rule, all));
        Assert.Equal(["У индикатора ПК-2.1 нет оценочного средства", "У индикатора УК-1.2 нет оценочного средства"],
            Run(rule, some).Select(i => i.Message).Order());
        Assert.All(Run(rule, some), i => Assert.Equal(("/evaluation_means", IssueLevels.Warning), (i.Path, rule.Level)));
    }

    [Fact]
    public void The_means_rule_reads_the_current_rpd_not_the_snapshot_and_works_without_the_section()
    {
        var rule = new AllIndicatorsHaveMeans();
        // Раздела «Оценочные средства» нет вовсе — это и есть расхождение: у всех трёх индикаторов средства нет.
        Assert.Equal(3, Run(rule, "{}").Count);
        Assert.True(rule.WhenAbsent);
        // РПД без компетенций — сравнивать не с чем.
        Assert.Empty(Run(rule, "{}", Facts(rpd: """{ "competences": [] }""")));
    }

    // ───── fos_passport_consistent ─────

    private const string GoodPassport = """
        { "sections": [ { "section_number": 1, "competence_codes": ["УК-1"], "tasks": [ { "group": "Раздел 1" } ] } ],
          "indicators": [
            { "competence_code": "УК-1", "indicator_code": "УК-1.1", "tasks": [ { "group": "Раздел 1", "from": 1, "to": 2 } ] },
            { "competence_code": "УК-1", "indicator_code": "УК-1.2", "tasks": [ { "group": "Раздел 1", "from": 3, "to": 3 } ] },
            { "competence_code": "ПК-2", "indicator_code": "ПК-2.1", "tasks": [ { "group": "Раздел 1" } ] } ] }
        """;

    [Fact]
    public void A_consistent_passport_gives_no_issues()
    {
        Assert.Empty(Run(new FosPassportConsistent(), Fos(bank: Group("Раздел 1", "questions", "current", 3), passport: GoodPassport)));
    }

    [Fact]
    public void A_reference_to_a_missing_group_or_missing_numbers_is_named_with_its_place()
    {
        var passport = """
            { "sections": [ { "section_number": 1, "tasks": [ { "group": "Нет такой" } ] } ],
              "indicators": [
                { "indicator_code": "УК-1.1", "tasks": [ { "group": "Раздел 1", "from": 2, "to": 6 } ] },
                { "indicator_code": "УК-1.2", "tasks": [ { "group": "Раздел 1", "from": 9 } ] },
                { "indicator_code": "ПК-2.1", "tasks": [ { "group": "Раздел 1", "to": 0 } ] } ] }
            """;

        var issues = Run(new FosPassportConsistent(), Fos(bank: Group("Раздел 1", "questions", "current", 4), passport: passport));

        Assert.Contains(issues, i => i.Path == "/passport/sections/0/tasks/0" && i.Message == "Раздел 1: в банке заданий нет группы «Нет такой»");
        Assert.Contains(issues, i => i.Path == "/passport/indicators/0/tasks/0" &&
                                     i.Message == "Индикатор УК-1.1: в группе «Раздел 1» нет заданий № 5, 6");
        Assert.Contains(issues, i => i.Path == "/passport/indicators/1/tasks/0" &&
                                     i.Message == "Индикатор УК-1.2: в группе «Раздел 1» нет заданий начиная с № 9");
        Assert.Contains(issues, i => i.Path == "/passport/indicators/2/tasks/0" &&
                                     i.Message == "Индикатор ПК-2.1: в группе «Раздел 1» нет заданий до № 0");
    }

    [Fact]
    public void An_indicator_without_a_task_and_a_section_missing_from_the_rpd_are_reported()
    {
        var passport = """
            { "sections": [ { "section_number": 7, "tasks": [ { "group": "Раздел 1" } ] } ],
              "indicators": [ { "indicator_code": "УК-1.1", "tasks": [ { "group": "Раздел 1" } ] },
                              { "indicator_code": "УК-1.2", "tasks": [] } ] }
            """;

        var issues = Run(new FosPassportConsistent(), Fos(bank: Group("Раздел 1", "questions", "current", 2), passport: passport));

        Assert.Contains(issues, i => i.Path == "/passport/sections/0" && i.Message == "Раздела 7 нет в РПД");
        var unbound = issues.Where(i => i.Path == "/passport/indicators").Select(i => i.Message).Order().ToList();
        Assert.Equal(["Индикатор ПК-2.1 не привязан ни к одному заданию", "Индикатор УК-1.2 не привязан ни к одному заданию"], unbound);
    }

    [Fact]
    public void Gaps_in_the_numbering_of_a_group_are_reported_and_a_continuous_group_is_not()
    {
        var bank = Group("Пропуски", "questions", "current", [1, 2, 5, 9]) + "," + Group("Подряд", "tasks", "current", 3);

        var issue = Assert.Single(Run(new FosPassportConsistent(), Fos(bank: bank, passport: GoodPassport.Replace("Раздел 1", "Подряд"))),
            i => i.Path == "/task_bank");

        Assert.Equal("В группе «Пропуски» пропущены номера: 3, 4, 6, 7, 8 — «Перенумеровать» уберёт пропуски", issue.Message);
    }

    // ───── scale_matches_control_type ─────

    [Fact]
    public void Every_attestation_of_the_rpd_needs_intermediate_means_in_the_fos()
    {
        var rule = new ScaleMatchesControlType();
        var none = Run(rule, Fos(bank: Group("Текущий", "questions", "current", 3)));
        var covered = Run(rule, Fos(
            bank: Group("К экзамену", "questions", "intermediate", 5, ", \"semesters\": [1]"),
            means: """{ "means_type": "credit_questions", "title": "Вопросы к зачёту" }"""));

        Assert.Equal(["1 семестр РПД: экзамен — в ФОС нет средств промежуточной аттестации (группы банка заданий или вопросов)",
                "2 семестр РПД: зачёт — в ФОС нет средств промежуточной аттестации (группы банка заданий или вопросов)"],
            none.Select(i => i.Message));
        Assert.Empty(covered);
    }

    [Theory]
    [InlineData("five_point", "exam", false)]
    [InlineData("hundred_point", "graded_credit", false)]
    [InlineData("pass_fail", "credit", false)]
    [InlineData("pass_fail", "exam", true)]
    [InlineData("five_point", "credit", true)]
    [InlineData("hundred_point", "credit", true)]
    public void The_scale_must_fit_the_attestation_type_of_the_rpd(string scale, string type, bool mismatch)
    {
        var rpd = $$"""{ "hours_distribution": { "semesters": [ { "semester": 1, "control_types": ["{{type}}"] } ] } }""";
        var fos = Fos(bank: Group("Итог", "questions", "intermediate", 3), scale: $$"""{ "type": "{{scale}}" }""");

        var issues = Run(new ScaleMatchesControlType(), fos, Facts(rpd: rpd));

        Assert.Equal(mismatch, issues.Any(i => i.Path == "/evaluation_scale/type"));
    }

    [Fact]
    public void Credit_and_exam_together_accept_either_scale_and_an_empty_scale_is_not_checked()
    {
        var rule = new ScaleMatchesControlType();
        var bank = Group("Итог", "questions", "intermediate", 3);

        Assert.Empty(Run(rule, Fos(bank: bank, scale: """{ "type": "pass_fail" }""")));
        Assert.Empty(Run(rule, Fos(bank: bank, scale: """{ "type": "five_point" }""")));
        Assert.Empty(Run(rule, Fos(bank: bank)));
    }

    // ───── control_forms_match ─────

    [Fact]
    public void Forms_of_current_control_are_matched_loosely_by_the_beginning_of_words()
    {
        var rule = new ControlFormsMatch();
        var matched = Fos(current: """
            { "forms": ["Тестирование"],
              "activities": [ { "module": "Модуль 1", "title": "Контрольные работы по разделу" },
                              { "title": "Отчёт по лабораторной работе" } ] }
            """);

        Assert.Empty(Run(rule, matched));
    }

    [Fact]
    public void A_form_without_an_activity_is_reported_with_its_sections_of_the_rpd()
    {
        var rule = new ControlFormsMatch();
        var fos = Fos(current: """{ "activities": [ { "title": "Контрольная работа" }, { "title": "Опрос" } ] }""");

        var issues = Run(rule, fos);

        Assert.Equal(["Форма текущего контроля «Лабораторная работа» (раздел 2 РПД) не представлена мероприятиями ФОС",
                "Форма текущего контроля «Тест» (раздел 2 РПД) не представлена мероприятиями ФОС"],
            issues.Select(i => i.Message).Order());
        Assert.All(issues, i => Assert.Equal("/current_control", i.Path));
        // Раздела «Текущий контроль» нет вообще: все три формы РПД не представлены.
        Assert.Equal(3, Run(rule, "{}").Count);
    }

    [Theory]
    [InlineData("Контрольная работа", "Контрольные работы", true)]
    [InlineData("Тест", "Тестирование", true)]
    [InlineData("Устный опрос", "Опрос", false)]
    [InlineData("Расчётно-графическая работа", "Расчетно-графические работы", true)]
    [InlineData("Контрольная работа", "Контрольный опрос", false)]
    public void Loose_matching_of_forms(string form, string activity, bool matched)
    {
        var rpd = $$"""{ "thematic_plan": [ { "section_number": 1, "current_control_forms": ["{{form}}"] } ] }""";
        var fos = Fos(current: $$"""{ "activities": [ { "title": "{{activity}}" } ] }""");

        Assert.Equal(!matched, Run(new ControlFormsMatch(), fos, Facts(rpd: rpd)).Count == 1);
    }

    // ───── fos_regulation_thresholds ─────

    [Fact]
    public void The_regulation_thresholds_count_questions_per_attestation_and_semester()
    {
        var rule = new FosRegulationThresholds();
        var few = Run(rule, Fos(bank: Group("К экзамену", "questions", "intermediate", 12, ", \"semesters\": [1]")));
        var enough = Run(rule, Fos(bank:
            Group("К экзамену", "questions", "intermediate", 20, ", \"semesters\": [1]") + "," +
            Group("К зачёту", "questions", "intermediate", 20, ", \"semesters\": [2]")));

        Assert.Contains(few, i => i.Message == "1 семестр, экзамен: вопросов в банке 12, по Положению не менее 20");
        Assert.Contains(few, i => i.Message == "2 семестр, зачёт: вопросов в банке 0, по Положению не менее 20");
        Assert.DoesNotContain(enough, i => i.Message.Contains("вопросов в банке"));
        // Вопросы текущего контроля к вопросам аттестации не относятся.
        Assert.Contains(Run(rule, Fos(bank: Group("Текущие", "questions", "current", 40))), i => i.Message.StartsWith("1 семестр, экзамен"));
    }

    [Fact]
    public void Tests_need_ten_items_per_section_and_the_exam_needs_tickets_or_a_test_group()
    {
        var rule = new FosRegulationThresholds();
        var lowTests = Fos(bank: Group("Тесты 1", "tests", "current", 6, ", \"section_number\": 1"));
        var okTests = Fos(bank: Group("Тесты 1", "tests", "current", 6, ", \"section_number\": 1") + "," +
                                Group("Тесты 1б", "tests", "current", 5, ", \"section_number\": 1"));

        Assert.Contains(Run(rule, lowTests), i => i.Message == "Раздел 1: тестовых заданий 6, по Положению не менее 10");
        Assert.DoesNotContain(Run(rule, okTests), i => i.Message.Contains("тестовых заданий"));

        // Экзамен в РПД есть: билетов 3 и группы тестов промежуточной аттестации нет — предупреждение; с группой — нет.
        var tickets = string.Join(",", Enumerable.Range(1, 3).Select(n => $$"""{ "number": {{n}}, "questions": ["а", "б"] }"""));
        Assert.Contains(Run(rule, Fos(tickets: tickets)), i =>
            i.Path == "/exam_tickets" && i.Message.StartsWith("К экзамену в ФОС билетов 3"));
        Assert.DoesNotContain(Run(rule, Fos(bank: Group("Итоговый тест", "tests", "intermediate", 12, ", \"section_number\": 1"), tickets: tickets)),
            i => i.Path == "/exam_tickets");
        Assert.DoesNotContain(Run(rule, Fos(tickets: string.Join(",", Enumerable.Range(1, 25).Select(n =>
            $$"""{ "number": {{n}}, "questions": ["а", "б"] }""")))), i => i.Path == "/exam_tickets");
    }

    [Fact]
    public void Every_evaluation_means_needs_filled_criteria()
    {
        var rule = new FosRegulationThresholds();
        var means = """
            { "means_type": "test", "title": "Тест", "criteria": "<p>&nbsp;</p>" },
            { "means_type": "essay", "title": "Реферат", "criteria": "<p>Оценка «отлично» — тема раскрыта.</p>" },
            { "means_type": "project", "title": "Проект", "criteria": "<table><tr><td>5</td></tr></table>" },
            { "means_type": "case_study", "title": "Кейс" }
            """;

        var issues = Run(rule, Fos(means: means)).Where(i => i.Path.StartsWith("/evaluation_means")).ToList();

        Assert.Equal(["/evaluation_means/0/criteria", "/evaluation_means/3/criteria"], issues.Select(i => i.Path));
        Assert.Equal("У оценочного средства «Тест» не заполнены критерии оценок", issues[0].Message);
    }

    // ───── через реестр правил: уровни, отправка и приём ─────

    private static IValidationRule[] AllFosRules() =>
    [
        new LinkedRpdMustBeApprovedOrUnderReview(), new AllIndicatorsHaveMeans(), new FosPassportConsistent(),
        new ScaleMatchesControlType(), new ControlFormsMatch(), new FosRegulationThresholds()
    ];

    private static IReadOnlyList<DocumentValidationError> RunSet(string fos, DisciplineFacts facts)
    {
        var schema = JsonDocument.Parse(Registry.Get(DocumentTypeRegistry.Fos).SchemaJson).RootElement;
        return new ValidationRuleSet(AllFosRules(), TimeProvider.System)
            .Run(schema, JsonDocument.Parse(fos).RootElement, facts, Registry.FindSchema, new HashSet<string>());
    }

    // ФОС с обязательными разделами схемы: правила ходят по полям, которые есть в содержимом.
    private const string Skeleton = """{ "linked_rpd": {}, "passport": {}, "competence_indicators": [] }""";

    [Fact]
    public void Warnings_name_their_rule_and_do_not_block_the_submission_but_the_rpd_status_does()
    {
        var onReview = RunSet(Skeleton, Facts(S.UnderReview));
        var onDraft = RunSet(Skeleton, Facts(S.Draft));

        Assert.NotEmpty(onReview);
        Assert.All(onReview, i => Assert.NotNull(i.Rule));
        Assert.False(DocumentSubmission.HasErrors(onReview), "предупреждения не блокируют отправку");
        Assert.Equal(["all_indicators_have_means", "control_forms_match", "fos_passport_consistent", "fos_regulation_thresholds",
                "scale_matches_control_type"], onReview.Select(i => i.Rule!).Distinct().Order());
        Assert.True(DocumentSubmission.HasErrors(onDraft), "РПД-черновик: ФОС не отправляется");
        var error = Assert.Single(onDraft, i => i.Level == IssueLevels.Error);
        Assert.Equal(("/linked_rpd", "linked_rpd_must_be_approved_or_under_review"), (error.Path, error.Rule));
    }

    [Fact]
    public void Rules_that_need_the_absent_section_run_for_it_but_not_on_saving()
    {
        var schema = JsonDocument.Parse(Registry.Get(DocumentTypeRegistry.Fos).SchemaJson).RootElement;
        var set = new ValidationRuleSet(AllFosRules(), TimeProvider.System);
        var payload = JsonDocument.Parse(Skeleton).RootElement;

        // Нет ни средств, ни банка, ни текущего контроля: без этих разделов в содержимом правила всё равно называют расхождения.
        Assert.Contains(set.Run(schema, payload, Facts(), Registry.FindSchema, new HashSet<string>()),
            i => i.Rule == "all_indicators_have_means" && i.Path == "/evaluation_means");
        // Сохранение раздела проверяет только ошибки в самих данных: сквозные правила — к отправке и в панели.
        Assert.Empty(set.Run(schema, payload, Facts(), Registry.FindSchema, new HashSet<string>(), onSave: true));
    }

    [Fact]
    public void The_schema_binds_every_fos_rule_and_all_rules_are_in_the_catalog()
    {
        var bound = SchemaInspector.Extensions(Registry.Get(DocumentTypeRegistry.Fos).Schema)
            .Where(e => e.Keyword == "x-validation-rule").Select(e => e.Value).ToHashSet();

        Assert.Equal(AllFosRules().Select(r => r.Name).Order(), bound.Order());
        Assert.Subset(ValidationRules.Names.ToHashSet(), bound);
    }
}
