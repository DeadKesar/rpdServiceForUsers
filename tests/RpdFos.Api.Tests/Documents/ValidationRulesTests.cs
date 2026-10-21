using System.Text.Json;
using Microsoft.Extensions.Options;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;
using RpdFos.Core.DocumentTypes;
using Xunit;

namespace RpdFos.Api.Tests.Documents;

/// <summary>Правила проверки РПД (этап 5, B2): часы тем против плана, покрытие компетенций, возраст литературы.</summary>
public class ValidationRulesTests
{
    private static readonly DocumentTypeRegistry Registry = new();
    private static readonly DocumentValidator Validator = new(Registry);
    private static readonly string Rpd = Registry.Get(DocumentTypeRegistry.Rpd).SchemaJson;
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static readonly IValidationRule[] All =
    [
        new TopicHoursMatchTotal(), new CompetencesMatchMatrix(),
        new LiteratureMainRecent(Options.Create(new ValidationRuleSettings { LiteratureMaxAgeYears = 10 }))
    ];

    // План: два семестра — часы складываются.
    private static readonly DisciplineFacts Facts = new(
        new Discipline
        {
            Name = "Дисциплина", Code = "Б1.О.99",
            Semesters =
            [
                new DisciplineSemesterDetails { Semester = 1, Lectures = 16, Practice = 16, SelfStudy = 40 },
                new DisciplineSemesterDetails { Semester = 2, Lectures = 8, LabHours = 16, SelfStudy = 20 }
            ]
        },
        new CurriculumPlan { Year = 2026 }, new StudyDirection(), new StudyProfile(), null,
        [
            Competence("УК-1", "УК-1.1", "УК-1.2"),
            Competence("ОПК-2", "ОПК-2.1"),
            Competence("ПК-3") // без индикаторов в списке компетенций — выбрать нечего, правило её не требует
        ]);

    [Fact]
    public void Topic_hours_match_plan_by_kind()
    {
        var plan = """
            [ { "hours": { "lectures": 20, "practice": 16, "lab_hours": 16, "self_study": 30 } },
              { "hours": { "lectures": 4, "self_study": 30 } } ]
            """;

        Assert.Empty(Run(new TopicHoursMatchTotal(), plan));
    }

    [Fact]
    public void Topic_hours_mismatch_names_kind_and_both_sums()
    {
        var plan = """[ { "hours": { "lectures": 12, "practice": 16, "lab_hours": 16, "self_study": 60 } } ]""";

        Assert.Equal(
            [new DocumentValidationError("/thematic_plan", "Лекции: по темам 12 ч, по учебному плану 24 ч")],
            Run(new TopicHoursMatchTotal(), plan, "/thematic_plan"));
    }

    [Fact]
    public void Every_matrix_competence_needs_an_own_indicator()
    {
        var competences = """
            [ { "code": "УК-1", "indicators": [ { "code": "УК-1.1" }, { "code": "ОПК-2.1" } ] },
              { "code": "ПК-9", "indicators": [ { "code": "ПК-9.1" } ] } ]
            """;

        Assert.Equal(
            [
                new DocumentValidationError("/competences", "ОПК-2: не выбран ни один индикатор"),
                new DocumentValidationError("/competences/0/indicators/1", "Индикатор ОПК-2.1 не относится к компетенции УК-1"),
                new DocumentValidationError("/competences/1", "Компетенции ПК-9 нет в матрице компетенций дисциплины")
            ],
            Run(new CompetencesMatchMatrix(), competences, "/competences"));
    }

    [Fact]
    public void Matrix_competences_with_own_indicators_pass()
    {
        var competences = """
            [ { "code": "УК-1", "indicators": [ { "code": "УК-1.2" } ] },
              { "code": "ОПК-2", "indicators": [ { "code": "ОПК-2.1" } ] } ]
            """;

        Assert.Empty(Run(new CompetencesMatchMatrix(), competences));
    }

    [Fact]
    public void Old_main_literature_is_a_warning()
    {
        var rule = All[2];
        var main = """[ { "citation": "старый учебник", "year": 2010 }, { "citation": "новый", "year": 2020 }, { "citation": "без года" } ]""";

        Assert.Equal(
            [new DocumentValidationError("/literature/main/0", "Издание 2010 года — старше 10 лет", IssueLevels.Warning)],
            Run(rule, main, "/literature/main"));
    }

    [Fact]
    public void Full_check_runs_rules_from_schema_with_their_levels()
    {
        // Схема РПД привязывает правила к полям; предупреждение отправку не блокирует.
        var year = DateTime.Today.Year;
        var payload = $$"""
            {
              "thematic_plan": [ { "hours": { "lectures": 1 } } ],
              "competences": [ { "code": "УК-1", "indicators": [ { "code": "УК-1.1" } ] } ],
              "literature": { "main": [ { "citation": "Учебник по моделированию систем", "year": {{year - 30}} } ] }
            }
            """;

        var issues = DocumentSubmission.Check(Rpd, JsonDocument.Parse(payload).RootElement, Facts, Validator,
            new ValidationRuleSet(All, TimeProvider.System), Registry.FindSchema);

        Assert.Contains(issues, i => i is { Path: "/thematic_plan", Level: IssueLevels.Error } && i.Message.StartsWith("Лекции"));
        Assert.Contains(issues, i => i is { Path: "/competences", Level: IssueLevels.Error } && i.Message.StartsWith("ОПК-2"));
        Assert.Contains(issues, i => i is { Path: "/literature/main/0", Level: IssueLevels.Warning });
    }

    [Fact]
    public void Implemented_rules_are_named_in_catalog()
    {
        // Реализации — подмножество каталога; правила каталога без реализации пропускаются при проверке.
        var implemented = new ValidationRuleSet(All, TimeProvider.System).Names;

        Assert.Subset(ValidationRules.Names.ToHashSet(), implemented.ToHashSet());
    }

    private static List<DocumentValidationError> Run(IValidationRule rule, string json, string path = "/x") =>
        rule.Check(new RuleContext(path, JsonDocument.Parse(json).RootElement, default, Facts, Now)).ToList();

    private static CompetenceCandidate Competence(string code, params string[] indicators) =>
        new(Guid.NewGuid(), code, $"Формулировка {code}", CompetenceCategories.Universal,
            indicators.Select(i => new IndicatorCandidate(Guid.NewGuid(), i, $"Индикатор {i}", "")).ToList());
}
