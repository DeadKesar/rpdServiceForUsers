using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RpdFos.Api.Features.Documents;
using RpdFos.Api.Features.Printing;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;
using RpdFos.Core.Printing;
using Xunit;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// Формирование DOCX реальной РПД (этап 5, B5): шаблон оформления DWP из фикстур, план 09.04.04, обезличенный
/// справочник должностей. Заглушек Autofill не остаётся, файл проходит OpenXmlValidator, у разделов, не
/// предусмотренных планом, — стандартные формулировки.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class DocxGenerationTests(DatabaseFixture app)
{
    private const string PriorKnowledge = "<p>основы <strong>высшей математики</strong>:</p><ul><li>линейная алгебра</li><li>теория вероятностей</li></ul>";

    [Fact]
    public async Task Real_rpd_is_printed_without_placeholders()
    {
        var (client, author) = await DocumentData.UserAsync(app);
        var created = await DocumentData.CreateAsync(app, client, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.10");
        var version = await DocumentData.FillForSubmissionAsync(client, created);
        var plan = DocumentData.Sums(created.Payload);
        var place = JsonNode.Parse(created.Payload.GetProperty("place_in_structure").GetRawText())!;
        place["prior_outcomes"] = new JsonObject { ["knows"] = PriorKnowledge };
        // Б1.О.10 — в 1 семестре: дисциплин раньше нет, позже — выбираются из плана.
        Assert.Empty(created.Metadata.PlanDisciplines.Earlier);
        // Вместо пустого списка 2.1 — формулировка магистратуры из сопоставления; редактор показывает её же.
        const string afterBachelor = "результаты обучения, полученные при освоении программы бакалавриата или специалитета";
        Assert.Equal(afterBachelor, created.Metadata.NoCandidatesTexts!["/place_in_structure/prerequisites"]);
        var later = created.Metadata.PlanDisciplines.Later.Take(2).ToList();
        place["postrequisites"] = new JsonArray(later.Select(d => (JsonNode)new JsonObject
        {
            ["discipline_id"] = d.Id.ToString(), ["code"] = d.Code, ["name"] = d.Name
        }).ToArray());
        // Новый документ получил постоянные пункты вуза из умолчаний шаблона — автор дополняет их своими.
        var literature = JsonNode.Parse(created.Payload.GetProperty("literature").GetRawText())!.AsObject();
        Assert.Equal((4, 8), (literature["internet_resources"]!.AsArray().Count, literature["software"]!.AsArray().Count));
        literature["main"] = new JsonArray(new JsonObject { ["citation"] = "Петров П. П. Моделирование систем : учебник. — Москва : Наука, 2024. — 320 с." });
        literature["additional"] = new JsonArray();
        literature["internet_resources"]!.AsArray().Add(new JsonObject { ["title"] = "Портал моделирования", ["url"] = "https://example.org/models" });
        literature["guidelines"] = new JsonObject { ["lab_works"] = "https://edu.example.org/labs" };
        literature["software"]!.AsArray().Add("Среда имитационного моделирования");
        var support = JsonNode.Parse(created.Payload.GetProperty("material_support").GetRawText())!.AsArray();
        support.Add("Компьютерный класс со средой моделирования.");
        var sections = new Dictionary<string, JsonNode>
        {
            // Виды самостоятельной работы: реферат с обозначением, изучение раздела 1, остаток — самоподготовка.
            ["hours_distribution"] = DocumentData.WithSelfStudy(created.Payload,
                new JsonObject { ["kind"] = "написание реферата", ["form_code"] = "Р", ["hours"] = new JsonArray(Hours(1, 6)) },
                new JsonObject { ["kind"] = "самостоятельное изучение разделов", ["sections"] = new JsonArray(1), ["hours"] = new JsonArray(Hours(1, 8)) },
                new JsonObject { ["kind"] = "подготовка к сдаче модулей" }, // без часов — не печатается
                new JsonObject { ["kind"] = "самоподготовка" }),
            ["place_in_structure"] = place,
            ["learning_outcomes"] = new JsonObject { ["knows"] = "<p>методы моделирования систем</p>" },
            ["thematic_plan"] = new JsonArray(new JsonObject
            {
                ["section_number"] = 1, ["topic_title"] = "Основы моделирования систем",
                ["topic_description"] = "Понятие модели и моделирования. Классификация моделей.",
                ["hours"] = new JsonObject
                {
                    ["lectures"] = plan.Lectures, ["practice"] = plan.Practice, ["lab_hours"] = plan.Labs, ["self_study"] = plan.SelfStudy
                },
                ["current_control_forms"] = new JsonArray("Опрос", "Р")
            }),
            ["literature"] = literature,
            ["material_support"] = support,
            // Один составитель: строка второго и протокол кафедры остаются бланками.
            ["approval_sheet"] = new JsonObject
            {
                ["developers"] = new JsonArray(new JsonObject
                {
                    ["user_id"] = author.Id.ToString(), ["name_for_document"] = "М. М. Методист", ["position"] = "доцент"
                })
            }
        };
        foreach (var (section, value) in sections)
            version = (await DocumentData.PatchAsync(client, created.Document.Id, section, version, value)).Version;

        var item = await PrintAsync(client, created);
        using var docx = await DownloadAsync(client, created, item);
        var body = docx.MainDocumentPart!.Document!.Body!;
        var text = body.InnerText;
        string Line(string start) => body.Elements<Paragraph>().First(p => p.InnerText.StartsWith(start)).InnerText;
        // Список от первого пункта до последнего: подряд, все пункты — элементы одного списка Word.
        void AssertOneList(string first, string last, int count)
        {
            var paragraphs = body.Elements<Paragraph>().ToList();
            var from = paragraphs.FindIndex(p => p.InnerText.StartsWith(first));
            var to = paragraphs.FindIndex(p => p.InnerText == last);
            Assert.Equal(count - 1, to - from);
            var lists = paragraphs.GetRange(from, count).Select(p => p.ParagraphProperties?.NumberingProperties?.NumberingId?.Val?.Value).ToList();
            Assert.NotNull(lists[0]);
            Assert.All(lists, id => Assert.Equal(lists[0], id));
        }

        Assert.DoesNotContain("Autofill", text);
        Assert.Contains("Математическое и компьютерное моделирование", text);
        Assert.Contains("Сформировать у обучающихся умения", text);                   // цели
        Assert.Contains("Освоить методы построения моделей систем", text);            // задачи
        Assert.Contains("основы высшей математики", text);                             // 2.1
        Assert.Contains("методы моделирования систем", text);                          // 2.2
        var paragraphs = body.Elements<Paragraph>().ToList();
        var requirements = paragraphs.FindIndex(p => p.InnerText.StartsWith("Для изучения дисциплины «"));
        Assert.Equal(afterBachelor, paragraphs[requirements + 1].InnerText);
        Assert.DoesNotContain(item.Unfilled, u => u.StartsWith("AutofillRequirementsLast"));
        // 2.2: где применяются — списком «индекс название» из плана.
        var next = body.Elements<Paragraph>().Where(p => later.Any(d => p.InnerText == $"{d.Code} {d.Name}")).ToList();
        Assert.Equal(2, next.Count);
        Assert.All(next, p => Assert.NotNull(p.ParagraphProperties?.NumberingProperties));
        Assert.Contains("Основы моделирования систем", text);                          // тематический план
        Assert.Contains(created.Metadata.CompetenceCandidates[0].Indicators[0].Code, text); // таблица компетенций
        Assert.Contains("относится к обязательной части", text);                       // форма слова: дательный падеж
        Assert.Contains("Сотрудник", text);                                            // подписанты из справочника
        Assert.Contains("Ответственный исполнитель, заведующий", text);                // должность внутри фразы — со строчной
        Assert.Equal("По дисциплине Математическое и компьютерное моделирование курсовое проектирование учебным планом не предусмотрено.",
            Line("По дисциплине"));

        // «Разделы дисциплины» одного семестра — одна группа: тема, «Промежуточная аттестация» с экзаменом, «Итого (час.)».
        Assert.Equal("Разделы дисциплины, изучаемые в 1 семестре:", Line("Разделы дисциплины, изучаемые"));
        var partition = body.Descendants<Table>().Single(t => t.InnerText.Contains("Раздел дисциплины"));
        var partitionRows = partition.Elements<TableRow>().Skip(3).Select(r => r.Elements<TableCell>().Select(c => c.InnerText).ToList()).ToList();
        Assert.Equal(["", "Промежуточная аттестация", "", "", "24", "16", "8", "24/36", "", "Экзамен"], partitionRows[^2]);
        Assert.Equal(["", "Итого (час.)", "", "", "24", "16", "8", "24/36", "", ""], partitionRows[^1]);

        // 3.1: общая трудоёмкость — сумма строк (контактная + контроль + самостоятельная), аттестация — экзамен.
        var laboriousness = Laboriousness(body);
        Assert.Equal(["108", "108"], Cells(laboriousness, "Общая трудоёмкость"));
        Assert.Equal(["48", "48"], Cells(laboriousness, "Контактная работа"));
        Assert.Equal(["36", "36"], Cells(laboriousness, "Промежуточный контроль"));
        Assert.Equal(["24", "24"], Cells(laboriousness, "Самостоятельная работа"));
        Assert.Equal(["Экзамен", "Экзамен"], Cells(laboriousness, "Вид промежуточного контроля"));
        // Виды самостоятельной работы с часами — подстроками прямым шрифтом вместо курсивной подсказки шаблона.
        Assert.Equal(["6", "6"], Cells(laboriousness, "- написание реферата (Р)"));
        Assert.Equal(["8", "8"], Cells(laboriousness, "- самостоятельное изучение разделов 1"));
        Assert.Equal(["10", "10"], Cells(laboriousness, "- самоподготовка"));
        Assert.DoesNotContain("подготовка к сдаче модулей", laboriousness.InnerText);
        Assert.DoesNotContain("и т.п.", laboriousness.InnerText);
        var kindRows = laboriousness.Elements<TableRow>().Where(r => r.InnerText.StartsWith("- ")).ToList();
        Assert.Equal(3, kindRows.Count);
        Assert.DoesNotContain(kindRows.SelectMany(r => r.Descendants<RunProperties>()), p => p.Italic is not null);
        // Разделы дисциплины: самостоятельная работа — общими часами раздела, реферат — обозначением в формах контроля.
        var topicCells = partition.Elements<TableRow>().First(r => r.InnerText.StartsWith("1Основы"))
            .Elements<TableCell>().Select(c => c.InnerText).ToList();
        Assert.Equal(("24", "Опрос, Р"), (topicCells[7], topicCells[9]));

        // 3.2: строка на тему, образцы шаблона убраны.
        var contents = body.Descendants<Table>().Single(t => t.InnerText.StartsWith("№ раздела"));
        Assert.Equal(2, contents.Elements<TableRow>().Count());
        Assert.Contains("Понятие модели и моделирования", contents.InnerText);

        // 4.3, 4.7, 5: пункты по умолчанию и пункты автора — один список шаблона с единой нумерацией.
        AssertOneList("Электронная образовательная среда в сети Интернет // Режим доступа URL: http://edu.stankin.ru/",
            "Портал моделирования // Режим доступа URL: https://example.org/models", 5);
        AssertOneList("Профессиональная база данных Web of Science", "Среда имитационного моделирования", 9);
        AssertOneList("Учебная аудитория для проведения лекций", "Компьютерный класс со средой моделирования.", 3);
        Assert.Contains("https://edu.example.org/labs", text);                         // 4.4 — ссылка

        // Пустые необязательные значения не стирают бланк: протокол и строка второго составителя — для ручного заполнения.
        Assert.Equal("протокол № ________ от \"___\" __________ 20__г.", Line("протокол №"));
        Assert.StartsWith("доцент", Line("доцент"));
        Assert.Contains("М. М. Методист", Line("доцент"));
        Assert.Contains(item.Unfilled, u => u.StartsWith("AutofillProtocolNumber1") && u.EndsWith("оставлен бланк"));
        Assert.Contains(item.Unfilled, u => u.StartsWith("AutofillDeveloperName2") && u.EndsWith("оставлен бланк"));

        // Форматированный текст: полужирный фрагмент и список с нумерацией Word.
        Assert.Contains(body.Descendants<Run>(), r => r.InnerText == "высшей математики" && r.RunProperties?.Bold is not null);
        var items = body.Descendants<Paragraph>().Where(p => p.InnerText is "линейная алгебра" or "теория вероятностей").ToList();
        Assert.Equal(2, items.Count);
        Assert.All(items, p => Assert.NotNull(p.ParagraphProperties?.NumberingProperties));

        Assert.Empty(new OpenXmlValidator(FileFormatVersions.Office2019).Validate(docx));

        // Председателя УМК в справочнике нет: место пустое, в отчёте — предупреждение и незаполненное место.
        Assert.Contains(item.Warnings, w => w.Contains("Председатель учебно-методической комиссии"));
        Assert.Contains(item.Unfilled, u => u.StartsWith("AutofillPredWay1"));

        await app.WithDbAsync(async db =>
        {
            var task = await db.GenerationTasks.SingleAsync(t => t.Id == item.TaskId);
            Assert.Equal((GenerationStatuses.Succeeded, author.Id), (task.Status, task.RequestedBy));
            var file = await db.GeneratedFiles.SingleAsync(f => f.GenerationTaskId == task.Id);
            Assert.Equal(file.CreatedAt.AddDays(30), file.ExpiresAt);
            // Учебный год — из титула (план 2023 года, 1 семестр), а не год печати.
            Assert.Equal("РПД_Математическое и компьютерное моделирование_2023-2024.docx", file.FileName);
            Assert.True(File.Exists(Path.Combine(app.GeneratedFiles, file.StoragePath)));
        });
    }

    [Fact]
    public async Task Sections_not_in_plan_get_standard_wording()
    {
        // Б1.О.01: лекции и практика — лабораторных по плану нет.
        var (client, _) = await DocumentData.UserAsync(app);
        var created = await DocumentData.CreateAsync(app, client, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.01");
        await DocumentData.FillForSubmissionAsync(client, created);

        using var docx = await DownloadAsync(client, created, await PrintAsync(client, created));
        var body = docx.MainDocumentPart!.Document!.Body!;

        // 3.3 и 4.4: формулировка вместо таблицы и вместо фразы о методических указаниях с ссылкой.
        Assert.Equal(2, body.Elements<Paragraph>().Count(p => p.InnerText == "Лабораторные работы учебным планом не предусмотрены."));
        Assert.DoesNotContain(body.Elements<Paragraph>(), p => p.InnerText.StartsWith("Методические указания к лабораторным занятиям по данной"));
        Assert.Contains(body.Elements<Paragraph>(), p => p.InnerText.StartsWith("Методические указания к практическим занятиям по данной"));
        Assert.DoesNotContain(body.Descendants<Table>(), t => t.InnerText.Contains("№ ЛР"));
        Assert.Contains(body.Descendants<Table>(), t => t.InnerText.Contains("№ занятия") && t.InnerText.Contains("Построение модели"));
        Assert.Empty(new OpenXmlValidator(FileFormatVersions.Office2019).Validate(docx));
    }

    [Fact]
    public async Task Course_project_phrase_reads_as_a_sentence()
    {
        // Б1.В.01: курсовой проект по плану — вид раздела из плана, фраза с родом: «предусмотрен курсовой проект».
        var (client, _) = await DocumentData.UserAsync(app);
        var created = await DocumentData.CreateAsync(app, client, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.В.01");
        await DocumentData.FillForSubmissionAsync(client, created);

        using var docx = await DownloadAsync(client, created, await PrintAsync(client, created));
        var body = docx.MainDocumentPart!.Document!.Body!;
        var line = body.Elements<Paragraph>().First(p => p.InnerText.StartsWith("По дисциплине")).InnerText;

        Assert.EndsWith(" предусмотрен курсовой проект. Примерные темы: Модель системы массового обслуживания.", line);
        // Курсовой проект — только в 3.5: в строках промежуточного контроля таблиц 3.1 и «Разделы дисциплины» его нет,
        // а экзамена или зачёта у этой дисциплины по плану нет.
        Assert.Equal(["", ""], Cells(Laboriousness(body), "Вид промежуточного контроля"));
        var partition = body.Descendants<Table>().Single(t => t.InnerText.Contains("Раздел дисциплины"));
        var attestation = partition.Elements<TableRow>().Single(r => r.InnerText.Contains("Промежуточная аттестация"));
        Assert.Equal("", attestation.Elements<TableCell>().Last().InnerText);
    }

    [Fact]
    public async Task Corrected_plan_fields_are_printed_without_marks()
    {
        // Б1.О.11: методист на проверке исправляет название и часы лекций — печать берёт исправленное.
        var (author, _) = await DocumentData.UserAsync(app);
        var (methodist, _) = await DocumentData.UserAsync(app, AppRoles.Methodist);
        var created = await DocumentData.CreateAsync(app, author, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.11");
        var version = await DocumentData.FillForSubmissionAsync(author, created);
        var submitted = await author.PostAsJsonAsync($"/api/documents/{created.Document.Id}/submit", new DocumentVersionRequest(version));
        version = (await submitted.Content.ReadFromJsonAsync<DocumentReviewed>())!.Version;

        var current = (await methodist.GetFromJsonAsync<DocumentDetails>($"/api/documents/{created.Document.Id}"))!;
        var title = JsonNode.Parse(current.Payload.GetProperty("title_page").GetRawText())!;
        title["discipline_name"] = "Исправленное название дисциплины";
        version = (await DocumentData.PatchAsync(methodist, created.Document.Id, "title_page", version, title)).Version;
        var hours = JsonNode.Parse(current.Payload.GetProperty("hours_distribution").GetRawText())!;
        var lectures = (int)hours["semesters"]![0]!["lectures"]! + 2;
        hours["semesters"]![0]!["lectures"] = lectures;
        await DocumentData.PatchAsync(methodist, created.Document.Id, "hours_distribution", version, hours);

        using var docx = await DownloadAsync(methodist, created, await PrintAsync(methodist, created));
        var body = docx.MainDocumentPart!.Document!.Body!;

        Assert.Contains("Исправленное название дисциплины", body.InnerText);
        Assert.Equal([lectures.ToString(), lectures.ToString()], Cells(Laboriousness(body), "Лекции"));
        Assert.DoesNotContain("изменено методистом", body.InnerText);
        Assert.DoesNotContain(PlanFields.DiffersFromPlan, body.InnerText);
    }

    [Fact]
    public async Task Methodist_prints_only_documents_under_review()
    {
        var (author, _) = await DocumentData.UserAsync(app);
        var (other, _) = await DocumentData.UserAsync(app);
        var (methodist, _) = await DocumentData.UserAsync(app, AppRoles.Methodist);
        var created = await DocumentData.CreateAsync(app, author, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.11");
        var url = $"/api/documents/{created.Document.Id}/print/docx";

        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsync(url, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await methodist.PostAsync(url, null)).StatusCode);

        var version = await DocumentData.FillForSubmissionAsync(author, created);
        var submitted = await author.PostAsJsonAsync($"/api/documents/{created.Document.Id}/submit", new DocumentVersionRequest(version));
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await methodist.PostAsync(url, null)).StatusCode);
    }

    [Fact]
    public async Task Pdf_is_converted_from_generated_docx_in_background()
    {
        var (client, _) = await DocumentData.UserAsync(app);
        var created = await DocumentData.CreateAsync(app, client, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.12");
        await DocumentData.FillForSubmissionAsync(client, created);

        var item = await PrintAsync(client, created, GenerationFormats.Pdf);
        var response = await client.GetAsync($"/api/documents/{created.Document.Id}/files/{item.FileId}");

        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(await response.Content.ReadAsByteArrayAsync()));
        Assert.EndsWith(".pdf", item.FileName);
        // Конвертеру ушёл сформированный DOCX — тот же, что скачивается в формате DOCX.
        using var sent = WordprocessingDocument.Open(new MemoryStream(FakePdfConverter.LastDocx!), false);
        Assert.Contains("Основы моделирования систем", sent.MainDocumentPart!.Document!.Body!.InnerText);

        // История формирования — от новых к старым.
        var history = (await client.GetFromJsonAsync<List<GenerationItem>>($"/api/documents/{created.Document.Id}/generations"))!;
        Assert.Equal(item.TaskId, history[0].TaskId);
    }

    [Fact]
    public async Task Unavailable_converter_fails_the_task_with_reason()
    {
        var (client, _) = await DocumentData.UserAsync(app);
        var created = await DocumentData.CreateAsync(app, client, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.13");
        FakePdfConverter.FailNext = true;

        var item = await PrintAsync(client, created, GenerationFormats.Pdf, GenerationStatuses.Failed);

        Assert.Contains("Конвертер PDF недоступен", item.ErrorMessage);
        Assert.Null(item.FileId);
    }

    [Fact]
    public async Task Expired_files_are_removed_by_cleanup()
    {
        var (client, _) = await DocumentData.UserAsync(app);
        var created = await DocumentData.CreateAsync(app, client, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.11");
        var item = await PrintAsync(client, created);
        string? path = null;
        await app.WithDbAsync(async db =>
        {
            var file = await db.GeneratedFiles.SingleAsync(f => f.Id == item.FileId);
            path = Path.Combine(app.GeneratedFiles, file.StoragePath);
            file.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        });

        await using (var scope = app.Services.CreateAsyncScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<GeneratedFilesCleanup>().RunAsync(CancellationToken.None) >= 1);

        Assert.False(File.Exists(path));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/documents/{created.Document.Id}/files/{item.FileId}")).StatusCode);
        await app.WithDbAsync(async db => Assert.False(await db.GeneratedFiles.AnyAsync(f => f.Id == item.FileId)));
    }

    /// <summary>Постановка в очередь (202) и опрос статуса, пока фоновая задача не завершится.</summary>
    private static async Task<GenerationItem> PrintAsync(HttpClient client, DocumentDetails document,
        string format = GenerationFormats.Docx, string expected = GenerationStatuses.Succeeded)
    {
        var response = await client.PostAsync($"/api/documents/{document.Document.Id}/print/{format}", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var item = (await response.Content.ReadFromJsonAsync<GenerationItem>())!;
        Assert.Equal(GenerationStatuses.Queued, item.Status);

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (item.Status is GenerationStatuses.Queued or GenerationStatuses.Running && DateTime.UtcNow < deadline)
        {
            await Task.Delay(300);
            item = (await client.GetFromJsonAsync<GenerationItem>(response.Headers.Location!))!;
        }
        Assert.Equal(expected, item.Status);
        return item;
    }

    private static JsonObject Hours(int semester, int hours) => new() { ["semester"] = semester, ["hours"] = hours };

    private static Table Laboriousness(Body body) => body.Descendants<Table>().Single(t => t.InnerText.Contains("Общая трудоёмкость"));

    // Значения строки таблицы 3.1 по началу подписи — ячейки после подписи.
    private static List<string> Cells(Table table, string label) => table.Elements<TableRow>()
        .First(r => r.Elements<TableCell>().First().InnerText.StartsWith(label))
        .Elements<TableCell>().Skip(1).Select(c => c.InnerText).ToList();

    private static async Task<WordprocessingDocument> DownloadAsync(HttpClient client, DocumentDetails document, GenerationItem item)
    {
        var response = await client.GetAsync($"/api/documents/{document.Document.Id}/files/{item.FileId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stream = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        return WordprocessingDocument.Open(stream, false);
    }
}
