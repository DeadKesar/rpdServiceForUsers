using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using RpdFos.Api.Tests.DocumentTypes;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;
using RpdFos.Core.DocumentTypes;
using RpdFos.Core.Printing;
using Xunit;

namespace RpdFos.Api.Tests.Documents;

/// <summary>
/// Печать ФОС по файлу оформления v2 (этап 7, B9), без базы: часть банка на приложение, билеты по числу, таблицы паспорта,
/// заглушки необязательных разделов, рисунки в заданиях и билетах. Содержимое — обезличенное.
/// </summary>
public class FosPrintingTests
{
    private static readonly DocumentTypeRegistry Registry = new();
    private static readonly Guid Scheme = Guid.Parse("11111111-2222-4333-8444-555555555555");

    private static string Img(string alt) => $"<img src=\"/api/images/{Scheme}\" width=\"60\" height=\"30\" alt=\"{alt}\">";

    // Дисциплина двух семестров: 1 — экзамен, 2 — зачёт. Банк: вопросы к экзамену, практические задания текущего контроля,
    // тесты зачёта по разделу 1; три билета.
    private static object Payload(bool withOptional = true) => new
    {
        title_page = new
        {
            department_name = "Информационных систем", discipline_name = "Моделирование систем", direction_code = "09.04.01",
            direction_name = "Информатика и вычислительная техника", profile_name = "Искусственный интеллект", study_form = "очная",
            education_level = "магистратуры", academic_year = "2026/2027"
        },
        linked_rpd = new
        {
            rpd_document_id = Guid.NewGuid(), rpd_version = 3,
            competences = new[]
            {
                new { code = "ПК-1", formulation = "Способен разрабатывать модели", indicators = new[] { new { code = "ПК-1.1", text = "Строит модель системы" } } },
                new { code = "ПК-2", formulation = "Способен оценивать качество", indicators = new[] { new { code = "ПК-2.1", text = "Оценивает адекватность" }, new { code = "ПК-2.2", text = "Сравнивает варианты" } } }
            },
            sections = new[]
            {
                new { number = 1, title = "Основы моделирования", semesters = new[] { 1 } },
                new { number = 2, title = "Имитационные модели", semesters = new[] { 2 } }
            },
            semesters = new[] { new { number = 1, control_types = new[] { "exam" } }, new { number = 2, control_types = new[] { "credit" } } }
        },
        competence_indicators = new[] { new { code = "ПК-1.1", text = "Строит модель системы" } },
        passport = new
        {
            sections = new object[]
            {
                new { section_number = 1, competence_codes = new[] { "ПК-1" }, tasks = new object[] { new { group = "Вопросы к экзамену", from = 1, to = 2 }, new { group = "Тесты раздела 1" } } },
                new { section_number = 2, section_title = "Своё название раздела", competence_codes = new[] { "ПК-1", "ПК-2" }, tasks = new object[] { new { group = "Практика", from = 1, to = 1 } } }
            },
            indicators = new object[]
            {
                new { competence_code = "ПК-1", indicator_code = "ПК-1.1", rpd_section_numbers = new[] { 1 }, tasks = new object[] { new { group = "Вопросы к экзамену", from = 1, to = 2 } } },
                new { competence_code = "ПК-2", indicator_code = "ПК-2.1", rpd_section_numbers = new[] { 1, 2 }, tasks = new object[] { new { group = "Тесты раздела 1", from = 1, to = 2 } } },
                new { competence_code = "ПК-2", indicator_code = "ПК-2.2", rpd_section_numbers = new[] { 2 }, tasks = new object[] { new { group = "Практика" } } }
            }
        },
        current_control = withOptional
            ? new { forms = new[] { "Контрольная работа", "Реферат" }, rating = "<p>Модуль 1 — 30 баллов</p>" }
            : null,
        evaluation_means = withOptional
            ? new[] { new { means_type = "essay", title = "Реферат", description = "<p>Тема по выбору.</p>", criteria = "<p>Полнота раскрытия темы.</p>", indicator_codes = new[] { "ПК-1.1" } } }
            : null,
        task_bank = new object[]
        {
            new
            {
                group = "Вопросы к экзамену", kind = "questions", control_form = "intermediate", semesters = new[] { 1 },
                tasks = new object[] { new { number = 1, text = "<p>Что такое модель?</p>" }, new { number = 2, text = "<p>Виды моделей.</p>" } }
            },
            new
            {
                group = "Практика", kind = "tasks", control_form = "current", title = "Практические задания по теме 1",
                tasks = new object[] { new { number = 1, text = $"<p>Постройте схему.</p><p>{Img("Схема цепи")}</p>" } }
            },
            new
            {
                group = "Тесты раздела 1", kind = "tests", control_form = "intermediate", section_number = 1, semesters = new[] { 2 },
                tasks = new object[] { new { number = 1, text = "<p>Вопрос теста</p><ol><li>Вариант а</li><li>Вариант б</li></ol>" }, new { number = 2, text = "<p>Второй тест</p>" } }
            }
        },
        exam_tickets = withOptional
            ? new object[]
            {
                new { number = 1, questions = new[] { "<p>Модель и её свойства</p>", $"<p>Задача: рисунок {Img("Рисунок билета")}</p>" } },
                new { number = 2, questions = new[] { "<p>Классификация моделей</p>", "<p>Метод Монте-Карло</p>" } },
                new { number = 3, questions = new[] { "<p>Адекватность</p>", "<p>Верификация</p>" } }
            }
            : null,
        approval_sheet = new
        {
            developers = withOptional ? new[] { new { name_for_document = "И. И. Иванов", position = "доцент" } } : []
        }
    };

    private static (WordprocessingDocument Document, DocxResult Result) Print(object payload)
    {
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        var definition = Registry.Get(DocumentTypeRegistry.Fos);
        var schema = JsonDocument.Parse(definition.SchemaJson).RootElement;
        var facts = new DisciplineFacts(
            new Discipline
            {
                Name = "Моделирование систем",
                Semesters = [new DisciplineSemesterDetails { Semester = 1, Lectures = 16, ControlTypes = ["exam"] }, new DisciplineSemesterDetails { Semester = 2, Lectures = 16, ControlTypes = ["credit"] }]
            },
            new CurriculumPlan(), new StudyDirection(), new StudyProfile(), null, []);
        var images = new Dictionary<Guid, ImageInfo> { [Scheme] = ImageFiles.Inspect(TestImages.Png(200, 100, seed: 1)).Image! };
        var context = new PrintContext(JsonDocument.Parse(json).RootElement, schema, Registry.FindSchema, facts, new SignatoryReport([], []), 2026, [], images);

        var result = DocxGenerator.Generate(DocxBookmarksTests.Template(DocxBookmarksTests.Fos), definition.PrintMapping, context);
        var docx = WordprocessingDocument.Open(new MemoryStream(result.Content), false);
        Assert.Empty(new OpenXmlValidator(DocumentFormat.OpenXml.FileFormatVersions.Office2019).Validate(docx).Select(e => $"{e.Path?.XPath}: {e.Description}"));
        return (docx, result);
    }

    private static List<string> Paragraphs(WordprocessingDocument docx) =>
        docx.MainDocumentPart!.Document!.Body!.Descendants<Paragraph>().Select(p => p.InnerText.Trim()).ToList();

    [Fact]
    public void Full_fos_has_no_stubs_left_and_prints_tickets_and_parts()
    {
        var (docx, result) = Print(Payload());
        var text = docx.MainDocumentPart!.Document!.Body!.InnerText;
        var paragraphs = Paragraphs(docx);

        Assert.DoesNotContain(paragraphs, p => p.Contains("Autofill"));
        // Билеты — по числу, каждый со своим номером, шапкой и подписью заведующего; вопросы пронумерованы.
        Assert.Equal(["ЭКЗАМЕНАЦИОННЫЙ БИЛЕТ № 1", "ЭКЗАМЕНАЦИОННЫЙ БИЛЕТ № 2", "ЭКЗАМЕНАЦИОННЫЙ БИЛЕТ № 3"],
            paragraphs.Where(p => p.StartsWith("ЭКЗАМЕНАЦИОННЫЙ БИЛЕТ")));
        Assert.Equal(3, paragraphs.Count(p => p.StartsWith("2026/2027") && p.EndsWith("учебный год")));
        Assert.Equal(3, paragraphs.Count(p => p.StartsWith("Зав. кафедрой")));
        Assert.Contains("1. Модель и её свойства", paragraphs);
        Assert.Contains("2. Метод Монте-Карло", paragraphs);
        // Три части банка — три приложения со своими заголовками (вид аттестации — из снимка РПД).
        Assert.Equal(
            ["Вопросы для подготовки к экзамену", "Комплект практических заданий, используемых при проведении текущего контроля",
                "Комплект тестовых заданий, используемых при проведении зачёта"],
            paragraphs.Where(p => p.StartsWith("Вопросы для подготовки") || p.StartsWith("Комплект")));
        // Номера заданий: у теста — «раздел.номер».
        Assert.Contains("1. Что такое модель?", paragraphs);
        Assert.Contains("1.1. Вопрос теста", paragraphs);
        Assert.Contains("1.2. Второй тест", paragraphs);
        Assert.Contains("Практические задания по теме 1", paragraphs);
        // Составитель — строкой с должностью и ФИО.
        Assert.Contains(paragraphs, p => p.StartsWith("Составитель доцент") && p.EndsWith("И. И. Иванов"));
        Assert.DoesNotContain(result.Report.Warnings, w => w.Contains("не сопоставлена"));
    }

    [Fact]
    public void Passport_tables_count_tasks_by_bank_and_merge_competences()
    {
        var (docx, _) = Print(Payload());
        var tables = docx.MainDocumentPart!.Document!.Body!.Elements<Table>().ToList();
        var first = tables[0].Elements<TableRow>().Select(r => r.Elements<TableCell>().Select(c => c.InnerText.Trim()).ToList()).ToList();
        var second = tables[1].Elements<TableRow>().Select(r => r.Elements<TableCell>().Select(c => c.InnerText.Trim()).ToList()).ToList();

        // Две строки шапки и два раздела; числа — по банку: вопросов 2 (диапазон 1–2), тестов 2 (вся группа).
        Assert.Equal(4, first.Count);
        Assert.Equal(["1", "Основы моделирования", "ПК-1", "2", "0", "2"], first[2]);
        Assert.Equal(["2", "Своё название раздела", "ПК-1, ПК-2", "0", "1", "0"], first[3]);
        // Шапка и три индикатора; компетенция ПК-2 — в одной объединённой ячейке на два индикатора.
        Assert.Equal(4, second.Count);
        Assert.Equal("ПК-1. Способен разрабатывать модели", second[1][0]);
        Assert.Equal("ПК-1.1. Строит модель системы", second[1][1]);
        Assert.Contains("Вопросы к экзамену: 1–2", second[1][3]);
        Assert.Equal(["ПК-2. Способен оценивать качество", ""], [second[2][0], second[3][0]]);
        Assert.Contains("Тесты раздела 1: 1.1–1.2", second[2][3]);
        Assert.Equal("Практика", second[3][3]);
        var merge = tables[1].Elements<TableRow>().Skip(2).Select(r => r.Elements<TableCell>().First().TableCellProperties!.VerticalMerge!.Val?.Value).ToList();
        Assert.Equal([MergedCellValues.Restart, MergedCellValues.Continue], merge);
    }

    [Fact]
    public void Optional_sections_print_stubs_and_typical_criteria_and_tickets_are_removed()
    {
        var (docx, result) = Print(Payload(withOptional: false));
        var paragraphs = Paragraphs(docx);
        var text = docx.MainDocumentPart!.Document!.Body!.InnerText;

        Assert.DoesNotContain("Autofill", text);
        Assert.DoesNotContain("ЭКЗАМЕНАЦИОННЫЙ БИЛЕТ", text);
        Assert.Contains("Мероприятия текущего контроля в фонде оценочных средств не предусмотрены.", paragraphs);
        Assert.Contains("Оценочные средства, кроме заданий банка, в фонде не предусмотрены.", paragraphs);
        // Шкала пуста, в РПД есть экзамен — типовые критерии пятибалльной шкалы таблицей.
        var scale = docx.MainDocumentPart!.Document!.Body!.Elements<Table>().Last();
        Assert.Equal(["Оценка", "Отлично", "Хорошо", "Удовлетворительно", "Неудовлетворительно"],
            scale.Elements<TableRow>().Select(r => r.Elements<TableCell>().First().InnerText.Trim()));
        // Составителей нет — бланк шаблона остаётся для ручного заполнения.
        Assert.Contains(paragraphs, p => p.StartsWith("Составитель"));
        Assert.Contains(result.Report.Unfilled, u => u.Contains("AutofillBankCompilers"));
    }

    [Fact]
    public void Document_without_bank_has_no_bank_sections_and_still_ends_with_scale()
    {
        var (docx, _) = Print(new { title_page = new { discipline_name = "Моделирование систем" }, task_bank = Array.Empty<object>() });
        var text = docx.MainDocumentPart!.Document!.Body!.InnerText;

        Assert.DoesNotContain("Комплект", text);
        Assert.DoesNotContain("Вопросы для подготовки", text);
        Assert.Contains("Критерии формирования оценок", text);
        Assert.DoesNotContain("Autofill", text);
    }

    [Fact]
    public void Pictures_in_task_and_ticket_question_are_printed_with_their_own_numbers()
    {
        var (docx, _) = Print(Payload());
        var inlines = docx.MainDocumentPart!.Document!.Body!.Descendants<DW.Inline>().ToList();

        // Эмблема на титуле, в трёх частях банка и трёх билетах, рисунок задания и рисунок вопроса билета №1.
        Assert.Equal(9, inlines.Count);
        Assert.Equal(inlines.Count, inlines.Select(i => i.GetFirstChild<DW.DocProperties>()!.Id!.Value).Distinct().Count());
        Assert.Contains(inlines, i => i.GetFirstChild<DW.DocProperties>()!.Description?.Value == "Схема цепи");
        Assert.Contains(inlines, i => i.GetFirstChild<DW.DocProperties>()!.Description?.Value == "Рисунок билета");
    }

    [Fact]
    public void Block_mapping_targets_exist_in_schema()
    {
        var definition = Registry.Get(DocumentTypeRegistry.Fos);
        var errors = PrintMappingValidator.Validate(Registry, DocumentTypeRegistry.Fos, definition.PrintMapping,
            PrintMappingJson.ReadSignatures(definition.SignaturesJson), DocxBookmarks.Read(DocxBookmarksTests.Template(DocxBookmarksTests.Fos)).Select(g => g.BaseName));

        Assert.Empty(errors);
        Assert.Equal((BookmarkTargetKinds.Block, BlockModes.Repeat), (definition.PrintMapping["AutofillTicket"].Kind, definition.PrintMapping["AutofillTicket"].Part));
        Assert.Equal(BlockModes.Present, definition.PrintMapping["AutofillExaming"].Part);
    }

    [Fact]
    public void File_name_of_fos_starts_with_fos_and_carries_academic_year()
    {
        var document = new RpdFos.Core.Data.Entities.Document { Type = TemplateTypes.Fos, Payload = """{ "title_page": { "academic_year": "2026/2027" } }""", CreatedAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero) };

        Assert.Equal("ФОС_Моделирование систем_2026-2027.pdf", DocumentPrinting.FileName(document, "Моделирование систем", DocumentPrinting.Period(document), "pdf"));
    }
}
