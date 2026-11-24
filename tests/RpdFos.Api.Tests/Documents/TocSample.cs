using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Wordprocessing;
using RpdFos.Api.Tests.DocumentTypes;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;
using RpdFos.Core.DocumentTypes;
using RpdFos.Core.Printing;

namespace RpdFos.Api.Tests.Documents;

/// <summary>
/// РПД для проверок оглавления (этап 8, A13): объём — около десяти страниц, чтобы заголовки попадали на разные; лабораторные, практика,
/// курсовой проект и раздел 6 — по признакам. Настоящая схема и сопоставление, без базы.
/// </summary>
internal static class TocSample
{
    private static readonly DocumentTypeRegistry Registry = new();

    public const string Section6 = "Особенности освоения дисциплины";

    /// <summary>Строки оглавления без номеров страниц: табуляция отделяет номер раздела уровня 1.</summary>
    public static List<string> Expected(bool section6)
    {
        List<string> lines =
        [
            "1.\tЦели и задачи освоения дисциплины",
            "2.\tМесто дисциплины в структуре образовательной программы",
            "2.1. Требования к входным результатам обучения, необходимым для освоения дисциплины",
            "2.2. Требования к результатам обучения по дисциплине",
            "3.\tСтруктура и содержание дисциплины",
            "3.1. Структура дисциплины",
            "3.2. Содержание разделов дисциплины",
            "3.3. Лабораторные работы",
            "3.4. Практические занятия (семинары)",
            "3.5. Курсовой проект (курсовая работа)",
            "4.\tУчебно-методическое обеспечение дисциплины",
            "4.1. Литература",
            "4.2. Периодические издания",
            "4.3. Интернет-ресурсы",
            "4.4. Методические указания к лабораторным занятиям",
            "4.5. Методические указания к практическим занятиям (семинарам)",
            "4.6. Методические указания к курсовому проектированию и другим видам самостоятельной работы",
            "4.7. Программное обеспечение, профессиональные базы данных и информационные справочные системы современных информационных технологий",
            "5.\tМатериально-техническое обеспечение дисциплины"
        ];
        if (section6) lines.Add("6.\t" + Section6);
        return lines;
    }

    /// <param name="lastLines">Сколько строк добавить в конец раздела 6: от них зависит, где на странице он кончается.</param>
    public static DocxResult Print(bool labs, bool practice, bool courseProject, bool section6, byte[]? template = null,
        IReadOnlyDictionary<string, BookmarkTarget>? mapping = null, int lastLines = 0)
    {
        var rpd = Registry.Get(DocumentTypeRegistry.Rpd);
        var payload = JsonDocument.Parse(Payload(labs, practice, courseProject, section6, lastLines).ToJsonString()).RootElement;
        var facts = new DisciplineFacts(
            new Discipline
            {
                Name = "Моделирование систем", Code = "Б1.О.10", TotalCredits = 4, TotalHours = 144,
                Semesters =
                [
                    new DisciplineSemesterDetails
                    {
                        Semester = 1, Lectures = 16, LabHours = labs ? 20 : 0, Practice = practice ? 60 : 0, Control = 36, SelfStudy = 24,
                        ContactHours = 16 + (labs ? 20 : 0) + (practice ? 60 : 0),
                        ControlTypes = courseProject ? ["exam", "course_project"] : ["exam"]
                    }
                ]
            },
            new CurriculumPlan { Year = 2026 }, new StudyDirection { EducationLevel = EducationLevels.Master }, new StudyProfile(), null, []);
        var context = new PrintContext(payload, rpd.Schema, Registry.FindSchema, facts, new SignatoryReport([], []), 2026,
            DocumentMetadata.Hidden(rpd.Schema, DocumentSemesters.Of(payload, facts), Registry.FindSchema));
        return DocxGenerator.Generate(template ?? DocxBookmarksTests.Template(DocxBookmarksTests.Rpd), mapping ?? rpd.PrintMapping, context);
    }

    private static JsonObject Payload(bool labs, bool practice, bool courseProject, bool section6, int lastLines)
    {
        var payload = new JsonObject
        {
            ["goals_and_objectives"] = new JsonObject
            {
                ["goals"] = $"<p>{Filler(4)}</p>", ["objectives"] = Array(5, i => JsonValue.Create($"Задача {i}: {Filler(1)}"))
            },
            ["place_in_structure"] = new JsonObject { ["prior_outcomes"] = new JsonObject { ["knows"] = $"<p>{Filler(3)}</p>" } },
            ["learning_outcomes"] = new JsonObject { ["knows"] = $"<p>{Filler(3)}</p>", ["can"] = $"<p>{Filler(3)}</p>" },
            ["thematic_plan"] = Array(10, i => new JsonObject
            {
                ["section_number"] = i, ["section_title"] = $"Раздел дисциплины {i}", ["semester"] = 1, ["topic_title"] = $"Тема {i}",
                ["topic_description"] = Filler(2),
                ["hours"] = new JsonObject { ["lectures"] = 2, ["practice"] = 6, ["lab_hours"] = 2, ["self_study"] = 2 }
            }),
            ["literature"] = new JsonObject
            {
                ["main"] = Array(6, i => new JsonObject { ["citation"] = $"Автор {i}. Учебник. — Москва : Наука, 2024. — 320 с." }),
                // Текст вместо фразы шаблона и сама фраза: в v4 фраза раздела 4.5 имела уровень структуры и попадала в оглавление.
                ["guidelines"] = new JsonObject { ["lab_works"] = new JsonObject { ["text"] = "Практикум, ч. 1\nПрактикум, ч. 2" } }
            },
            ["material_support"] = new JsonArray("Лаборатория моделирования")
        };
        if (labs)
            payload["lab_works"] = Array(10, i => new JsonObject { ["number"] = $"{i}", ["section_number"] = i, ["title"] = $"Лабораторная работа {i}", ["hours"] = 2 });
        if (practice)
            payload["practical_classes"] = Array(60, i => new JsonObject { ["number"] = $"{i}", ["section_number"] = (i + 5) / 6, ["title"] = $"Занятие {i}", ["hours"] = 1 });
        if (courseProject) payload["course_work"] = new JsonObject { ["kind"] = "course_project", ["topics"] = new JsonArray("Тема проекта") };
        // Нумерованный список и заголовок в тексте автора заголовками разделов не становятся.
        if (section6)
            payload["additional_section"] = new JsonObject
            {
                ["title"] = Section6,
                ["text"] = $"<p><strong>7. Не раздел</strong></p><ol><li>{Filler(2)}</li><li>{Filler(1)}</li></ol><p>{Filler(6)}</p>" +
                    string.Concat(Enumerable.Range(1, lastLines).Select(i => $"<p>Строка {i}.</p>"))
            };
        return payload;

        static string Filler(int sentences) =>
            string.Join(" ", Enumerable.Repeat("Описание материала, который изучается в этой теме дисциплины.", sentences));

        static JsonArray Array(int count, Func<int, JsonNode?> item) => new(Enumerable.Range(1, count).Select(item).ToArray());
    }

    /// <summary>Абзацы строк оглавления — со ссылкой на закладку внутри документа.</summary>
    public static List<Paragraph> Lines(Body body) =>
        body.Elements<Paragraph>().Where(p => p.Elements<Hyperlink>().Any(l => l.Anchor?.Value?.StartsWith("_Toc", StringComparison.Ordinal) == true)).ToList();

    /// <summary>Текст абзаца с табуляциями, без кодов полей.</summary>
    public static string Plain(Paragraph paragraph) =>
        string.Concat(paragraph.Descendants().Select(e => e switch { Text t => t.Text, TabChar => "\t", _ => "" }));

    /// <summary>Строка без номера страницы: «1.\tЦели…\t3» — «1.\tЦели…».</summary>
    public static string WithoutPage(string line) => Regex.Replace(line, "\t\\d*$", "");

    /// <summary>Для сравнения строки с заголовком: без пробелов, точки после номера раздела и двоеточия заголовка.</summary>
    public static string Bare(string text) =>
        Regex.Replace(Regex.Replace(text, @"\s+", "").TrimEnd(':'), @"^(\d+(?:\.\d+)*)\.?", "$1");
}
