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
/// «Создать на основе» и признак «устарел» (этап 7, A11; решения 3 и 4): документ для дисциплины другого плана или для той же
/// дисциплины с переносом ввода автора, правило «у дисциплины с согласованным документом новый создаётся только на его основе»,
/// доступ к основе и пометка основы «устарел», когда согласован её преемник. Закрепления тесты убирают за собой
/// (<see cref="AssignmentTestBase"/>).
/// </summary>
[Collection(DatabaseCollection.Name)]
public class DocumentBasedOnApiTests(DatabaseFixture fixture) : AssignmentTestBase(fixture)
{
    [Fact]
    public async Task A_document_for_a_discipline_of_another_plan_carries_the_authors_input_takes_plan_data_anew_and_lists_the_rest()
    {
        var (author, authorRecord) = await AccountAsync(App, AppRoles.Teacher);
        var (methodist, _) = await AccountAsync(App, AppRoles.Methodist);
        var source = await DocumentData.CreateAsync(App, author, (await DocumentData.PublishedTemplateAsync(App)).Id, "Б1.О.10");
        var version = await DocumentData.FillForSubmissionAsync(author, source);

        // К индикатору первой компетенции автор добавил второй — такого в справочнике нет.
        var chosen = JsonNode.Parse((await DetailsAsync(author, source.Document.Id)).Payload.GetProperty("competences").GetRawText())!.AsArray();
        Assert.True(chosen.Count > 1, "нужны хотя бы две компетенции в матрице дисциплины");
        var keptCode = (string)chosen[0]!["code"]!;
        var bogus = $"{keptCode}.99";
        chosen[0]!["indicators"]!.AsArray().Add(new JsonObject { ["code"] = bogus, ["text"] = "Индикатора в справочнике нет" });
        version = (await DocumentData.PatchAsync(author, source.Document.Id, "competences", version, chosen)).Version;
        var original = await DetailsAsync(author, source.Document.Id);

        // Цель — дисциплина другого плана, в её матрице только первая компетенция.
        var (otherTemplate, other) = await OtherPlanAsync("Б1.О.10", keep: [keptCode]);
        var created = await CreatedAsync(author, otherTemplate.Id, other.Id, source.Document.Id);
        var before = JsonNode.Parse(original.Payload.GetRawText())!;
        var payload = JsonNode.Parse(created.Payload.GetRawText())!;

        // Ввод автора перенесён как есть: цели, тематический план, основная литература (пустое — не ввод). Стандартные пункты вуза
        // (интернет-ресурсы, программное обеспечение) основа не вводила — они в новом документе по умолчанию шаблона.
        AssertSame(before, payload, "goals_and_objectives");
        AssertSame(before, payload, "thematic_plan");
        AssertSame(before, payload, "literature", "main");

        // Поля из плана и справочников — по цели, а не по основе: название, направление, компетенции из её матрицы.
        Assert.Equal((S.Draft, other.Id, authorRecord.Id), (created.Document.Status, created.Document.DisciplineId, created.Document.AuthorId));
        Assert.Equal("Дисциплина другого плана", (string)payload["title_page"]!["discipline_name"]!);
        Assert.Equal(await DirectionCodeAsync(other), (string)payload["title_page"]!["direction_code"]!);
        Assert.NotEqual((string)before["title_page"]!["direction_code"]!, (string)payload["title_page"]!["direction_code"]!);
        var competence = Assert.Single(payload["competences"]!.AsArray())!;
        Assert.Equal(keptCode, (string)competence["code"]!);
        // Индикатор с парой остался (по коду); тот, которого нет в справочнике, — нет.
        var indicator = Assert.Single(competence["indicators"]!.AsArray())!;
        Assert.Equal((string)chosen[0]!["indicators"]![0]!["code"]!, (string)indicator["code"]!);

        // Список неперенесённого: индикатор без пары и индикаторы компетенций, которых нет в матрице цели.
        var notes = created.BasedOn!.NotTransferred;
        Assert.Contains(notes, n => n.Contains($"индикатор {bogus} не перенесён") && n.Contains("нет такого индикатора"));
        foreach (var dropped in chosen.Skip(1))
            foreach (var code in dropped!["indicators"]!.AsArray().Select(i => (string)i!["code"]!))
                Assert.Contains(notes, n => n.Contains($"индикатор {code} не перенесён") && n.Contains($"компетенции {(string)dropped["code"]!} нет в матрице"));
        Assert.Equal((source.Document.Id, original.Document.Title, S.Draft, true),
            (created.BasedOn.Id, created.BasedOn.Title, created.BasedOn.Status, created.BasedOn.CanOpen));

        // Связь и список — в базе и в журнале аудита; редактор берёт плашку оттуда же при каждом открытии, любому, кто видит документ.
        await App.WithDbAsync(async db =>
        {
            var document = await db.Documents.SingleAsync(d => d.Id == created.Document.Id);
            Assert.Equal(source.Document.Id, document.BasedOnDocumentId);
            var audit = await db.AuditLog.SingleAsync(a => a.EntityId == created.Document.Id && a.EventType == AuditEvents.DocumentBasedOn);
            Assert.Equal((authorRecord.Id, AuditEntities.Document), (audit.UserId, audit.EntityType));
            var logged = JsonDocument.Parse(audit.Payload).RootElement;
            Assert.Equal(source.Document.Id.ToString(), logged.GetProperty("base").GetString());
            Assert.Equal(notes, logged.GetProperty("not_transferred").EnumerateArray().Select(n => n.GetString()!));
        });
        foreach (var client in new[] { author, methodist })
        {
            var seen = (await DetailsAsync(client, created.Document.Id)).BasedOn!;
            Assert.Equal(notes, seen.NotTransferred);
            Assert.True(seen.CanOpen);
        }

        // Основа не тронута: версия, статус, признак.
        var after = await DetailsAsync(author, source.Document.Id);
        Assert.Equal((original.Document.Version, S.Draft, false, (DocumentBasis?)null), (after.Document.Version, after.Document.Status, after.Document.IsOutdated, after.BasedOn));
    }

    [Fact]
    public async Task The_next_edition_of_the_same_discipline_is_created_only_on_the_approved_document_and_marks_it_outdated_when_approved()
    {
        var c = await ApprovedAsync(App, "Б1.О.12");
        var (baseId, templateId, disciplineId) = (c.Created.Document.Id, c.Created.Document.TemplateId, c.Created.Document.DisciplineId);

        // Без основы дисциплину занимает согласованный документ: 409 со ссылкой на него и подсказкой.
        var blocked = await RefusedAsync(c.Author, HttpStatusCode.Conflict, templateId, disciplineId, null);
        Assert.Equal(baseId.ToString(), blocked.Extensions["documentId"]!.ToString());
        Assert.Contains("на его основе", blocked.Detail);
        // Экран выбора дисциплин показывает его статус — по нему выбирается кнопка «Создать на основе».
        var option = await OptionAsync(c.Author, templateId, disciplineId);
        Assert.Equal((baseId, S.Approved), (option.DocumentId, option.DocumentStatus));

        // На основе согласованного — создаётся: второй документ той же дисциплины и линии шаблона; ввод автора перенесён.
        var created = await CreatedAsync(c.Author, templateId, disciplineId, baseId);
        Assert.Equal((S.Draft, c.AuthorRecord.Id, disciplineId, baseId), (created.Document.Status, created.Document.AuthorId, created.Document.DisciplineId, created.BasedOn!.Id));
        var original = await DetailsAsync(c.Author, baseId);
        var (before, payload) = (JsonNode.Parse(original.Payload.GetRawText())!, JsonNode.Parse(created.Payload.GetRawText())!);
        foreach (var path in new[] { new[] { "goals_and_objectives" }, ["thematic_plan"], ["competences"], ["literature", "main"], ["hours_distribution", "self_study_kinds"] })
            AssertSame(before, payload, path);
        Assert.Empty(created.BasedOn.NotTransferred);
        var occupied = await OptionAsync(c.Author, templateId, disciplineId);
        Assert.Equal((created.Document.Id, S.Draft), (occupied.DocumentId, occupied.DocumentStatus));

        // Незавершённый документ дисциплину занимает: второй на основе того же — 409 со ссылкой на первый; возврат основы на
        // исправление — тоже 409 (один незавершённый документ на дисциплину, A8).
        var again = await RefusedAsync(c.Author, HttpStatusCode.Conflict, templateId, disciplineId, baseId);
        Assert.Equal(created.Document.Id.ToString(), again.Extensions["documentId"]!.ToString());
        var back = await c.Methodist.PostAsJsonAsync(Url(baseId, "return-for-correction"), new CorrectionRequest(original.Document.Version, "Исправить"));
        Assert.Equal(HttpStatusCode.Conflict, back.StatusCode);
        Assert.Equal("Есть незавершённый документ", (await back.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
        Assert.False((await DetailsAsync(c.Author, baseId)).Document.IsOutdated);

        // Преемник согласован — основа устарела; статус, версия и время изменения основы прежние.
        await ApproveAsync(c, created);
        var outdated = await DetailsAsync(c.Author, baseId);
        Assert.Equal((S.Approved, true, original.Document.Version, original.Document.UpdatedAt),
            (outdated.Document.Status, outdated.Document.IsOutdated, outdated.Document.Version, outdated.Document.UpdatedAt));
        Assert.False((await DetailsAsync(c.Author, created.Document.Id)).Document.IsOutdated);
        Assert.Contains((await c.Author.GetFromJsonAsync<List<DocumentListItem>>("/api/documents"))!, d => d.Id == baseId && d.IsOutdated);
        await App.WithDbAsync(async db =>
        {
            var audit = await db.AuditLog.SingleAsync(a => a.EntityId == baseId && a.EventType == AuditEvents.DocumentOutdated);
            Assert.Equal((c.UmuRecord.Id, AuditEntities.Document), (audit.UserId, audit.EntityType));
            Assert.Equal(created.Document.Id.ToString(), JsonDocument.Parse(audit.Payload).RootElement.GetProperty("successor").GetString());
        });

        // В архив основу отправляют явно; признак остаётся, а новый документ по той же дисциплине — только на согласованном.
        await ExpectAsync(c.Author.PostAsJsonAsync(Url(baseId, "archive"), new DocumentVersionRequest(outdated.Document.Version)), S.Archived);
        Assert.True((await DetailsAsync(c.Author, baseId)).Document.IsOutdated);
        await RefusedAsync(c.Author, HttpStatusCode.Conflict, templateId, disciplineId, baseId);
        var third = await CreatedAsync(c.Author, templateId, disciplineId, created.Document.Id);
        Assert.Equal(created.Document.Id, third.BasedOn!.Id);
    }

    [Fact]
    public async Task A_successor_approved_at_once_by_a_sheet_without_electronic_seats_marks_the_base_outdated_too()
    {
        // Лист согласования без электронных мест: приём сразу даёт «Согласован» — вторая дорога к согласованному документу.
        var (author, _) = await AccountAsync(App, AppRoles.Teacher);
        var (methodist, _) = await AccountAsync(App, AppRoles.Methodist);
        var template = await DocumentData.PublishedTemplateAsync(App, electronicSeats: []);
        var first = await DocumentData.CreateAsync(App, author, template.Id, "Б1.О.10");
        await AcceptedAtOnceAsync(first);
        var created = await CreatedAsync(author, template.Id, first.Document.DisciplineId, first.Document.Id);
        await AcceptedAtOnceAsync(created);

        var base0 = await DetailsAsync(author, first.Document.Id);
        Assert.Equal((S.Approved, true), (base0.Document.Status, base0.Document.IsOutdated));
        Assert.False((await DetailsAsync(author, created.Document.Id)).Document.IsOutdated);

        async Task AcceptedAtOnceAsync(DocumentDetails document)
        {
            var version = await DocumentData.FillForSubmissionAsync(author, document);
            version = await ExpectAsync(author.PostAsJsonAsync(Url(document, "submit"), new DocumentVersionRequest(version)), S.UnderReview);
            await ExpectAsync(methodist.PostAsJsonAsync(Url(document, "accept"), new DocumentVersionRequest(version)), S.Approved);
        }
    }

    [Fact]
    public async Task A_successor_for_a_discipline_of_another_plan_does_not_make_the_base_outdated()
    {
        var c = await ApprovedAsync(App, "Б1.О.13");
        var (otherTemplate, other) = await OtherPlanAsync("Б1.О.13");
        var before = await DetailsAsync(c.Author, c.Created.Document.Id);

        // Основа согласована, цель — дисциплина другого плана: она свободна, документ создаётся и проходит круг согласования.
        var created = await CreatedAsync(c.Author, otherTemplate.Id, other.Id, c.Created.Document.Id);
        Assert.Equal(other.Id, created.Document.DisciplineId);
        await ApproveAsync(c, created);

        // Это другой документ, а не следующая редакция прежнего: основа не устарела и не изменилась.
        var after = await DetailsAsync(c.Author, c.Created.Document.Id);
        Assert.Equal((S.Approved, false, before.Document.Version), (after.Document.Status, after.Document.IsOutdated, after.Document.Version));
        await App.WithDbAsync(async db =>
            Assert.Equal(0, await db.AuditLog.CountAsync(a => a.EntityId == c.Created.Document.Id && a.EventType == AuditEvents.DocumentOutdated)));
    }

    [Fact]
    public async Task A_discipline_with_an_approved_document_takes_a_new_one_only_on_its_basis_and_not_on_a_document_of_another_plan()
    {
        var c = await ApprovedAsync(App, "Б1.О.05");
        var (templateId, disciplineId) = (c.Created.Document.TemplateId, c.Created.Document.DisciplineId);

        // Чужая основа — документ другого плана: у дисциплины с согласованным документом новый на ней не создать.
        var (otherTemplate, other) = await OtherPlanAsync("Б1.О.05");
        var foreign = await CreatedAsync(c.Author, otherTemplate.Id, other.Id, c.Created.Document.Id);
        var refused = await RefusedAsync(c.Author, HttpStatusCode.Conflict, templateId, disciplineId, foreign.Document.Id);
        Assert.Equal(c.Created.Document.Id.ToString(), refused.Extensions["documentId"]!.ToString());
        Assert.Contains("на его основе", refused.Detail);
        // Дисциплина другого плана уже занята незавершённым документом: и в ней второй — 409.
        await RefusedAsync(c.Author, HttpStatusCode.Conflict, otherTemplate.Id, other.Id, c.Created.Document.Id);
    }

    [Fact]
    public async Task A_base_is_the_authors_own_document_a_document_of_an_assigned_discipline_or_any_document_for_a_methodist_and_admin()
    {
        var (author, _) = await AccountAsync(App, AppRoles.Teacher);
        var (stranger, strangerRecord) = await AccountAsync(App, AppRoles.Teacher);
        var (methodist, _) = await AccountAsync(App, AppRoles.Methodist);
        var (admin, _) = await AccountAsync(App, AppRoles.Admin);
        var source = await DocumentData.CreateAsync(App, author, (await DocumentData.PublishedTemplateAsync(App)).Id, "Б1.О.10");
        var (otherTemplate, other) = await OtherPlanAsync("Б1.О.10");
        var ids = await DisciplinesAsync("Б1.О.10");

        // Чужой документ посторонний преподаватель основой не берёт — 403, и документ дисциплины ему не открыт.
        var refused = await RefusedAsync(stranger, HttpStatusCode.Forbidden, otherTemplate.Id, other.Id, source.Document.Id);
        Assert.Equal("Основа недоступна", refused.Title);

        // Закреплённый за дисциплиной основы берёт её документ основой для своей новой дисциплины (A10 + A11); без закрепления — нет.
        await AssignedAsync(methodist, strangerRecord, ids);
        var created = await CreatedAsync(stranger, otherTemplate.Id, other.Id, source.Document.Id);
        Assert.Equal((strangerRecord.Id, source.Document.Id), (created.Document.AuthorId, created.BasedOn!.Id));
        Assert.True(created.BasedOn.CanOpen);
        await ExpectStatusAsync(HttpStatusCode.NoContent, methodist.DeleteAsync(
            $"/api/discipline-assignments/{AssigneeOf(await PageAsync(methodist), "Б1.О.10", strangerRecord).Id}"));
        // Основа закрыта для него снова: плашка нового документа называет её, но ссылки на неё не даёт.
        Assert.False((await DetailsAsync(stranger, created.Document.Id)).BasedOn!.CanOpen);
        var (secondTemplate, second) = await OtherPlanAsync("Б1.О.10");
        await RefusedAsync(stranger, HttpStatusCode.Forbidden, secondTemplate.Id, second.Id, source.Document.Id);

        // Методист и администратор видят все документы: любой подходит им в основы, они создают «за кафедру».
        foreach (var client in new[] { methodist, admin })
        {
            var (template, discipline) = await OtherPlanAsync("Б1.О.10");
            Assert.Equal(source.Document.Id, (await CreatedAsync(client, template.Id, discipline.Id, source.Document.Id)).BasedOn!.Id);
        }
    }

    [Fact]
    public async Task A_base_that_does_not_exist_is_deleted_of_another_type_or_of_another_discipline_of_the_same_plan_is_refused_with_422()
    {
        var (author, _) = await AccountAsync(App, AppRoles.Teacher);
        var template = await DocumentData.PublishedTemplateAsync(App);
        var source = await DocumentData.CreateAsync(App, author, template.Id, "Б1.О.10");
        var draft = await DocumentData.CreateAsync(App, author, template.Id, "Б1.О.03");
        var (otherTemplate, other) = await OtherPlanAsync("Б1.О.10");

        // Нет такого документа; удалённый документ — тоже нет (удаляет автор свой черновик).
        Assert.Equal("Основа не найдена", (await RefusedAsync(author, HttpStatusCode.UnprocessableEntity, otherTemplate.Id, other.Id, Guid.NewGuid())).Title);
        Assert.Equal(HttpStatusCode.NoContent, (await author.DeleteAsync($"/api/documents/{draft.Document.Id}?version={draft.Document.Version}")).StatusCode);
        Assert.Equal("Основа не найдена", (await RefusedAsync(author, HttpStatusCode.UnprocessableEntity, otherTemplate.Id, other.Id, draft.Document.Id)).Title);

        // Другая дисциплина того же плана: на основе создают для той же дисциплины или для дисциплины другого плана.
        var sameDiscipline = await DocumentData.DisciplineAsync(App, "Б1.О.05");
        var samePlan = await RefusedAsync(author, HttpStatusCode.UnprocessableEntity, template.Id, sameDiscipline, source.Document.Id);
        Assert.Equal("Основа из того же плана", samePlan.Title);

        // Основа другого типа: РПД создаётся на основе РПД.
        await App.WithDbAsync(async db => await db.Documents.Where(d => d.Id == source.Document.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Type, "fos")));
        var otherType = await RefusedAsync(author, HttpStatusCode.UnprocessableEntity, otherTemplate.Id, other.Id, source.Document.Id);
        Assert.Equal("Основа другого типа", otherType.Title);

        // Ничего не создано и не записано.
        await App.WithDbAsync(async db =>
        {
            Assert.Equal(0, await db.Documents.CountAsync(d => d.DisciplineId == other.Id));
            Assert.Equal(0, await db.AuditLog.CountAsync(a => a.EventType == AuditEvents.DocumentBasedOn && a.UserId == source.Document.AuthorId));
        });
    }

    [Fact]
    public async Task Create_based_on_is_offered_in_approved_and_archived_documents_to_the_author_corrector_and_assigned_but_not_to_an_approver()
    {
        var c = await ApprovedAsync(App, "Б1.О.03");
        var id = c.Created.Document.Id;
        var ids = await DisciplinesAsync("Б1.О.03");
        var (colleague, colleagueRecord) = await AccountAsync(App, AppRoles.Teacher);

        // Согласован: автор, методисты и администратор — да; согласующий и посторонний (его не пускают в документ) — нет.
        foreach (var client in new[] { c.Author, c.Methodist, c.OtherMethodist, c.Admin })
            Assert.True((await DetailsAsync(client, id)).Actions.CreateBasedOn);
        Assert.False((await DetailsAsync(c.Head, id)).Actions.CreateBasedOn);
        Assert.Equal(HttpStatusCode.Forbidden, (await colleague.GetAsync(Url(id, ""))).StatusCode);

        // Закреплённый за дисциплиной — да, как и «берёт её документы основой» (A10); согласующий, которого закрепили, — тоже.
        await AssignedAsync(c.Methodist, colleagueRecord, ids);
        Assert.True((await DetailsAsync(colleague, id)).Actions.CreateBasedOn);

        // Архивный — те же; в других статусах кнопки нет (основой при создании служит документ любого статуса, но не из документа).
        await ExpectAsync(c.Author.PostAsJsonAsync(Url(id, "archive"), new DocumentVersionRequest(c.Version)), S.Archived);
        foreach (var client in new[] { c.Author, c.Methodist, c.Admin, colleague })
            Assert.True((await DetailsAsync(client, id)).Actions.CreateBasedOn);
        Assert.False((await DetailsAsync(c.Head, id)).Actions.CreateBasedOn);
        foreach (var status in new[] { S.Draft, S.SavedDraft, S.UnderReview, S.Returned, S.Accepted, S.Approval })
        {
            await SetStatusAsync(App, id, status);
            Assert.False((await DetailsAsync(c.Author, id)).Actions.CreateBasedOn);
        }
    }

    [Fact]
    public async Task Marking_outdated_keeps_the_version_of_the_document_but_any_other_change_still_raises_it()
    {
        var (author, _) = await AccountAsync(App, AppRoles.Teacher);
        var created = await DocumentData.CreateAsync(App, author, (await DocumentData.PublishedTemplateAsync(App)).Id, "Б1.О.05");
        // Исходное состояние — из базы: время в ответе создания не округлено до микросекунд, как в timestamptz.
        var (version, updatedAt) = (0, default(DateTimeOffset));
        await App.WithDbAsync(async db =>
        {
            var document = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == created.Document.Id);
            (version, updatedAt) = (document.Version, document.UpdatedAt);
        });

        await App.WithDbAsync(async db =>
        {
            var document = await db.Documents.SingleAsync(d => d.Id == created.Document.Id);
            document.IsOutdated = true;
            await db.SaveChangesAsync();
        });
        await App.WithDbAsync(async db =>
        {
            var document = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == created.Document.Id);
            Assert.Equal((true, version, updatedAt), (document.IsOutdated, document.Version, document.UpdatedAt));
        });

        // Признак вместе с другой правкой — обычное изменение: версия растёт.
        await App.WithDbAsync(async db =>
        {
            var document = await db.Documents.SingleAsync(d => d.Id == created.Document.Id);
            document.IsOutdated = false;
            document.Title = "Другое название";
            await db.SaveChangesAsync();
            Assert.Equal(version + 1, document.Version);
        });
    }

    // ───── вспомогательное ─────

    /// <summary>Место содержимого (JSON-путь по именам) в двух документах одинаково.</summary>
    private static void AssertSame(JsonNode before, JsonNode after, params string[] path)
    {
        JsonNode? a = before, b = after;
        foreach (var segment in path) (a, b) = (a?[segment], b?[segment]);
        Assert.True(JsonNode.DeepEquals(a, b), string.Join("/", path));
    }

    /// <summary>Согласие всех согласующих круга: заполнение, отправка, приём и отметки обоих электронных мест.</summary>
    private static async Task ApproveAsync(DocumentCircle c, DocumentDetails created)
    {
        var version = await DocumentData.FillForSubmissionAsync(c.Author, created);
        version = await ExpectAsync(c.Author.PostAsJsonAsync(Url(created, "submit"), new DocumentVersionRequest(version)), S.UnderReview);
        await ExpectAsync(c.Methodist.PostAsJsonAsync(Url(created, "accept"), new DocumentVersionRequest(version)), S.Approval);
        await ExpectAsync(c.Head.PostAsync(Url(created, "approval/approve"), null), S.Approval);
        await ExpectAsync(c.Umu.PostAsync(Url(created, "approval/approve"), null), S.Approved);
    }

    private static async Task<DocumentDetails> CreatedAsync(HttpClient client, Guid templateId, Guid disciplineId, Guid basedOn)
    {
        var response = await client.PostAsJsonAsync("/api/documents", new CreateDocumentRequest(templateId, disciplineId, basedOn));
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<DocumentDetails>())!;
    }

    private static async Task<ProblemDetails> RefusedAsync(
        HttpClient client, HttpStatusCode status, Guid templateId, Guid disciplineId, Guid? basedOn)
    {
        var response = await client.PostAsJsonAsync("/api/documents", new CreateDocumentRequest(templateId, disciplineId, basedOn));
        Assert.True(response.StatusCode == status, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ProblemDetails>())!;
    }

    private static async Task<DisciplineOption> OptionAsync(HttpClient client, Guid templateId, Guid disciplineId) =>
        (await client.GetFromJsonAsync<List<DisciplineOption>>($"/api/documents/disciplines?templateId={templateId}"))!
            .Single(o => o.Id == disciplineId);

    private async Task<string> DirectionCodeAsync(Discipline discipline)
    {
        var code = "";
        await App.WithDbAsync(async db => code = await (
                from p in db.CurriculumPlans
                join d in db.StudyDirections on p.DirectionId equals d.Id
                where p.Id == discipline.CurriculumPlanId
                select d.Code)
            .SingleAsync());
        return code;
    }

    /// <summary>
    /// Второй учебный план — со своим направлением (код в обычном виде, чтобы документ проходил проверку), дисциплиной с теми же
    /// часами и названием «Дисциплина другого плана», матрицей из компетенций исходной дисциплины (все или названные) и
    /// опубликованным шаблоном РПД. Кафедра — та же: подписанты документов находятся по ней.
    /// </summary>
    private async Task<(DocumentTemplate Template, Discipline Discipline)> OtherPlanAsync(string code, string[]? keep = null)
    {
        var planId = await App.RealPlanIdAsync();
        DocumentTemplate? template = null;
        Discipline? discipline = null;
        await App.WithDbAsync(async db =>
        {
            var source = await db.Disciplines.AsNoTracking().Include(d => d.Semesters)
                .SingleAsync(d => d.CurriculumPlanId == planId && d.Code == code);
            var direction = new StudyDirection
            {
                Code = $"{Random.Shared.Next(50, 100)}.{Random.Shared.Next(10, 100)}.{Random.Shared.Next(10, 100)}",
                Name = $"Тестовое направление {Guid.NewGuid()}", EducationLevel = EducationLevels.Master
            };
            var profile = new StudyProfile { DirectionId = direction.Id, Name = "Тестовый профиль" };
            var plan = new CurriculumPlan { DirectionId = direction.Id, ProfileId = profile.Id, Year = 2026, CreatedAt = DateTimeOffset.UtcNow };
            discipline = new Discipline
            {
                CurriculumPlanId = plan.Id, DepartmentId = source.DepartmentId, Code = source.Code, Name = "Дисциплина другого плана",
                Block = source.Block, PartType = source.PartType, EntryKind = source.EntryKind, TotalHours = source.TotalHours,
                TotalCredits = source.TotalCredits,
                Semesters =
                [
                    .. source.Semesters.Select(s => new DisciplineSemesterDetails
                    {
                        Semester = s.Semester, ContactHours = s.ContactHours, Lectures = s.Lectures, Practice = s.Practice,
                        LabHours = s.LabHours, SelfStudy = s.SelfStudy, Control = s.Control, Credits = s.Credits,
                        ControlTypes = [.. s.ControlTypes]
                    })
                ]
            };
            db.AddRange(direction, profile, plan, discipline);
            var matrix = await (
                    from m in db.CompetenceMatrix
                    join c in db.Competences on m.CompetenceId equals c.Id
                    where m.DisciplineId == source.Id && (keep == null || keep.Contains(c.Code))
                    select m.CompetenceId)
                .ToListAsync();
            db.CompetenceMatrix.AddRange(matrix.Select(id => new CompetenceMatrixEntry { DisciplineId = discipline.Id, CompetenceId = id }));
            await db.SaveChangesAsync();
            template = await TemplateData.AddPublishedAsync(db, plan.Id);
        });
        return (template!, discipline!);
    }
}
