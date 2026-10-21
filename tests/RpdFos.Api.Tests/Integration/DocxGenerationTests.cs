using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.EntityFrameworkCore;
using RpdFos.Api.Features.Documents;
using RpdFos.Api.Features.Printing;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;
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
    private const string Prerequisites = "<p>Знания <strong>высшей математики</strong>:</p><ul><li>линейная алгебра</li><li>теория вероятностей</li></ul>";

    [Fact]
    public async Task Real_rpd_is_printed_without_placeholders()
    {
        var (client, author) = await DocumentData.UserAsync(app);
        var created = await DocumentData.CreateAsync(app, client, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.10");
        var version = await DocumentData.FillForSubmissionAsync(client, created);
        var place = JsonNode.Parse(created.Payload.GetProperty("place_in_structure").GetRawText())!;
        place["prerequisites"] = Prerequisites;
        await DocumentData.PatchAsync(client, created.Document.Id, "place_in_structure", version, place);

        var item = await PrintAsync(client, created);
        using var docx = await DownloadAsync(client, created, item);
        var body = docx.MainDocumentPart!.Document!.Body!;
        var text = body.InnerText;

        Assert.DoesNotContain("Autofill", text);
        Assert.Contains("Математическое и компьютерное моделирование", text);
        Assert.Contains("Основы моделирования систем", text);                         // тематический план
        Assert.Contains(created.Metadata.CompetenceCandidates[0].Indicators[0].Code, text); // таблица компетенций
        Assert.Contains("относится к обязательной части", text);                      // форма слова: дательный падеж
        Assert.Contains("Сотрудник", text);                                           // подписанты из справочника
        Assert.Contains("В учебном плане курсовое проектирование для данной дисциплины не предусмотрено.", text);

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
            Assert.Equal($"РПД_Математическое и компьютерное моделирование_{DateTime.UtcNow.Year}.docx", file.FileName);
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

        Assert.Contains("Лабораторные работы учебным планом не предусмотрены.", body.InnerText);
        Assert.DoesNotContain(body.Descendants<Table>(), t => t.InnerText.Contains("№ ЛР"));
        Assert.Contains(body.Descendants<Table>(), t => t.InnerText.Contains("№ занятия") && t.InnerText.Contains("Построение модели"));
        Assert.Empty(new OpenXmlValidator(FileFormatVersions.Office2019).Validate(docx));
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
        Assert.Equal(HttpStatusCode.OK, (await methodist.PostAsync(url, null)).StatusCode);
    }

    private static async Task<GenerationItem> PrintAsync(HttpClient client, DocumentDetails document)
    {
        var response = await client.PostAsync($"/api/documents/{document.Document.Id}/print/docx", null);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var item = (await response.Content.ReadFromJsonAsync<GenerationItem>())!;
        Assert.Equal(GenerationStatuses.Succeeded, item.Status);
        return item;
    }

    private static async Task<WordprocessingDocument> DownloadAsync(HttpClient client, DocumentDetails document, GenerationItem item)
    {
        var response = await client.GetAsync($"/api/documents/{document.Document.Id}/files/{item.FileId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stream = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        return WordprocessingDocument.Open(stream, false);
    }
}
