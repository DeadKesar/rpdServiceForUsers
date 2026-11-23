using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using RpdFos.Api.Tests.DocumentTypes;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;
using RpdFos.Core.DocumentTypes;
using RpdFos.Core.Printing;
using Xunit;

namespace RpdFos.Api.Tests.Documents;

/// <summary>
/// Печать № занятия текстом в таблицах практических занятий и лабораторных работ (этап 7.1): «1–2», «4, 5» — как введено;
/// число из документа по прежнему шаблону печатается как раньше. Без базы: настоящая схема, сопоставление и файл шаблона v3.
/// Неделя семестра в таблице «Разделы дисциплины» — в <see cref="PartitionPrintingTests"/>.
/// </summary>
public class WeekAndClassNumberPrintingTests
{
    private static readonly DocumentTypeRegistry Registry = new();

    // В плане есть практические и лабораторные занятия: таблицы печатаются, а не заменяются заглушкой.
    private static readonly DisciplineFacts Facts = new(
        new Discipline
        {
            Name = "Дисциплина",
            Semesters = [new DisciplineSemesterDetails { Semester = 1, Lectures = 16, Practice = 16, LabHours = 16, ControlTypes = ["exam"] }]
        },
        new CurriculumPlan(), new StudyDirection(), new StudyProfile(), null, []);

    private static Body Print(string payloadJson)
    {
        var rpd = Registry.Get(DocumentTypeRegistry.Rpd);
        var payload = JsonDocument.Parse(payloadJson).RootElement;
        var context = new PrintContext(payload, rpd.Schema, Registry.FindSchema, Facts, new SignatoryReport([], []), 2026,
            DocumentMetadata.Hidden(rpd.Schema, DocumentSemesters.Of(payload, Facts), Registry.FindSchema));
        var result = DocxGenerator.Generate(DocxBookmarksTests.Template(DocxBookmarksTests.Rpd), rpd.PrintMapping, context);

        var document = WordprocessingDocument.Open(new MemoryStream(result.Content), false);
        Assert.Empty(new OpenXmlValidator(FileFormatVersions.Office2019).Validate(document).Select(e => $"{e.Path?.XPath}: {e.Description}"));
        return document.MainDocumentPart!.Document!.Body!;
    }

    // Строки данных таблицы занятий — после шапки; в последней — «Итого:».
    private static List<List<string>> Rows(Body body, string title)
    {
        var table = body.Descendants<Table>().Single(t => t.InnerText.Contains(title) && t.InnerText.Contains("Итого:"));
        return table.Elements<TableRow>().Skip(1).Select(r => r.Elements<TableCell>().Select(c => c.InnerText).ToList()).ToList();
    }

    [Fact]
    public void Class_numbers_are_printed_as_entered_in_both_tables()
    {
        var body = Print("""
            { "practical_classes": [
                { "number": "1–2", "title": "Построение модели", "hours": 8, "section_number": 1, "control_form": "Отчёт" },
                { "number": "3", "title": "Анализ модели", "hours": 8, "section_number": 2 } ],
              "lab_works": [
                { "number": "4, 5", "title": "Исследование модели", "hours": 10 },
                { "number": "6-7", "title": "Оптимизация модели", "hours": 6 } ] }
            """);

        // № | раздел | тема | часы | форма контроля
        Assert.Equal(
            [
                ["1–2", "1", "Построение модели", "8", "Отчёт"],
                ["3", "2", "Анализ модели", "8", ""],
                ["", "", "Итого:", "16", ""]
            ],
            Rows(body, "Построение модели"));
        Assert.Equal(
            [
                ["4, 5", "", "Исследование модели", "10", ""],
                ["6-7", "", "Оптимизация модели", "6", ""],
                ["", "", "Итого:", "16", ""]
            ],
            Rows(body, "Исследование модели"));
    }

    [Fact]
    public void Integer_number_of_a_document_by_a_former_template_is_printed_as_before()
    {
        var body = Print("""
            { "lab_works": [ { "number": 1, "title": "Исследование модели", "hours": 8 }, { "number": 2, "title": "Оптимизация модели", "hours": 8 } ] }
            """);

        Assert.Equal(["1", "2", ""], Rows(body, "Исследование модели").Select(r => r[0]));
    }
}
