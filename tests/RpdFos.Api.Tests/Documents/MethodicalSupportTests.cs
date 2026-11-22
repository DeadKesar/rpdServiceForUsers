using System.Text.Json;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;
using RpdFos.Core.DocumentTypes;
using Xunit;

namespace RpdFos.Api.Tests.Documents;

/// <summary>
/// Учебно-методическое обеспечение РПД (этап 7.1): ссылка у литературы и методические указания записью «текст и/или ссылка».
/// Схема нового шаблона — как её видит проверка содержимого; прежние снимки схем этих полей не получают.
/// </summary>
public class MethodicalSupportTests
{
    private static readonly DocumentTypeRegistry Registry = new();
    private static readonly DocumentValidator Validator = new(Registry);
    private static readonly string Rpd = Registry.Get(DocumentTypeRegistry.Rpd).SchemaJson;
    private static readonly ValidationRuleSet Rules = new([new GuidelineTextOrUrl()], TimeProvider.System);

    // В плане есть лабораторные и практические занятия: все три места методических указаний предусмотрены.
    private static readonly DisciplineFacts Facts = new(
        new Discipline
        {
            Name = "Дисциплина", Code = "Б1.О.99",
            Semesters = [new DisciplineSemesterDetails { Semester = 1, Lectures = 16, Practice = 16, LabHours = 16 }]
        },
        new CurriculumPlan { Year = 2026 }, new StudyDirection(), new StudyProfile(), null, []);

    private const string Citation = "Иванов И. И. Моделирование систем : учебник. — Москва : Станкин, 2024. — 320 с.";

    private static IReadOnlyList<DocumentValidationError> Literature(string main, ValidationMode mode = ValidationMode.Submit) =>
        Validator.Validate(Rpd, JsonDocument.Parse($$"""{ "literature": { "main": {{main}} } }""").RootElement, mode)
            .Where(e => e.Path.StartsWith("/literature/main/")).ToList();

    private static IReadOnlyList<DocumentValidationError> Guidelines(string guidelines) =>
        DocumentSubmission.Check(Rpd, JsonDocument.Parse($$"""{ "literature": { "guidelines": {{guidelines}} } }""").RootElement, Facts,
                Validator, Rules, Registry.FindSchema)
            .Where(e => e.Path.StartsWith("/literature/guidelines")).ToList();

    [Fact]
    public void Literature_link_is_optional()
    {
        // Со ссылкой, без неё и со ссылкой на сайт с кириллическим именем — проверка не находит замечаний к полю.
        Assert.Empty(Literature($$"""
            [ { "citation": "{{Citation}}", "year": 2024, "url": "https://e.lanbook.com/book/123456" },
              { "citation": "{{Citation}}" },
              { "citation": "{{Citation}}", "url": "http://библиотека.рф/книги/1?id=2#часть" } ]
            """));
    }

    [Theory]
    [InlineData("в научной библиотеке")]
    [InlineData("www.lib.example/book")]
    [InlineData("ftp://files.example/book.pdf")]
    [InlineData("mailto:lib@example.org")]
    [InlineData("https://")]
    [InlineData("https://lib example/book")]
    public void Wrong_literature_address_is_a_field_error(string address)
    {
        var errors = Literature($$"""[ { "citation": "{{Citation}}", "url": "{{address}}" } ]""");

        var error = Assert.Single(errors);
        Assert.Equal("/literature/main/0/url", error.Path);
        Assert.StartsWith("Неверный формат. Адрес издания", error.Message);
        // Ошибка адреса блокирует и сохранение черновика: как неверный тип или формат даты.
        Assert.Single(Literature($$"""[ { "citation": "{{Citation}}", "url": "{{address}}" } ]""", ValidationMode.Draft));
    }

    [Fact]
    public void Guideline_with_text_only_passes()
    {
        Assert.Empty(Guidelines("""{ "lab_works": { "text": "Лабораторный практикум, ч. 1\nЛабораторный практикум, ч. 2" } }"""));
    }

    [Fact]
    public void Guideline_with_link_only_or_both_passes()
    {
        Assert.Empty(Guidelines("""
            { "lab_works": { "url": "https://edu.stankin.ru/course/1" },
              "practical_classes": { "text": "Сборник задач", "url": "https://edu.stankin.ru/course/2" } }
            """));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "text": "   " }""")]
    [InlineData("""{ "text": "", "url": "" }""")]
    public void Empty_guideline_is_a_field_error(string record)
    {
        var error = Assert.Single(Guidelines($$"""{ "independent_work": {{record}} }"""));

        Assert.Equal(("/literature/guidelines/independent_work", IssueLevels.Error), (error.Path, error.Level));
        Assert.Equal("Укажите текст или ссылку на методические указания", error.Message);
    }

    [Fact]
    public void Guideline_that_is_absent_is_not_an_error()
    {
        // Указания необязательны: нет записи — нет замечаний; начатая и оставленная пустой — ошибка (см. выше).
        Assert.Empty(Guidelines("{}"));
    }

    [Fact]
    public void Wrong_guideline_address_is_a_field_error_even_with_text()
    {
        var error = Assert.Single(Guidelines("""{ "lab_works": { "text": "Практикум", "url": "в ЭОС" } }"""));

        Assert.Equal("/literature/guidelines/lab_works/url", error.Path);
        Assert.StartsWith("Неверный формат.", error.Message);
    }

    [Fact]
    public void Empty_guideline_is_a_draft_not_an_error_on_save()
    {
        // Черновик с начатой и пустой записью сохраняется: правило проверяет полноту и работает только к отправке.
        var payload = JsonDocument.Parse("""{ "literature": { "guidelines": { "lab_works": {} } } }""").RootElement;

        Assert.False(((IValidationRule)new GuidelineTextOrUrl()).OnSave);
        Assert.Empty(Validator.Validate(Rpd, payload, ValidationMode.Draft));
        Assert.Empty(Rules.Run(JsonDocument.Parse(Rpd).RootElement, payload, Facts, Registry.FindSchema, new HashSet<string>(), onSave: true));
    }

    [Fact]
    public void Lists_of_the_section_are_reorderable_in_new_schema_only()
    {
        // Перестановка — свойство схемы нового шаблона: главные списки раздела помечены, остальные нет.
        var schema = Registry.Get(DocumentTypeRegistry.Rpd).Schema;
        bool Reorderable(params string[] path)
        {
            var node = schema.GetProperty("properties");
            for (var i = 0; i < path.Length; i++)
            {
                node = node.GetProperty(path[i]);
                if (i < path.Length - 1) node = node.GetProperty("properties");
            }
            return node.TryGetProperty("x-reorderable", out var flag) && flag.GetBoolean();
        }

        Assert.All(new[]
        {
            new[] { "literature", "main" }, new[] { "literature", "additional" }, new[] { "literature", "periodicals" },
            new[] { "literature", "internet_resources" }, new[] { "literature", "software" }, new[] { "material_support" }
        }, path => Assert.True(Reorderable(path), string.Join("/", path)));
        // Таблицы занятий и тематический план нумеруются автором — их порядок остаётся порядком добавления.
        Assert.False(Reorderable("lab_works"));
        Assert.False(Reorderable("practical_classes"));
        Assert.False(Reorderable("thematic_plan"));
    }
}
