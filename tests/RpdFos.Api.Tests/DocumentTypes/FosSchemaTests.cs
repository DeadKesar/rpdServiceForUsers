using System.Text.Json;
using RpdFos.Core.Documents;
using RpdFos.Core.DocumentTypes;
using Xunit;

namespace RpdFos.Api.Tests.DocumentTypes;

/// <summary>
/// Схема ФОС по ответам автора на разбор настоящих ФОС (этап 7, B2; П1–П14): средства, ссылки паспорта, группы банка,
/// билеты, критерии, снимок РПД. Проверка — через валидатор документов, как при сохранении и отправке.
/// </summary>
public class FosSchemaTests
{
    private static readonly DocumentTypeRegistry Registry = new();
    private static readonly DocumentValidator Validator = new(Registry);
    private static readonly string Schema = Registry.Get(DocumentTypeRegistry.Fos).SchemaJson;

    // Разделы, которые меняла правка схемы; остальные (титул, лист согласования) здесь не заполняются.
    private const string Good = """
        {
          "linked_rpd": {
            "rpd_document_id": "7f0c1a5e-3b3f-4a64-9d0e-6a1f2b6c7d10", "rpd_version": 3, "snapshot_at": "2026-11-21T10:00:00+03:00",
            "competences": [ { "code": "УК-1", "formulation": "Способен искать и анализировать информацию",
                               "indicators": [ { "code": "УК-1.1", "text": "Анализирует задачу" } ] } ],
            "sections": [ { "number": 1, "title": "Введение, основные понятия", "semesters": [1, 2], "competence_codes": ["УК-1"],
                            "indicator_codes": ["УК-1.1"], "current_control_forms": ["Контрольная работа"] } ],
            "semesters": [ { "number": 1, "control_types": ["credit"] }, { "number": 2, "control_types": ["graded_credit", "course_work"] } ]
          },
          "passport": {
            "sections": [ { "section_number": 1, "section_title": "Введение, основные понятия", "competence_codes": ["УК-1"],
                            "tasks": [ { "group": "Вопросы к зачёту", "from": 1, "to": 20 }, { "group": "Тест 1" } ] } ],
            "indicators": [ { "competence_code": "УК-1", "indicator_code": "УК-1.1", "rpd_section_numbers": [1],
                              "tasks": [ { "group": "Вопросы к зачёту", "from": 5, "to": 5 } ] } ]
          },
          "evaluation_means": [ { "means_type": "essay", "title": "Реферат", "description": "<p>Структура и оформление</p>",
                                  "criteria": "<p>Критерии оценок</p>", "indicator_codes": ["УК-1.1"] } ],
          "task_bank": [
            { "group": "Вопросы к зачёту", "kind": "questions", "control_form": "intermediate", "semesters": [1, 2],
              "title": "по разделу «Введение» (1–2 семестр)", "tasks": [ { "number": 1, "text": "<p>Что такое модель?</p>" } ] },
            { "group": "Тест 1", "kind": "tests", "section_number": 1, "control_form": "current",
              "tasks": [ { "number": 1, "text": "<p>Вопрос</p><ol><li>а</li><li>б</li></ol>", "expected_answer": "а" } ] }
          ],
          "exam_tickets": [ { "number": 1, "questions": ["Первый вопрос", "Второй вопрос", "Третий вопрос", "Четвёртый вопрос"] } ],
          "evaluation_scale": { "type": "five_point", "criteria": [ { "grade": "Отлично", "description": "Знает, умеет, владеет" } ] }
        }
        """;

    // Разделы, которые заполняет этот тест: остальные корневые обязательные поля (титул, индикаторы) в примере не нужны.
    private static readonly string[] Sections =
        ["/linked_rpd", "/passport", "/evaluation_means", "/task_bank", "/exam_tickets", "/evaluation_scale"];

    private static IEnumerable<(string Path, string Message)> Errors(string payload, ValidationMode mode = ValidationMode.Submit) =>
        Validator.Validate(Schema, JsonDocument.Parse(payload).RootElement, mode)
            .Where(e => Sections.Any(s => e.Path.StartsWith(s, StringComparison.Ordinal)))
            .Select(e => (e.Path, e.Message));

    [Theory]
    [InlineData(ValidationMode.Draft)]
    [InlineData(ValidationMode.Submit)]
    public void Payload_in_the_new_shape_has_no_errors(ValidationMode mode) =>
        // В том числе группа вопросов без раздела: условие «у теста раздел обязателен» не должно давать ошибок вне тестов.
        Assert.Empty(Errors(Good, mode));

    [Fact]
    public void Passport_reference_is_group_with_optional_range()
    {
        // Без from и to — вся группа; один номер — from равен to; 0 и меньше — не номер.
        Assert.Contains(("/passport/sections/0/tasks/0/from", "Значение должно быть не меньше 1"),
            Errors(Good.Replace("\"from\": 1, \"to\": 20", "\"from\": 0, \"to\": 20")));
        Assert.Contains(("/passport/indicators/0/tasks/0/group", "Обязательное поле не заполнено"),
            Errors(Good.Replace("{ \"group\": \"Вопросы к зачёту\", \"from\": 5, \"to\": 5 }", "{ \"from\": 5, \"to\": 5 }")));
    }

    [Fact]
    public void Evaluation_means_is_optional_and_a_record_has_type_and_title()
    {
        var withoutMeans = Good[..Good.IndexOf("\"evaluation_means\"", StringComparison.Ordinal)] +
                           Good[Good.IndexOf("\"task_bank\"", StringComparison.Ordinal)..];

        Assert.Empty(Errors(withoutMeans));
        Assert.Contains(("/evaluation_means/0/title", "Обязательное поле не заполнено"),
            Errors(Good.Replace("\"title\": \"Реферат\", ", "")));
        // Описание и критерии необязательны для схемы: их отсутствие — предупреждение правила, а не ошибка структуры.
        Assert.Empty(Errors(Good.Replace("\"description\": \"<p>Структура и оформление</p>\",", "").Replace("\"criteria\": \"<p>Критерии оценок</p>\",", "")));
    }

    [Fact]
    public void Bank_group_needs_kind_and_form_and_tests_need_section()
    {
        // Тест без раздела: номер задания не напечатать как «раздел.номер» (П14).
        Assert.Contains(("/task_bank/1/section_number", "Обязательное поле не заполнено"),
            Errors(Good.Replace("\"kind\": \"tests\", \"section_number\": 1,", "\"kind\": \"tests\",")));
        // В черновике полнота не проверяется.
        Assert.Empty(Errors(Good.Replace("\"kind\": \"tests\", \"section_number\": 1,", "\"kind\": \"tests\","), ValidationMode.Draft));
        Assert.Contains(("/task_bank/0/kind", "Значение не из списка допустимых"),
            Errors(Good.Replace("\"kind\": \"questions\"", "\"kind\": \"essays\"")));
        Assert.Contains(("/task_bank/0/control_form", "Обязательное поле не заполнено"),
            Errors(Good.Replace("\"control_form\": \"intermediate\", ", "")));
        Assert.Contains(("/task_bank/0/semesters/0", "Значение должно быть не больше 12"),
            Errors(Good.Replace("\"control_form\": \"intermediate\", \"semesters\": [1, 2],", "\"control_form\": \"intermediate\", \"semesters\": [13, 2],")));
    }

    [Fact]
    public void Ticket_has_any_number_of_questions_from_two()
    {
        Assert.Empty(Errors(Good));
        Assert.Contains(("/exam_tickets/0/questions", "Нужно не меньше 2 элементов"),
            Errors(Good.Replace("[\"Первый вопрос\", \"Второй вопрос\", \"Третий вопрос\", \"Четвёртый вопрос\"]", "[\"Первый вопрос\"]")));
        // Черновик билет с одним вопросом сохраняет (минимум проверяет отправка), поэтому вопросы не обрезаются при правке.
        Assert.Empty(Errors(Good.Replace("[\"Первый вопрос\", \"Второй вопрос\", \"Третий вопрос\", \"Четвёртый вопрос\"]", "[\"Первый вопрос\"]"), ValidationMode.Draft));
    }

    [Fact]
    public void Scale_criteria_do_not_need_scores()
    {
        // В критериях Положения баллов нет (П13); оценка и описание остаются обязательными.
        Assert.Empty(Errors(Good));
        Assert.Contains(("/evaluation_scale/criteria/0/description", "Обязательное поле не заполнено"),
            Errors(Good.Replace(", \"description\": \"Знает, умеет, владеет\"", "")));
        Assert.Empty(Errors(Good.Replace("\"grade\": \"Отлично\"", "\"grade\": \"Отлично\", \"min_score\": 85, \"max_score\": 100")));
    }

    [Fact]
    public void Rpd_snapshot_is_checked_for_codes_and_attestation_kinds()
    {
        Assert.Contains(("/linked_rpd/competences/0/indicators/0/code", "Неверный формат"),
            Errors(Good.Replace("\"code\": \"УК-1.1\"", "\"code\": \"ОК-1.1\"")));
        Assert.Contains(("/linked_rpd/semesters/0/control_types/0", "Значение не из списка допустимых"),
            Errors(Good.Replace("[\"credit\"]", "[\"zachet\"]")));
        Assert.Contains(("/linked_rpd/sections/0/number", "Значение должно быть не меньше 1"),
            Errors(Good.Replace("\"number\": 1, \"title\"", "\"number\": 0, \"title\"")));
        // Коды ОК и ПСК ФГОС 3+ не принимаются (П5): схемы ФОС и РПД ведутся по ФГОС 3++.
        Assert.Contains(("/passport/sections/0/competence_codes/0", "Неверный формат"),
            Errors(Good.Replace("\"section_title\": \"Введение, основные понятия\", \"competence_codes\": [\"УК-1\"]",
                "\"section_title\": \"Введение, основные понятия\", \"competence_codes\": [\"ОК-1\"]")));
    }

    [Fact]
    public void Snapshot_attestation_kinds_equal_those_of_the_rpd()
    {
        // Снимок копирует виды аттестации РПД как есть: расхождение списков сломало бы обновление снимка.
        static string[] Kinds(JsonElement schema, string pointer) =>
            SchemaInspector.SubschemaAt(schema, pointer, _ => null)!.Value.GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).Order().ToArray();

        var rpd = Registry.Get(DocumentTypeRegistry.Rpd).Schema;
        var fos = Registry.Get(DocumentTypeRegistry.Fos).Schema;

        Assert.Equal(Kinds(rpd, "/hours_distribution/semesters/0/control_types/0"),
            Kinds(fos, "/linked_rpd/semesters/0/control_types/0"));
    }

    [Fact]
    public void Snapshot_competence_fields_are_those_of_the_rpd()
    {
        // Компетенции в снимке — код, формулировка, индикаторы (код, текст): подмножество полей РПД, без «знать / уметь / владеть».
        static string[] Names(JsonElement schema, string pointer) =>
            SchemaInspector.SubschemaAt(schema, pointer, _ => null)!.Value.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order().ToArray();

        var rpd = Registry.Get(DocumentTypeRegistry.Rpd).Schema;
        var fos = Registry.Get(DocumentTypeRegistry.Fos).Schema;

        Assert.Subset(Names(rpd, "/competences/0").ToHashSet(), Names(fos, "/linked_rpd/competences/0").ToHashSet());
        Assert.Subset(Names(rpd, "/competences/0/indicators/0").ToHashSet(), Names(fos, "/linked_rpd/competences/0/indicators/0").ToHashSet());
        Assert.DoesNotContain("results", Names(fos, "/linked_rpd/competences/0/indicators/0"));
    }

    // Поля без title общая форма подписывает именем свойства по-английски («module», «min_score»): так выглядели три раздела
    // ФОС на точке проверки B7. Свои виджеты (паспорт, банк, средства, билеты, связь с РПД) подписи рисуют сами.
    private static IEnumerable<string> Untitled(JsonElement schema, string path)
    {
        if (schema.TryGetProperty("properties", out var properties))
            foreach (var property in properties.EnumerateObject())
            {
                var here = $"{path}/{property.Name}";
                if (!property.Value.TryGetProperty("title", out _)) yield return here;
                foreach (var inner in Untitled(property.Value, here)) yield return inner;
            }
        if (schema.TryGetProperty("items", out var items))
            foreach (var inner in Untitled(items, $"{path}/*")) yield return inner;
    }

    [Theory]
    [InlineData("competence_indicators")]
    [InlineData("current_control")]
    [InlineData("evaluation_scale")]
    public void Sections_drawn_by_the_generic_form_have_a_title_on_every_field(string section) =>
        Assert.Empty(Untitled(Registry.Get(DocumentTypeRegistry.Fos).Schema.GetProperty("properties").GetProperty(section), $"/{section}"));

    [Fact]
    public void Every_scale_type_has_a_russian_title()
    {
        var type = Registry.Get(DocumentTypeRegistry.Fos).Schema.GetProperty("properties").GetProperty("evaluation_scale")
            .GetProperty("properties").GetProperty("type");
        var titles = type.GetProperty("x-enum-titles");

        Assert.All(type.GetProperty("enum").EnumerateArray().Select(v => v.GetString()!), kind =>
            Assert.Matches("[А-Яа-я]", titles.GetProperty(kind).GetString()!));
    }
}
