using System.Text.Json;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;
using RpdFos.Core.DocumentTypes;
using Xunit;

namespace RpdFos.Api.Tests.Documents;

/// <summary>Условные разделы при отправке: видимый — хотя бы одна запись, скрытый — не проверяется (FR-TPL-14).</summary>
public class DocumentSubmissionTests
{
    private static readonly DocumentTypeRegistry Registry = new();
    private static readonly DocumentValidator Validator = new(Registry);
    private static readonly ValidationRuleSet Rules = new([], TimeProvider.System);
    private static readonly string Rpd = Registry.Get(DocumentTypeRegistry.Rpd).SchemaJson;

    [Theory]
    [InlineData(16, "exam", """{ "lab_works": [] }""", "/lab_works")]
    [InlineData(16, "exam", """{ "lab_works": [ { "number": 1, "title": "Работа", "hours": 4 } ] }""", null)]
    [InlineData(0, "exam", "{}", null)]                                      // лабораторных нет в плане — раздел скрыт
    [InlineData(0, "course_project", "{}", "/course_work")]
    [InlineData(0, "course_project", """{ "course_work": { "kind": "course_project" } }""", null)]
    [InlineData(0, "course_project", """{ "course_work": { "topics": [] } }""", "/course_work")]
    public void Visible_conditional_section_needs_a_record(int labHours, string controlType, string payload, string? missing)
    {
        var facts = Facts(new DisciplineSemesterDetails { Semester = 1, Lectures = 16, LabHours = labHours, ControlTypes = [controlType] });

        var errors = DocumentSubmission.Check(Rpd, JsonDocument.Parse(payload).RootElement, facts, Validator, Rules, Registry.FindSchema)
            .Where(e => e.Message == DocumentSubmission.EmptyVisibleSection).Select(e => e.Path);

        Assert.Equal(missing is null ? [] : [missing], errors);
    }

    [Fact]
    public void Hidden_section_errors_are_ignored()
    {
        // Курсового проекта нет в плане: даже неверное содержимое раздела не мешает отправке.
        var facts = Facts(new DisciplineSemesterDetails { Semester = 1, Lectures = 16, ControlTypes = ["exam"] });

        var errors = DocumentSubmission.Check(Rpd, JsonDocument.Parse("""{ "course_work": { "kind": "essay" } }""").RootElement,
            facts, Validator, Rules, Registry.FindSchema);

        Assert.DoesNotContain(errors, e => e.Path.StartsWith("/course_work"));
    }

    [Theory]
    // Раздел-объект с обязательными полями: ничего — «ни одного», одно из двух — часть, всё и без ошибок — выполнен.
    [InlineData("""{ "type": "object", "required": ["goals", "objectives"] }""", true, null, 2, SectionFill.Empty)]
    [InlineData("""{ "type": "object", "required": ["goals", "objectives"] }""", true, """{ "goals": "Цель" }""", 1, SectionFill.Incomplete)]
    [InlineData("""{ "type": "object", "required": ["goals", "objectives"] }""", true, """{ "goals": "Цель", "objectives": ["З"] }""", 0, SectionFill.Complete)]
    // Список: обязательный или условный — элемент сам список; записи есть, но ошибка правила — часть.
    [InlineData("""{ "type": "array" }""", true, "[]", 1, SectionFill.Empty)]
    [InlineData("""{ "type": "array", "x-visible-if": { "source": "parser.discipline.details", "any": "lab_hours" } }""", false, """[ { "hours": 4 } ]""", 1, SectionFill.Incomplete)]
    // Правило на разделе — обязательный элемент: без ошибок — выполнен, с ошибкой при начатом разделе — часть.
    [InlineData("""{ "type": "object", "x-validation-rule": "hours_match_curriculum" }""", false, """{ "total_hours": 108 }""", 0, SectionFill.Complete)]
    [InlineData("""{ "type": "object", "x-validation-rule": "hours_match_curriculum" }""", false, """{ "total_hours": 108 }""", 1, SectionFill.Incomplete)]
    // Обязательного нет: без ошибок — серый; необязательный пустой объект ничего не требует.
    [InlineData("""{ "type": "object", "properties": {} }""", false, null, 0, SectionFill.NotApplicable)]
    [InlineData("""{ "type": "object", "required": ["number"] }""", false, null, 0, SectionFill.NotApplicable)]
    public void Section_fill_counts_required_elements(string schema, bool requiredAtRoot, string? value, int errors, string expected)
    {
        var section = JsonDocument.Parse(schema).RootElement;
        JsonElement? payload = value is null ? null : JsonDocument.Parse(value).RootElement;

        Assert.Equal(expected, SectionFill.Of(true, errors, SectionFill.Required(section, requiredAtRoot, payload)));
    }

    private static DisciplineFacts Facts(params DisciplineSemesterDetails[] semesters) => new(
        new Discipline { Name = "Дисциплина", Code = "Б1.О.99", Semesters = [.. semesters] },
        new CurriculumPlan { Year = 2026 }, new StudyDirection(), new StudyProfile(), null, []);
}
