using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RpdFos.Api.Features.Documents;
using RpdFos.Core.Data.Entities;
using Xunit;
using static RpdFos.Api.Tests.Integration.DocumentCircles;
using S = RpdFos.Core.Documents.DocumentStatuses;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// Что закрепление дисциплины (этап 7, A10; решение 5) меняет для документов: закреплённый видит документы дисциплины и не
/// правит их, создают документы закреплённые преподаватели, «Документы моих дисциплин» и передача документа. Дисциплины
/// реального плана берутся по индексу; закрепления тест убирает за собой (<see cref="AssignmentTestBase"/>).
/// </summary>
[Collection(DatabaseCollection.Name)]
public class DisciplineAccessApiTests(DatabaseFixture fixture) : AssignmentTestBase(fixture)
{
    private const string Reason = "Преподаватель ушёл с кафедры";

    [Fact]
    public async Task An_assigned_employee_reads_the_documents_of_the_discipline_but_changes_nothing_and_loses_the_access_when_unassigned()
    {
        var (methodist, _) = await AccountAsync(App, AppRoles.Methodist);
        var (author, authorRecord) = await AccountAsync(App, AppRoles.Teacher, "Автор Документа Обезличенный");
        var (holder, holderRecord) = await AccountAsync(App, AppRoles.Teacher, "Закреплённый Первый Обезличенный");
        var (stranger, _) = await AccountAsync(App, AppRoles.Teacher, "Посторонний Преподаватель Обезличенный");
        var created = await DocumentData.CreateAsync(App, author, (await DocumentData.PublishedTemplateAsync(App)).Id, "Б1.О.10");
        var id = created.Document.Id;
        var ids = await DisciplinesAsync("Б1.О.10");

        // До закрепления чужой документ закрыт для обоих преподавателей.
        foreach (var client in new[] { holder, stranger })
            await ExpectStatusAsync(HttpStatusCode.Forbidden, client.GetAsync(Url(id, "")));

        await AssignedAsync(methodist, holderRecord, ids);

        // Закреплённому документ открыт — только для чтения: ни правки, ни действий; замечания он читает.
        var seen = await DetailsAsync(holder, id);
        Assert.Equal((id, authorRecord.Id, false), (seen.Document.Id, seen.Document.AuthorId, seen.Editable));
        Assert.Equal(new DocumentActions(Submit: false, Review: false, ResolveComments: false, Print: false), seen.Actions);
        await ExpectStatusAsync(HttpStatusCode.OK, holder.GetAsync(Url(id, "comments")));
        foreach (var path in new[] { "", "comments" })
            await ExpectStatusAsync(HttpStatusCode.Forbidden, stranger.GetAsync(Url(id, path)));

        // Правит и действует только автор: любая попытка закреплённого — 403, документ не меняется.
        var before = await DetailsAsync(author, id);
        var edit = new SectionUpdateRequest(created.Document.Version,
            JsonDocument.Parse("""{"goals":"<p>Правка закреплённого</p>","objectives":["Задача"]}""").RootElement);
        await ExpectStatusAsync(HttpStatusCode.Forbidden, holder.PatchAsJsonAsync(Url(id, "sections/goals_and_objectives"), edit));
        await ExpectStatusAsync(HttpStatusCode.Forbidden, holder.PostAsJsonAsync(Url(id, "sections/goals_and_objectives/check"), edit));
        foreach (var action in new[] { "save", "submit", "archive" })
            await ExpectStatusAsync(HttpStatusCode.Forbidden, holder.PostAsJsonAsync(Url(id, action), new DocumentVersionRequest(created.Document.Version)));
        await ExpectStatusAsync(HttpStatusCode.Forbidden, holder.DeleteAsync($"{Url(id, "")}?version={created.Document.Version}"));
        await ExpectStatusAsync(HttpStatusCode.Forbidden, holder.PostAsync(Url(id, "print/docx"), null));
        await ExpectStatusAsync(HttpStatusCode.Forbidden,
            holder.PostAsJsonAsync(Url(id, "transfer"), new TransferRequest(created.Document.Version, holderRecord.Id, Reason)));
        var after = await DetailsAsync(author, id);
        Assert.Equal((before.Document.Version, before.Payload.GetRawText()), (after.Document.Version, after.Payload.GetRawText()));

        // «Документы моих дисциплин»: чужие документы закреплённых дисциплин, с теми же фильтрами, что у основного списка.
        Assert.DoesNotContain((await holder.GetFromJsonAsync<List<DocumentListItem>>("/api/documents"))!, d => d.Id == id);
        var mine = Assert.Single((await holder.GetFromJsonAsync<List<DocumentListItem>>("/api/documents/assigned"))!);
        Assert.Equal((id, authorRecord.ShortName, S.Draft, false), (mine.Id, mine.AuthorName, mine.Status, mine.CanDelete));
        var planId = await App.RealPlanIdAsync();
        Assert.Single((await holder.GetFromJsonAsync<List<DocumentListItem>>($"/api/documents/assigned?status={S.Draft}&planId={planId}"))!);
        Assert.Empty((await holder.GetFromJsonAsync<List<DocumentListItem>>($"/api/documents/assigned?status={S.Approved}"))!);
        Assert.Empty((await holder.GetFromJsonAsync<List<DocumentListItem>>($"/api/documents/assigned?planId={Guid.NewGuid()}"))!);
        // Автору документа, постороннему и методисту второй список пуст: свои документы — в основном, у методиста там все. Методист,
        // который ещё и преподаватель, закреплён за дисциплиной, но и ему документ уже есть в основном списке — дубля нет.
        var (both, bothRecord, _) = await AccountAsync(App, [AppRoles.Teacher, AppRoles.Methodist], "Двойная Роль Обезличенный");
        await AssignedAsync(methodist, bothRecord, ids);
        foreach (var client in new[] { author, stranger, methodist, both })
            Assert.Empty((await client.GetFromJsonAsync<List<DocumentListItem>>("/api/documents/assigned"))!);
        Assert.Contains((await both.GetFromJsonAsync<List<DocumentListItem>>("/api/documents"))!, d => d.Id == id);

        // Снятие закрепления закрывает доступ.
        var removed = AssigneeOf(await PageAsync(methodist), "Б1.О.10", holderRecord);
        await ExpectStatusAsync(HttpStatusCode.NoContent, methodist.DeleteAsync($"/api/discipline-assignments/{removed.Id}"));
        await ExpectStatusAsync(HttpStatusCode.Forbidden, holder.GetAsync(Url(id, "")));
        await ExpectStatusAsync(HttpStatusCode.Forbidden, holder.GetAsync(Url(id, "comments")));
        Assert.Empty((await holder.GetFromJsonAsync<List<DocumentListItem>>("/api/documents/assigned"))!);
    }

    [Fact]
    public async Task On_a_discipline_with_assignments_only_the_assigned_teachers_create_documents()
    {
        var (methodist, _) = await AccountAsync(App, AppRoles.Methodist);
        var (admin, _) = await AccountAsync(App, AppRoles.Admin);
        var (holder, holderRecord) = await AccountAsync(App, AppRoles.Teacher, "Аааа Закреплённый Обезличенный");
        var (second, secondRecord) = await AccountAsync(App, AppRoles.Teacher, "Бббб Закреплённый Обезличенный");
        var (stranger, _) = await AccountAsync(App, AppRoles.Teacher, "Посторонний Преподаватель Обезличенный");
        var template = await DocumentData.PublishedTemplateAsync(App);
        var ids = await DisciplinesAsync("Б1.О.03", "Б1.О.05", "Б1.О.08", "Б1.О.09", "Б1.О.10", "Б1.О.11");

        async Task<HttpResponseMessage> CreateAsync(HttpClient client, Guid discipline) =>
            await client.PostAsJsonAsync("/api/documents", new CreateDocumentRequest(template.Id, discipline));
        async Task<ProblemDetails> RefusedAsync(HttpClient client, Guid discipline)
        {
            var response = await CreateAsync(client, discipline);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<ProblemDetails>())!;
        }

        // Закреплений нет — как до A10: документ создаёт любой преподаватель.
        await ExpectStatusAsync(HttpStatusCode.Created, CreateAsync(stranger, ids[0]));

        // Дисциплина закреплена за одним: посторонний получает отказ доступа с названием закреплённого, закреплённый создаёт.
        await AssignedAsync(methodist, holderRecord, ids[1]);
        var refusal = await RefusedAsync(stranger, ids[1]);
        Assert.Equal("Дисциплина закреплена", refusal.Title);
        Assert.Equal($"Дисциплина закреплена за сотрудником: {holderRecord.ShortName} " +
                     "Документы по ней создают закреплённые преподаватели — обратитесь к методисту.", refusal.Detail);
        var made = await CreateAsync(holder, ids[1]);
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        var madeDocument = (await made.Content.ReadFromJsonAsync<DocumentDetails>())!;
        Assert.Equal(holderRecord.Id, madeDocument.Document.AuthorId);
        // Свой документ закреплённого — в основном списке, а не в «Документах моих дисциплин»: там только чужие.
        Assert.Contains((await holder.GetFromJsonAsync<List<DocumentListItem>>("/api/documents"))!, d => d.Id == madeDocument.Document.Id);
        Assert.DoesNotContain((await holder.GetFromJsonAsync<List<DocumentListItem>>("/api/documents/assigned"))!, d => d.Id == madeDocument.Document.Id);

        // Отказ доступа раньше сообщения «документ уже есть»: постороннему не рассказывают о чужом документе.
        Assert.Equal("Дисциплина закреплена", (await RefusedAsync(stranger, ids[1])).Title);
        var duplicate = await CreateAsync(holder, ids[1]);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(madeDocument.Document.Id, (await duplicate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("documentId").GetGuid());

        // Закреплённых двое: в отказе оба, по алфавиту; создаёт любой из них.
        await AssignedAsync(methodist, holderRecord, ids[2]);
        await AssignedAsync(methodist, secondRecord, ids[2]);
        Assert.StartsWith($"Дисциплина закреплена за сотрудниками: {holderRecord.ShortName}, {secondRecord.ShortName} ", (await RefusedAsync(stranger, ids[2])).Detail);
        await ExpectStatusAsync(HttpStatusCode.Created, CreateAsync(second, ids[2]));

        // Методист и администратор создают «за кафедру» по любой дисциплине: закрепления ограничивают преподавателей.
        await AssignedAsync(methodist, holderRecord, ids[3], ids[4]);
        await ExpectStatusAsync(HttpStatusCode.Created, CreateAsync(methodist, ids[3]));
        await ExpectStatusAsync(HttpStatusCode.Created, CreateAsync(admin, ids[4]));

        // Снятие закрепления возвращает прежнее поведение: у дисциплины закреплений не осталось — создаёт любой.
        await AssignedAsync(methodist, holderRecord, ids[5]);
        await ExpectStatusAsync(HttpStatusCode.Forbidden, CreateAsync(stranger, ids[5]));
        await ExpectStatusAsync(HttpStatusCode.NoContent,
            methodist.DeleteAsync($"/api/discipline-assignments/{AssigneeOf(await PageAsync(methodist), "Б1.О.11", holderRecord).Id}"));
        await ExpectStatusAsync(HttpStatusCode.Created, CreateAsync(stranger, ids[5]));
    }

    [Fact]
    public async Task The_discipline_choice_shows_who_a_discipline_is_assigned_to_and_who_may_create_a_document()
    {
        var (methodist, _) = await AccountAsync(App, AppRoles.Methodist);
        var (holder, holderRecord) = await AccountAsync(App, AppRoles.Teacher, "Аааа Закреплённый Обезличенный");
        var (second, secondRecord) = await AccountAsync(App, AppRoles.Teacher, "Бббб Закреплённый Обезличенный");
        var (stranger, _) = await AccountAsync(App, AppRoles.Teacher, "Посторонний Преподаватель Обезличенный");
        var template = await DocumentData.PublishedTemplateAsync(App);
        var own = await DocumentData.CreateAsync(App, stranger, template.Id, "Б1.О.03");       // закреплений нет, документ постороннего
        var taken = await DocumentData.CreateAsync(App, holder, template.Id, "Б1.О.05");       // закреплена за двумя, документ первого
        var ids = await DisciplinesAsync("Б1.О.03", "Б1.О.05", "Б1.О.08", "Б1.О.09");
        await AssignedAsync(methodist, holderRecord, ids[1]);
        await AssignedAsync(methodist, secondRecord, ids[1]);
        await AssignedAsync(methodist, holderRecord, ids[2]);                                  // закреплена, документа нет

        async Task<Dictionary<string, DisciplineOption>> ChoiceAsync(HttpClient client) =>
            (await client.GetFromJsonAsync<List<DisciplineOption>>($"/api/documents/disciplines?templateId={template.Id}"))!
                .ToDictionary(d => d.Code);

        // Посторонний: свой документ открыт; закреплённая дисциплина с чужим документом — не открыта и недоступна для создания.
        var forStranger = await ChoiceAsync(stranger);
        var mine = forStranger["Б1.О.03"];
        Assert.Equal((own.Document.Id, true, true), (mine.DocumentId, mine.CanOpen, mine.CanCreate));
        Assert.Null(mine.AssignedTo);
        var theirs = forStranger["Б1.О.05"];
        Assert.Equal((taken.Document.Id, holderRecord.ShortName, false, false, $"{holderRecord.ShortName}, {secondRecord.ShortName}"),
            (theirs.DocumentId, theirs.DocumentAuthor, theirs.CanOpen, theirs.CanCreate, theirs.AssignedTo));
        var assignedFree = forStranger["Б1.О.08"];
        Assert.Equal((false, holderRecord.ShortName), (assignedFree.CanCreate, assignedFree.AssignedTo));
        Assert.Null(assignedFree.DocumentId);
        // Свободная дисциплина — создаёт любой.
        var free = forStranger["Б1.О.09"];
        Assert.True(free.CanCreate);
        Assert.Null(free.AssignedTo);
        Assert.Null(free.DocumentId);

        // Закреплённый открывает документ дисциплины, созданный другим закреплённым, и создаёт по закреплённой дисциплине;
        // документ свободной дисциплины, чужой и не закреплённый за ним, не открывает.
        var forSecond = await ChoiceAsync(second);
        Assert.Equal((true, true), (forSecond["Б1.О.05"].CanOpen, forSecond["Б1.О.05"].CanCreate));
        Assert.False(forSecond["Б1.О.03"].CanOpen);
        Assert.True((await ChoiceAsync(holder))["Б1.О.08"].CanCreate);

        // Методист — по любой дисциплине.
        Assert.All((await ChoiceAsync(methodist)).Values, d => Assert.True(d.CanCreate));
    }

    [Fact]
    public async Task Transfer_assigns_the_new_author_only_when_the_discipline_has_assignments_and_an_assigned_former_author_keeps_the_access()
    {
        var (methodist, methodistRecord) = await AccountAsync(App, AppRoles.Methodist);
        var (author, _) = await AccountAsync(App, AppRoles.Teacher, "Автор Документа Обезличенный");
        var (next, nextRecord) = await AccountAsync(App, AppRoles.Teacher, "Новый Автор Обезличенный");
        var (holder, holderRecord) = await AccountAsync(App, AppRoles.Teacher, "Закреплённый Первый Обезличенный");
        var (previous, previousRecord) = await AccountAsync(App, AppRoles.Teacher, "Прежний Закреплённый Обезличенный");
        var template = await DocumentData.PublishedTemplateAsync(App);
        var ids = await DisciplinesAsync("Б1.О.03", "Б1.О.05", "Б1.О.08");

        async Task<int> TransferredAsync(DocumentDetails document, Employee to, HttpClient? by = null) =>
            await ExpectAsync((by ?? methodist).PostAsJsonAsync(Url(document, "transfer"),
                new TransferRequest((await DetailsAsync(methodist, document.Document.Id)).Document.Version, to.Id, Reason)), S.Draft);
        async Task<List<Guid>> HoldersAsync(string code) =>
            RowOf(await PageAsync(methodist), code).Assigned.Select(a => a.EmployeeId).Order().ToList();

        // (а) У дисциплины закреплений нет: передача ничего не закрепляет.
        var free = await DocumentData.CreateAsync(App, author, template.Id, "Б1.О.03");
        await TransferredAsync(free, nextRecord);
        Assert.Empty(await HoldersAsync("Б1.О.03"));

        // (б) Закреплена за другим: новый автор закрепляется, в аудите — документ, из-за которого это случилось; уведомления об
        // этом нет (он получает «Вам передан документ»). Прежний автор не закреплён — документ ему закрыт.
        var taken = await DocumentData.CreateAsync(App, author, template.Id, "Б1.О.05");
        await AssignedAsync(methodist, holderRecord, ids[1]);
        await TransferredAsync(taken, nextRecord);
        Assert.Equal(new[] { holderRecord.Id, nextRecord.Id }.Order(), await HoldersAsync("Б1.О.05"));
        Assert.Equal(methodistRecord.ShortName, AssigneeOf(await PageAsync(methodist), "Б1.О.05", nextRecord).AssignedBy);
        await App.WithDbAsync(async db =>
        {
            var audit = (await db.AuditLog.Where(a => a.EventType == AuditEvents.DisciplineAssigned && a.EntityId == ids[1]).ToListAsync())
                .Select(a => JsonNode.Parse(a.Payload)!.AsObject()).Single(p => Guid.Parse((string)p["employee"]!) == nextRecord.Id);
            Assert.Equal(taken.Document.Id, Guid.Parse((string)audit["document"]!));
        });
        Assert.Empty(await FeedAsync(next, NotificationEvents.DisciplineAssigned));
        Assert.Single(await FeedAsync(next, NotificationEvents.TransferredIn), n => n.DocumentId == taken.Document.Id);
        await ExpectStatusAsync(HttpStatusCode.Forbidden, author.GetAsync(Url(taken, "")));

        // Тот, кто уже закреплён, не дублируется: возврат документа первому закреплённому закреплений не добавляет.
        await TransferredAsync(taken, holderRecord);
        Assert.Equal(new[] { holderRecord.Id, nextRecord.Id }.Order(), await HoldersAsync("Б1.О.05"));

        // (в) Прежний автор закреплён за дисциплиной: после передачи документ ему открыт для чтения, а уведомление — со ссылкой.
        await AssignedAsync(methodist, previousRecord, ids[2]);
        var kept = await DocumentData.CreateAsync(App, previous, template.Id, "Б1.О.08");
        await TransferredAsync(kept, nextRecord);
        Assert.Equal(new[] { previousRecord.Id, nextRecord.Id }.Order(), await HoldersAsync("Б1.О.08"));
        var seen = await DetailsAsync(previous, kept.Document.Id);
        Assert.Equal((nextRecord.Id, false), (seen.Document.AuthorId, seen.Editable));
        var out1 = Assert.Single(await FeedAsync(previous, NotificationEvents.TransferredOut));
        Assert.Equal(kept.Document.Id, out1.DocumentId);
        Assert.Contains(kept.Document.Id, (await previous.GetFromJsonAsync<List<DocumentListItem>>("/api/documents/assigned"))!.Select(d => d.Id));
    }
}
