using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using RpdFos.Api.Features.Documents;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;
using Xunit;

namespace RpdFos.Api.Tests.Integration;

/// <summary>Замечания к записям списков следуют за записью (этап 8, A14): перестановка, вставка, удаление.</summary>
[Collection(DatabaseCollection.Name)]
public class CommentPlacesApiTests(DatabaseFixture app)
{
    private static JsonObject Book(string title) => new()
    {
        ["citation"] = $"{title} : учебник. — Москва : Наука, {DateTime.Today.Year - 1}. — 300 с.", ["year"] = DateTime.Today.Year - 1
    };

    private static JsonObject Literature(params JsonObject[] main) => new()
    {
        ["main"] = new JsonArray(main.Select(m => (JsonNode)m.DeepClone()).ToArray()), ["additional"] = new JsonArray()
    };

    private static string Url(DocumentDetails d, string action) => $"/api/documents/{d.Document.Id}/{action}";

    [Fact]
    public async Task Comment_follows_its_record_through_move_insert_and_delete_and_stays_when_record_is_deleted()
    {
        var (author, _) = await DocumentData.UserAsync(app);
        var (methodist, _) = await DocumentData.UserAsync(app, AppRoles.Methodist);
        var created = await DocumentData.CreateAsync(app, author, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.12");
        var id = created.Document.Id;
        var (a, b, c) = (Book("Алексеев А. А. Первая"), Book("Борисов Б. Б. Вторая"), Book("Волков В. В. Третья"));
        var version = await DocumentData.FillForSubmissionAsync(author, created);
        version = (await DocumentData.PatchAsync(author, id, "literature", version, Literature(a, b, c))).Version;
        version = await StatusChangeAsync(author.PostAsJsonAsync(Url(created, "submit"), new DocumentVersionRequest(version)));

        // Замечание к третьей записи; методист возвращает документ.
        var comment = (await (await methodist.PostAsJsonAsync(Url(created, "comments"),
            new CommentRequest("/literature/main/2/citation", "Уточните выходные данные"))).Content.ReadFromJsonAsync<CommentItem>())!;
        version = await StatusChangeAsync(methodist.PostAsJsonAsync(Url(created, "return"), new ReturnRequest(version, null)));

        // Проверка без сохранения показывает, куда уйдёт замечание, но ничего не меняет.
        var check = await author.PostAsJsonAsync(Url(created, "sections/literature/check"),
            new SectionUpdateRequest(version, JsonDocument.Parse(Literature(c, a, b).ToJsonString()).RootElement));
        var moves = (await check.Content.ReadFromJsonAsync<SectionChecked>())!.Comments!;
        Assert.Equal(("/literature/main/0/citation", false), (Assert.Single(moves).Path, moves[0].IsRecordDeleted));
        Assert.Equal("/literature/main/2/citation", (await PathAsync(author, created, comment.Id)).Path);

        // Запись поднята на первое место — замечание у неё.
        version = (await DocumentData.PatchAsync(author, id, "literature", version, Literature(c, a, b))).Version;
        Assert.Equal(("/literature/main/0/citation", false), await PathAsync(author, created, comment.Id));

        // Удалена запись ниже — замечание у своей; вставлена новая выше — сдвиг.
        version = (await DocumentData.PatchAsync(author, id, "literature", version, Literature(c, b))).Version;
        Assert.Equal(("/literature/main/0/citation", false), await PathAsync(author, created, comment.Id));
        var fresh = Book("Громов Г. Г. Новая");
        version = (await DocumentData.PatchAsync(author, id, "literature", version, Literature(fresh, c, b))).Version;
        Assert.Equal(("/literature/main/1/citation", false), await PathAsync(author, created, comment.Id));

        // Запись изменена и сдвинута сразу — замечание всё равно у неё.
        var edited = Book("Волков В. В. Третья, переработанная");
        edited["year"] = DateTime.Today.Year - 2;
        version = (await DocumentData.PatchAsync(author, id, "literature", version, Literature(c, fresh, b))).Version;
        Assert.Equal(("/literature/main/0/citation", false), await PathAsync(author, created, comment.Id));
        version = (await DocumentData.PatchAsync(author, id, "literature", version, Literature(fresh, b, edited))).Version;
        Assert.Equal(("/literature/main/2/citation", false), await PathAsync(author, created, comment.Id));

        // Удалена сама запись: замечание остаётся, на её месте теперь другая запись — ей оно не достаётся.
        version = (await DocumentData.PatchAsync(author, id, "literature", version, Literature(fresh, b))).Version;
        var (path, deleted) = await PathAsync(author, created, comment.Id);
        Assert.True(deleted);
        Assert.Equal("/literature/main/2/citation", path);
        version = (await DocumentData.PatchAsync(author, id, "literature", version, Literature(fresh, b, Book("Дроздов Д. Д. Четвёртая")))).Version;
        Assert.Equal(("/literature/main/2/citation", true), await PathAsync(author, created, comment.Id));

        // Замечание неснятое: приём заблокирован, пока автор его не снимет.
        version = await StatusChangeAsync(author.PostAsJsonAsync(Url(created, "submit"), new DocumentVersionRequest(version)));
        Assert.Equal(HttpStatusCode.Conflict, (await methodist.PostAsJsonAsync(Url(created, "accept"), new DocumentVersionRequest(version))).StatusCode);
        var resolved = await methodist.PostAsync(Url(created, $"comments/{comment.Id}/resolve"), null);
        Assert.True(resolved.IsSuccessStatusCode, await resolved.Content.ReadAsStringAsync());
        Assert.True((await methodist.PostAsJsonAsync(Url(created, "accept"), new DocumentVersionRequest(version))).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Comment_to_a_field_outside_lists_and_to_a_list_itself_does_not_move()
    {
        var (author, _) = await DocumentData.UserAsync(app);
        var (methodist, _) = await DocumentData.UserAsync(app, AppRoles.Methodist);
        var created = await DocumentData.CreateAsync(app, author, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.13");
        var id = created.Document.Id;
        var version = await DocumentData.FillForSubmissionAsync(author, created);
        version = (await DocumentData.PatchAsync(author, id, "literature", version, Literature(Book("Алексеев А. А. Первая"), Book("Борисов Б. Б. Вторая")))).Version;
        version = await StatusChangeAsync(author.PostAsJsonAsync(Url(created, "submit"), new DocumentVersionRequest(version)));
        var onList = (await (await methodist.PostAsJsonAsync(Url(created, "comments"), new CommentRequest("/literature/main", "Мало источников")))
            .Content.ReadFromJsonAsync<CommentItem>())!;
        var onGoals = (await (await methodist.PostAsJsonAsync(Url(created, "comments"), new CommentRequest("/goals_and_objectives/goals", "Уточните цели")))
            .Content.ReadFromJsonAsync<CommentItem>())!;
        version = await StatusChangeAsync(methodist.PostAsJsonAsync(Url(created, "return"), new ReturnRequest(version, null)));

        // Список очищен целиком — замечание к самому списку остаётся на месте.
        await DocumentData.PatchAsync(author, id, "literature", version, Literature());
        Assert.Equal(("/literature/main", false), await PathAsync(author, created, onList.Id));
        Assert.Equal(("/goals_and_objectives/goals", false), await PathAsync(author, created, onGoals.Id));
    }

    private async Task<(string Path, bool Deleted)> PathAsync(HttpClient client, DocumentDetails created, Guid commentId)
    {
        var comments = (await client.GetFromJsonAsync<List<CommentItem>>(Url(created, "comments")))!;
        var comment = comments.Single(c => c.Id == commentId);
        // То же, что видит база: клиент ничего не пересчитывает.
        await app.WithDbAsync(async db =>
        {
            var row = await db.DocumentComments.SingleAsync(c => c.Id == commentId);
            Assert.Equal((comment.FieldPath, comment.IsRecordDeleted), (row.FieldPath, row.IsRecordDeleted));
        });
        return (comment.FieldPath, comment.IsRecordDeleted);
    }

    private static async Task<int> StatusChangeAsync(Task<HttpResponseMessage> request)
    {
        var response = await request;
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<DocumentReviewed>())!.Version;
    }
}
