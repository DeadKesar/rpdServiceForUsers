using System.Text.Json;
using RpdFos.Core.Documents;
using RpdFos.Core.DocumentTypes;
using Xunit;

namespace RpdFos.Api.Tests.Documents;

/// <summary>Проверка содержимого документа по схеме шаблона (FR-VAL-01) в режимах черновика и отправки.</summary>
public class DocumentValidatorTests
{
    private static readonly DocumentTypeRegistry Registry = new();
    private static readonly DocumentValidator Validator = new(Registry);
    private static readonly string Rpd = Registry.Get(DocumentTypeRegistry.Rpd).SchemaJson;

    // Ошибки типов и форматов вместе с незаполненными обязательными полями.
    private const string Broken = """
        {
          "title_page": { "discipline_name": "Моделирование", "direction_code": "09.04", "academic_year": "2026" },
          "goals_and_objectives": { "goals": 123 },
          "hours_distribution": { "semesters": [ { "semester": 13, "lectures": -2, "control_types": ["zachet"] } ] },
          "competences": [ { "code": "ОПК-1", "indicators": [ { "code": "ОПК-1.1" } ] } ],
          "literature": { "internet_resources": [ { "title": "Сайт", "url": "не ссылка", "accessed_on": "31.12.2026" } ] }
        }
        """;

    [Fact]
    public void Draft_blocks_type_and_format_errors_but_not_missing_fields()
    {
        var errors = Validate(Rpd, Broken, ValidationMode.Draft);

        Assert.Equal(
        [
            ("/goals_and_objectives/goals", "Ожидается текст"),
            ("/hours_distribution/semesters/0/control_types/0", "Значение не из списка допустимых"),
            ("/hours_distribution/semesters/0/lectures", "Значение должно быть не меньше 0"),
            ("/hours_distribution/semesters/0/semester", "Значение должно быть не больше 12"),
            ("/literature/internet_resources/0/accessed_on", "Ожидается дата в формате ГГГГ-ММ-ДД"),
            ("/literature/internet_resources/0/url", "Ожидается адрес ссылки, например https://…"),
            ("/title_page/academic_year", "Неверный формат. Учебный год, например 2026/2027"),
            ("/title_page/direction_code", "Неверный формат")
        ], errors.Select(e => (e.Path, e.Message)));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "goals_and_objectives": { "goals": "Коротко", "objectives": [] } }""")]
    public void Unfilled_draft_is_saved(string payload) =>
        // Черновик сохраняется незаполненным: обязательность и полнота (minLength, minItems) не проверяются.
        Assert.Empty(Validate(Rpd, payload, ValidationMode.Draft));

    [Fact]
    public void Submit_also_requires_mandatory_and_complete_fields()
    {
        var errors = Validate(Rpd, Broken.Replace("\"goals\": 123", "\"goals\": \"Коротко\""), ValidationMode.Submit);

        // Обязательное поле — по месту самого поля, а не объекта, в котором его нет.
        Assert.Contains(new DocumentValidationError("/title_page/study_form", "Обязательное поле не заполнено"), errors);
        Assert.Contains(new DocumentValidationError("/competences/0/formulation", "Обязательное поле не заполнено"), errors);
        Assert.Contains(new DocumentValidationError("/thematic_plan", "Обязательное поле не заполнено"), errors);
        Assert.Contains(new DocumentValidationError("/goals_and_objectives/goals",
            "Слишком короткий текст: нужно не меньше 50 символов"), errors);
        // Ошибки форматов — и при отправке.
        Assert.Contains(errors, e => e.Path == "/title_page/direction_code");
    }

    [Theory]
    [InlineData(DocumentStatuses.UnderReview)]
    [InlineData(DocumentStatuses.Approval, "/approval_sheet/department_head", "/approval_sheet/methodist")]
    [InlineData(DocumentStatuses.Approved, "/approval_sheet/approver", "/approval_sheet/department_head", "/approval_sheet/methodist")]
    public void Signatures_are_required_from_their_status_on(string target, params string[] expected)
    {
        // x-required-for-status: подписи заведующего и методиста — к согласованию, утверждающего — к утверждению.
        var errors = Validate(Rpd, """{ "approval_sheet": { "developer": {} } }""", ValidationMode.Submit, target);

        Assert.Equal(expected, errors.Where(e => e.Message.StartsWith("Обязательно для перехода")).Select(e => e.Path));
    }

    [Fact]
    public void Correct_values_pass()
    {
        var payload = """
            {
              "title_page": { "direction_code": "09.04.04", "academic_year": "2026/2027" },
              "hours_distribution": { "semesters": [ { "semester": 2, "lectures": 16, "control_types": ["exam", "course_project"] } ] },
              "competences": [ { "code": "ПК-1", "indicators": [ { "code": "ПК-1.1.1" } ] } ],
              "literature": { "internet_resources": [ { "title": "ЭБС", "url": "https://e.lanbook.com/book/1", "accessed_on": "2026-12-31" } ] }
            }
            """;

        Assert.Empty(Validate(Rpd, payload, ValidationMode.Draft));
    }

    [Fact]
    public void Fos_title_page_is_checked_through_reference_to_rpd()
    {
        // Титул ФОС — $ref на схему РПД: ссылку разрешает реестр типов.
        var errors = Validate(Registry.Get(DocumentTypeRegistry.Fos).SchemaJson,
            """{ "title_page": { "direction_code": "09.04" } }""", ValidationMode.Draft);

        Assert.Equal([new DocumentValidationError("/title_page/direction_code", "Неверный формат")], errors);
    }

    [Theory]
    [InlineData(DocumentTypeRegistry.Rpd)]
    [InlineData(DocumentTypeRegistry.Fos)]
    public void Every_assertion_keyword_of_schema_has_message(string type)
    {
        // Валидатор сообщает только об известных утверждениях: новое ключевое слово в схеме — расширить список.
        string[] validation =
        [
            "type", "enum", "const", "pattern", "format", "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum",
            "multipleOf", "minLength", "maxLength", "minItems", "maxItems", "uniqueItems", "minContains", "maxContains",
            "required", "minProperties", "maxProperties", "dependentRequired", "additionalProperties",
            "unevaluatedProperties", "unevaluatedItems", "not", "oneOf", "anyOf", "if", "contains", "propertyNames"
        ];
        var used = new HashSet<string>();
        Collect(Registry.Get(type).Schema, names: false);

        Assert.Contains("required", used);
        Assert.All(used, keyword => Assert.Contains(keyword, DocumentValidator.Assertions));

        void Collect(JsonElement node, bool names)
        {
            if (node.ValueKind == JsonValueKind.Array)
                foreach (var item in node.EnumerateArray()) Collect(item, false);
            if (node.ValueKind != JsonValueKind.Object) return;
            foreach (var p in node.EnumerateObject())
            {
                // Ключи внутри properties и $defs — имена полей и определений, а не ключевые слова.
                if (!names && validation.Contains(p.Name)) used.Add(p.Name);
                Collect(p.Value, !names && p.Name is "properties" or "$defs" or "patternProperties" or "dependentSchemas");
            }
        }
    }

    private static IReadOnlyList<DocumentValidationError> Validate(string schema, string payload, ValidationMode mode,
        string target = DocumentStatuses.UnderReview) =>
        Validator.Validate(schema, JsonDocument.Parse(payload).RootElement, mode, target);
}
