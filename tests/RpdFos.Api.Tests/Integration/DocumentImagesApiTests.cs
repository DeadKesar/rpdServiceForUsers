using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RpdFos.Api.Features.Documents;
using RpdFos.Api.Features.Images;
using RpdFos.Api.Tests.Documents;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;
using Xunit;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// Рисунки в тексте документа (этап 7, B4а): загрузка (PNG и JPEG по сигнатуре, лимиты, метаданные, повтор того же файла),
/// чтение по документам, в которых рисунок вставлен, и сохранение раздела — своё и читаемое остаётся и связывается, чужое и
/// внешнее снимается. Перенос «на основе» — в <see cref="DocumentBasedOnApiTests"/>.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class DocumentImagesApiTests(DatabaseFixture app)
{
    internal static string Tag(Guid id, string attributes = "") => $"<img src=\"/api/images/{id}\" {attributes}>".Replace(" >", ">");

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid documentId, byte[] content, string name = "scheme.png", string mime = "image/png")
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(mime);
        form.Add(file, "file", name);
        return await client.PostAsync($"/api/documents/{documentId}/images", form);
    }

    internal static async Task<ImageUploaded> UploadedAsync(HttpClient client, Guid documentId, byte[] content, string name = "scheme.png")
    {
        var response = await UploadAsync(client, documentId, content, name);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ImageUploaded>())!;
    }

    private async Task<(HttpClient Client, Employee Employee, DocumentDetails Document)> AuthorWithDocumentAsync(string code)
    {
        var (client, employee) = await DocumentData.UserAsync(app);
        var created = await DocumentData.CreateAsync(app, client, (await DocumentData.PublishedTemplateAsync(app)).Id, code);
        return (client, employee, created);
    }

    private static Task<SectionSaved> SaveGoalsAsync(HttpClient client, DocumentDetails document, int version, string goals) =>
        DocumentData.PatchAsync(client, document.Document.Id, "goals_and_objectives", version,
            new JsonObject { ["goals"] = goals, ["objectives"] = new JsonArray("Изучить схемы") });

    private async Task<string> GoalsOfAsync(Guid documentId)
    {
        var goals = "";
        await app.WithDbAsync(async db =>
            goals = (string)JsonNode.Parse((await db.Documents.AsNoTracking().SingleAsync(d => d.Id == documentId)).Payload)!["goals_and_objectives"]!["goals"]!);
        return goals;
    }

    [Fact]
    public async Task Png_is_stored_in_the_database_without_metadata_and_linked_to_the_document()
    {
        var (client, employee, document) = await AuthorWithDocumentAsync("Б1.О.10");
        var source = TestImages.PngWithText("Иванов И. И.");

        var uploaded = await UploadedAsync(client, document.Document.Id, source, "..\\схема цепи.png");

        Assert.Equal($"/api/images/{uploaded.Id}", uploaded.Url);
        await app.WithDbAsync(async db =>
        {
            var file = await db.UploadedFiles.AsNoTracking().SingleAsync(f => f.Id == uploaded.Id);
            Assert.Equal((UploadedFileTypes.Image, "image/png", employee.Id), (file.FileType, file.MimeType, file.UploadedBy));
            // Имя — без пути; в содержимом нет текстовых частей PNG; контрольная сумма — по очищенному содержимому.
            Assert.Equal("схема цепи.png", file.OriginalName);
            Assert.DoesNotContain("Author", System.Text.Encoding.Latin1.GetString(file.Content!));
            Assert.Equal(file.Content!.Length, file.SizeBytes);
            Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(file.Content)), file.Checksum);
            Assert.True(await db.DocumentImages.AnyAsync(i => i.DocumentId == document.Document.Id && i.FileId == uploaded.Id));
        });
    }

    [Fact]
    public async Task The_same_file_in_a_document_is_stored_once_and_another_one_is_a_new_record()
    {
        var (client, _, document) = await AuthorWithDocumentAsync("Б1.О.10");

        var first = await UploadedAsync(client, document.Document.Id, TestImages.Png(seed: 10));
        var again = await UploadedAsync(client, document.Document.Id, TestImages.Png(seed: 10));
        // Тот же рисунок, но с другим текстом метаданных, после очистки — тот же файл.
        var withText = await UploadedAsync(client, document.Document.Id, TestImages.Png(40, 30, 10, ("tEXt", "Comment\0x"u8.ToArray())));
        var other = await UploadedAsync(client, document.Document.Id, TestImages.Png(seed: 11));
        var jpeg = await UploadedAsync(client, document.Document.Id, TestImages.Jpeg(seed: 12), "photo.jpg");

        Assert.Equal(first.Id, again.Id);
        Assert.Equal(first.Id, withText.Id);
        Assert.Equal(3, new[] { first.Id, other.Id, jpeg.Id }.Distinct().Count());
        await app.WithDbAsync(async db => Assert.Equal(3, await db.DocumentImages.CountAsync(i => i.DocumentId == document.Document.Id)));
        // JPEG хранится со своим типом.
        await app.WithDbAsync(async db =>
            Assert.Equal("image/jpeg", (await db.UploadedFiles.AsNoTracking().SingleAsync(f => f.Id == jpeg.Id)).MimeType));
    }

    [Fact]
    public async Task Other_types_oversize_and_broken_files_are_refused_with_a_reason()
    {
        var (client, _, document) = await AuthorWithDocumentAsync("Б1.О.10");

        async Task<ProblemDetails> RefusedAsync(byte[] content, string name = "a.png", string mime = "image/png")
        {
            var response = await UploadAsync(client, document.Document.Id, content, name, mime);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>())!;
            Assert.Equal("Рисунок не принят", problem.Title);
            return problem;
        }

        // Тип — по сигнатуре: имя и заголовок «image/png» ничего не значат.
        Assert.Equal(ImageFiles.NotImage, (await RefusedAsync("GIF89a\u0001\u0000\u0001\u0000"u8.ToArray())).Detail);
        Assert.Equal(ImageFiles.NotImage, (await RefusedAsync("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8.ToArray(), "a.svg", "image/svg+xml")).Detail);
        Assert.Equal(ImageFiles.Damaged, (await RefusedAsync(TestImages.Png()[..30])).Detail);
        // Лишний размер: ровно предел проходит, на байт больше — отказ.
        Assert.Equal("Рисунок больше 2 МБ.", (await RefusedAsync(new byte[ImageFiles.MaxBytes + 1])).Detail);
        // Нет файла в запросе.
        var empty = await client.PostAsync($"/api/documents/{document.Document.Id}/images", new MultipartFormDataContent());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        var notForm = await client.PostAsJsonAsync($"/api/documents/{document.Document.Id}/images", new { file = "x" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notForm.StatusCode);
        await app.WithDbAsync(async db => Assert.False(await db.DocumentImages.AnyAsync(i => i.DocumentId == document.Document.Id)));
    }

    [Fact]
    public async Task The_hundred_and_first_image_in_a_document_is_refused_but_a_known_one_is_not()
    {
        var (client, employee, document) = await AuthorWithDocumentAsync("Б1.О.10");
        var known = TestImages.Png(seed: 1);
        await app.WithDbAsync(async db =>
        {
            for (var i = 0; i < ImageFiles.MaxPerDocument; i++)
            {
                var content = i == 0 ? ImageFiles.Inspect(known).Image!.Content : TestImages.Png(seed: 1000 + i);
                var file = new UploadedFile
                {
                    UploadedBy = employee.Id, FileType = UploadedFileTypes.Image, OriginalName = $"{i}.png", Content = content, MimeType = "image/png",
                    SizeBytes = content.Length, Checksum = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content)),
                    UploadedAt = DateTimeOffset.UtcNow
                };
                db.UploadedFiles.Add(file);
                db.DocumentImages.Add(new DocumentImage { DocumentId = document.Document.Id, FileId = file.Id, LinkedAt = DateTimeOffset.UtcNow });
            }
            await db.SaveChangesAsync();
        });

        var refused = await UploadAsync(client, document.Document.Id, TestImages.Png(seed: 2));
        var same = await UploadAsync(client, document.Document.Id, known);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("100", (await refused.Content.ReadFromJsonAsync<ProblemDetails>())!.Detail);
        // Тот же файл нового рисунка не добавляет: лимит его не касается.
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        await app.WithDbAsync(async db => Assert.Equal(100, await db.DocumentImages.CountAsync(i => i.DocumentId == document.Document.Id)));
    }

    [Fact]
    public async Task Only_those_who_may_edit_the_document_upload_and_only_those_who_may_read_it_read()
    {
        var (author, _, document) = await AuthorWithDocumentAsync("Б1.О.10");
        var (stranger, _) = await DocumentData.UserAsync(app);
        var (methodist, _) = await DocumentData.UserAsync(app, AppRoles.Methodist);
        var uploaded = await UploadedAsync(author, document.Document.Id, TestImages.Png(seed: 20));

        // Загрузка: чужой — 403, без входа — 401; методист черновик не правит.
        var byStranger = await UploadAsync(stranger, document.Document.Id, TestImages.Png(seed: 21));
        var anonymous = await UploadAsync(app.CreateClientFor(null), document.Document.Id, TestImages.Png(seed: 22));
        var byMethodist = await UploadAsync(methodist, document.Document.Id, TestImages.Png(seed: 23));
        var missingDocument = await UploadAsync(author, Guid.NewGuid(), TestImages.Png(seed: 24));
        Assert.Equal(HttpStatusCode.Forbidden, byStranger.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, byMethodist.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingDocument.StatusCode);

        // Чтение: автор и методист — рисунок, посторонний — 403, без входа — 401, неизвестный номер — 404.
        var read = await author.GetAsync(uploaded.Url);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("image/png", read.Content.Headers.ContentType!.MediaType);
        Assert.Equal(ImageFiles.Inspect(TestImages.Png(seed: 20)).Image!.Content, await read.Content.ReadAsByteArrayAsync());
        Assert.Contains("private", read.Headers.CacheControl!.ToString());
        Assert.Equal(HttpStatusCode.OK, (await methodist.GetAsync(uploaded.Url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync(uploaded.Url)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClientFor(null).GetAsync(uploaded.Url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await author.GetAsync($"/api/images/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task A_file_that_is_not_an_image_is_not_served_as_one()
    {
        var (client, _, _) = await AuthorWithDocumentAsync("Б1.О.10");
        var planFileId = Guid.Empty;
        await app.WithDbAsync(async db =>
            planFileId = await db.UploadedFiles.Where(f => f.FileType == UploadedFileTypes.CurriculumPlan).Select(f => f.Id).FirstAsync());

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/images/{planFileId}")).StatusCode);
    }

    [Fact]
    public async Task Saving_a_section_keeps_own_images_and_drops_foreign_and_unknown_ones()
    {
        var (client, _, document) = await AuthorWithDocumentAsync("Б1.О.10");
        var own = await UploadedAsync(client, document.Document.Id, TestImages.Png(seed: 30));
        var unknown = Guid.NewGuid();

        var saved = await SaveGoalsAsync(client, document, document.Document.Version,
            $"<p>Схема: {Tag(own.Id, "width=\"120\" height=\"60\" alt=\"Схема\"")}</p><p>{Tag(unknown)}" +
            "<img src=\"https://example.org/a.png\"><img src=\"data:image/png;base64,AAAA\"><img src=\"file:///C:/a.png\" onerror=\"x()\"></p>");

        Assert.Equal($"<p>Схема: {Tag(own.Id, "width=\"120\" height=\"60\" alt=\"Схема\"")}</p><p></p>", await GoalsOfAsync(document.Document.Id));
        Assert.Equal(2, saved.Version);
    }

    [Fact]
    public async Task An_image_of_a_document_the_author_cannot_read_is_dropped_on_save_and_stays_unreadable()
    {
        var (owner, _, foreignDocument) = await AuthorWithDocumentAsync("Б1.О.10");
        var (client, _, document) = await AuthorWithDocumentAsync("Б1.О.11");
        var foreign = await UploadedAsync(owner, foreignDocument.Document.Id, TestImages.Png(seed: 40));

        // Номер рисунка постороннего угадан или подсмотрен: вставка снимается, связи не появляется.
        await SaveGoalsAsync(client, document, document.Document.Version, $"<p>Чужой: {Tag(foreign.Id)}</p>");

        Assert.Equal("<p>Чужой: </p>", await GoalsOfAsync(document.Document.Id));
        await app.WithDbAsync(async db => Assert.False(await db.DocumentImages.AnyAsync(i => i.DocumentId == document.Document.Id)));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(foreign.Url)).StatusCode);
    }

    [Fact]
    public async Task An_image_of_another_document_the_author_reads_is_kept_linked_and_survives_the_first_document()
    {
        var (client, _, first) = await AuthorWithDocumentAsync("Б1.О.10");
        var template = await DocumentData.PublishedTemplateAsync(app);
        var second = await DocumentData.CreateAsync(app, client, template.Id, "Б1.О.11");
        var image = await UploadedAsync(client, first.Document.Id, TestImages.Png(seed: 50));

        // Вставка из буфера: рисунок первого документа, который автор читает, — во втором.
        await SaveGoalsAsync(client, second, second.Document.Version, $"<p>{Tag(image.Id, "width=\"80\"")}</p>");

        Assert.Equal($"<p>{Tag(image.Id, "width=\"80\"")}</p>", await GoalsOfAsync(second.Document.Id));
        await app.WithDbAsync(async db =>
        {
            Assert.Equal(2, await db.DocumentImages.CountAsync(i => i.FileId == image.Id));
            Assert.Equal(1, await db.UploadedFiles.CountAsync(f => f.Id == image.Id));
        });

        // Первый документ удалён: рисунок читают по второму.
        var deleted = await client.DeleteAsync($"/api/documents/{first.Document.Id}?version={first.Document.Version}");
        Assert.True(deleted.IsSuccessStatusCode, await deleted.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(image.Url)).StatusCode);
    }

    [Fact]
    public async Task Section_check_does_not_link_images()
    {
        var (client, _, first) = await AuthorWithDocumentAsync("Б1.О.10");
        var template = await DocumentData.PublishedTemplateAsync(app);
        var second = await DocumentData.CreateAsync(app, client, template.Id, "Б1.О.11");
        var image = await UploadedAsync(client, first.Document.Id, TestImages.Png(seed: 60));

        var response = await client.PostAsJsonAsync($"/api/documents/{second.Document.Id}/sections/goals_and_objectives/check",
            new SectionUpdateRequest(second.Document.Version, System.Text.Json.JsonDocument.Parse(
                new JsonObject { ["goals"] = $"<p>{Tag(image.Id)}</p>" }.ToJsonString()).RootElement));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await app.WithDbAsync(async db => Assert.False(await db.DocumentImages.AnyAsync(i => i.DocumentId == second.Document.Id)));
    }
}
