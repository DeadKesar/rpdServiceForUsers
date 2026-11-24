using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RpdFos.Api.Features.Documents;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;
using RpdFos.Core.DocumentTypes;
using Xunit;
using static RpdFos.Api.Tests.Integration.DocumentCircles;
using S = RpdFos.Core.Documents.DocumentStatuses;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// Создание ФОС и связь с РПД (этап 7, B3; FR-DOC-19, решения 5 и 8): ФОС создаёт автор РПД или закреплённый за дисциплиной по РПД
/// в любом статусе, кроме удалённой и архивной; наследуемое от РПД — снимок; пометка «РПД изменилась» следует за данными, а не
/// за версией; «Обновить из РПД» не трогает паспорт; автор ФОС читает связанную РПД, не правя. Закрепления тесты убирают за собой
/// (<see cref="AssignmentTestBase"/>). Изолированный план — для проверок, где РПД дисциплины в общей базе не должны смешиваться
/// с РПД других тестов.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class FosCreationApiTests(DatabaseFixture fixture) : AssignmentTestBase(fixture)
{
    [Fact]
    public async Task The_rpd_author_creates_a_fos_with_the_title_from_the_plan_and_a_snapshot_of_the_rpd()
    {
        var (author, authorRecord) = await AccountAsync(App, AppRoles.Teacher);
        var rpd = await FilledRpdAsync(author, (await DocumentData.PublishedTemplateAsync(App)).Id, "Б1.О.11");
        Assert.True(rpd.Actions.CreateFos);
        Assert.Empty(rpd.LinkedFos!);
        var fosTemplate = await FosTemplateAsync();

        var fos = await CreatedAsync(author, fosTemplate.Id, rpd.Document.DisciplineId, rpd.Document.Id);

        Assert.Equal(("fos", S.Draft, authorRecord.Id, rpd.Document.DisciplineId),
            (fos.Document.Type, fos.Document.Status, fos.Document.AuthorId, fos.Document.DisciplineId));
        Assert.StartsWith("ФОС «", fos.Document.Title);
        var payload = JsonNode.Parse(fos.Payload.GetRawText())!;
        var source = JsonNode.Parse(rpd.Payload.GetRawText())!;

        // Титул — из плана, как у РПД; составитель — автор ФОС.
        Assert.Equal((string)source["title_page"]!["discipline_name"]!, (string)payload["title_page"]!["discipline_name"]!);
        Assert.Equal((string)source["title_page"]!["direction_code"]!, (string)payload["title_page"]!["direction_code"]!);
        Assert.Equal(authorRecord.Id.ToString(), (string)payload["approval_sheet"]!["developers"]![0]!["user_id"]!);

        // Снимок: РПД и её версия, компетенции с индикаторами, разделы тематического плана, виды аттестации по семестрам.
        var link = payload["linked_rpd"]!;
        Assert.Equal((rpd.Document.Id.ToString(), rpd.Document.Version), ((string)link["rpd_document_id"]!, (int)link["rpd_version"]!));
        Assert.NotNull(link["snapshot_at"]);
        var chosen = source["competences"]!.AsArray();
        Assert.Equal(chosen.Select(c => (string)c!["code"]!), link["competences"]!.AsArray().Select(c => (string)c!["code"]!));
        var indicators = chosen.SelectMany(c => c!["indicators"]!.AsArray()).Select(i => (string)i!["code"]!).ToList();
        Assert.Equal(indicators, payload["competence_indicators"]!.AsArray().Select(i => (string)i!["code"]!));

        var topics = source["thematic_plan"]!.AsArray();
        var sections = link["sections"]!.AsArray();
        Assert.Equal(topics.Count, sections.Count);
        var first = sections[0]!;
        // Название раздела в снимке — наименование раздела РПД (этап 8, A12), а не его темы.
        Assert.Equal((1, "Раздел 1. Основы моделирования"), ((int)first["number"]!, (string)first["title"]!));
        Assert.Equal(new[] { (string)topics[0]!["competence_indicators"]![0]! }, Strings(first["indicator_codes"]));
        Assert.Equal(new[] { (string)chosen[0]!["code"]! }, Strings(first["competence_codes"]));
        Assert.Equal(new[] { "Контрольная работа" }, Strings(first["current_control_forms"]));
        Assert.Equal(new[] { (int)topics[0]!["semester"]! }, first["semesters"]!.AsArray().Select(s => (int)s!));

        var semesters = source["hours_distribution"]!["semesters"]!.AsArray();
        Assert.Equal(semesters.Select(s => (int)s!["semester"]!), link["semesters"]!.AsArray().Select(s => (int)s!["number"]!));
        // Виды аттестации семестра — те же, что в РПД (порядок в снимке канонический, поэтому сравниваем наборы).
        Assert.Equal(semesters.Select(s => string.Join(',', Strings(s!["control_types"]).Order())),
            link["semesters"]!.AsArray().Select(s => string.Join(',', Strings(s!["control_types"]).Order())));

        // Снимок только для чтения и не «поле из плана»: ни метки, ни предупреждения «отличается от учебного плана».
        Assert.Contains("/linked_rpd", fos.Metadata.ReadOnlyPaths);
        Assert.Contains("/competence_indicators", fos.Metadata.ReadOnlyPaths);
        Assert.DoesNotContain(fos.Metadata.PlanValues.Keys, k => k.StartsWith("/linked_rpd") || k.StartsWith("/competence_indicators"));
        Assert.DoesNotContain(fos.Metadata.Sections.SelectMany(s => s.Warnings), w => w.Message.StartsWith(PlanFields.DiffersFromPlan));

        // Связь в ответе ФОС и РПД: ссылка, статус, пометки нет; у РПД — её ФОС, а новый создать уже нельзя.
        var linked = fos.LinkedRpd!;
        Assert.Equal((rpd.Document.Id, S.Draft, true, false, false, rpd.Document.Version),
            (linked.Id, linked.Status, linked.CanOpen, linked.Changed, linked.Deleted, linked.SnapshotVersion));
        var after = await DetailsAsync(author, rpd.Document.Id);
        var listed = Assert.Single(after.LinkedFos!);
        Assert.Equal((fos.Document.Id, S.Draft, true), (listed.Id, listed.Status, listed.CanOpen));
        Assert.False(after.Actions.CreateFos);

        // Нормализованные таблицы: часов у ФОС нет, связи с индикаторами — из снимка; история начинается с черновика.
        await App.WithDbAsync(async db =>
        {
            var rows = await db.DocumentCompetenceLinks.Where(l => l.DocumentId == fos.Document.Id).ToListAsync();
            Assert.Equal(indicators.Count, rows.Count(l => l.IndicatorId != null));
            Assert.Empty(await db.DocumentHours.Where(h => h.DocumentId == fos.Document.Id).ToListAsync());
            var history = Assert.Single(await db.DocumentStatusHistory.Where(h => h.DocumentId == fos.Document.Id).ToListAsync());
            Assert.Null(history.FromStatus);
            Assert.Equal((S.Draft, authorRecord.Id), (history.ToStatus, history.ChangedBy));
        });
    }

    [Fact]
    public async Task The_rpd_author_creates_when_nobody_is_assigned_a_methodist_creates_for_the_department_and_a_stranger_is_refused()
    {
        var (author, _) = await AccountAsync(App, AppRoles.Teacher);
        var (stranger, _) = await AccountAsync(App, AppRoles.Teacher);
        var (methodist, methodistRecord) = await AccountAsync(App, AppRoles.Methodist);
        var rpdTemplate = await DocumentData.PublishedTemplateAsync(App);
        var fosTemplate = await FosTemplateAsync();
        var first = await DocumentData.CreateAsync(App, author, rpdTemplate.Id, "Б1.О.12");
        var second = await DocumentData.CreateAsync(App, author, rpdTemplate.Id, "Б1.О.13");

        // Посторонний — 403, ФОС не создан; автор РПД — создаёт.
        var refused = await RefusedAsync(stranger, HttpStatusCode.Forbidden, fosTemplate.Id, first.Document.DisciplineId, first.Document.Id);
        Assert.Equal("РПД недоступна для ФОС", refused.Title);
        Assert.Contains("автор РПД", refused.Detail);
        await CreatedAsync(author, fosTemplate.Id, first.Document.DisciplineId, first.Document.Id);

        // Методист создаёт «за кафедру»: ФОС — его, а РПД остаётся у автора; открыть эту РПД он может, и ФОС в её списке виден.
        var byMethodist = await CreatedAsync(methodist, fosTemplate.Id, second.Document.DisciplineId, second.Document.Id);
        Assert.Equal(methodistRecord.Id, byMethodist.Document.AuthorId);
        var listed = Assert.Single((await DetailsAsync(author, second.Document.Id)).LinkedFos!);
        // ФОС методиста автору РПД открыт для чтения (этап 8, A4).
        Assert.Equal((byMethodist.Document.Id, true), (listed.Id, listed.CanOpen));
    }

    [Fact]
    public async Task A_discipline_with_assignments_gives_fos_to_the_assigned_teachers_only()
    {
        var (author, _) = await AccountAsync(App, AppRoles.Teacher);
        var (holder, holderRecord) = await AccountAsync(App, AppRoles.Teacher);
        var (stranger, _) = await AccountAsync(App, AppRoles.Teacher);
        var (methodist, _) = await AccountAsync(App, AppRoles.Methodist);
        var rpd = await DocumentData.CreateAsync(App, author, (await DocumentData.PublishedTemplateAsync(App)).Id, "Б1.О.10");
        var disciplineId = rpd.Document.DisciplineId;
        var fosTemplate = await FosTemplateAsync();
        await AssignedAsync(methodist, holderRecord, disciplineId);

        // С закреплениями ФОС создают закреплённые, как любой документ дисциплины (A10): автор РПД, не закреплённый, — тоже отказ.
        foreach (var client in new[] { author, stranger })
        {
            var refused = await RefusedAsync(client, HttpStatusCode.Forbidden, fosTemplate.Id, disciplineId, rpd.Document.Id);
            Assert.Equal("Дисциплина закреплена", refused.Title);
        }
        Assert.False((await DetailsAsync(author, rpd.Document.Id)).Actions.CreateFos);

        // Закреплённый видит РПД автора, создаёт ФОС и становится его автором; автор РПД эту РПД видит со списком ФОС.
        Assert.True((await DetailsAsync(holder, rpd.Document.Id)).Actions.CreateFos);
        var fos = await CreatedAsync(holder, fosTemplate.Id, disciplineId, rpd.Document.Id);
        Assert.Equal(holderRecord.Id, fos.Document.AuthorId);
        Assert.Equal(S.Draft, fos.Document.Status);
    }

    [Fact]
    public async Task A_deleted_or_archived_rpd_or_one_of_another_discipline_gives_no_fos_and_every_other_status_does()
    {
        var plan = await IsolatedPlanAsync("Б1.О.10", "Б1.О.11", "Б1.О.12", "Б1.О.13");
        var (author, _) = await AccountAsync(App, AppRoles.Teacher);
        var rpds = new List<DocumentDetails>();
        foreach (var discipline in plan.Disciplines) rpds.Add(await CreatedRpdAsync(author, plan.Rpd.Id, discipline.Id));

        // РПД другой дисциплины — 422: данные запроса.
        var mismatch = await RefusedAsync(author, HttpStatusCode.UnprocessableEntity, plan.Fos.Id, plan.Disciplines[0].Id, rpds[1].Document.Id);
        Assert.Equal("РПД другой дисциплины", mismatch.Title);

        // Удалённая РПД не находится — 422; а у дисциплины, где другой РПД нет, не из чего выбрать — 409.
        var deleted = await author.DeleteAsync($"/api/documents/{rpds[0].Document.Id}?version={rpds[0].Document.Version}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var gone = await RefusedAsync(author, HttpStatusCode.UnprocessableEntity, plan.Fos.Id, plan.Disciplines[0].Id, rpds[0].Document.Id);
        Assert.Equal("РПД не найдена", gone.Title);
        var none = await RefusedAsync(author, HttpStatusCode.Conflict, plan.Fos.Id, plan.Disciplines[0].Id, null);
        Assert.Equal("РПД нет", none.Title);

        // Архивная — 409, с названием и без: архивная РПД дисциплину без РПД не оставляет «чужой», а просто не берётся.
        await SetStatusAsync(App, rpds[1].Document.Id, S.Archived);
        var archived = await RefusedAsync(author, HttpStatusCode.Conflict, plan.Fos.Id, plan.Disciplines[1].Id, rpds[1].Document.Id);
        Assert.Equal("РПД в архиве", archived.Title);
        Assert.Equal("РПД нет", (await RefusedAsync(author, HttpStatusCode.Conflict, plan.Fos.Id, plan.Disciplines[1].Id, null)).Title);

        // Остальные статусы подходят (решение 8): на проверке и согласована.
        await SetStatusAsync(App, rpds[2].Document.Id, S.UnderReview);
        await SetStatusAsync(App, rpds[3].Document.Id, S.Approved);
        foreach (var rpd in rpds.Skip(2))
            Assert.Equal(S.Draft, (await CreatedAsync(author, plan.Fos.Id, rpd.Document.DisciplineId, rpd.Document.Id)).Document.Status);
    }

    [Fact]
    public async Task A_second_unfinished_fos_is_refused_and_the_discipline_list_shows_the_rpd_to_create_from()
    {
        var plan = await IsolatedPlanAsync("Б1.О.10", "Б1.О.11");
        var (author, _) = await AccountAsync(App, AppRoles.Teacher);
        var (stranger, _) = await AccountAsync(App, AppRoles.Teacher);
        var (withRpd, withoutRpd) = (plan.Disciplines[0], plan.Disciplines[1]);

        // До РПД: ФОС создавать не по чему.
        var before = await OptionAsync(author, plan.Fos.Id, withRpd.Id);
        Assert.Null(before.RpdId);
        Assert.False(before.CanCreate);
        var rpd = await CreatedRpdAsync(author, plan.Rpd.Id, withRpd.Id);

        // Экран выбора: у дисциплины названа РПД, её статус и автор; создать может автор РПД, постороннему — нет; без РПД — нельзя никому.
        var option = await OptionAsync(author, plan.Fos.Id, withRpd.Id);
        Assert.Equal((rpd.Document.Id, S.Draft, true), (option.RpdId, option.RpdStatus, option.CanCreate));
        Assert.Null(option.DocumentId);
        Assert.NotNull(option.RpdAuthor);
        Assert.False((await OptionAsync(stranger, plan.Fos.Id, withRpd.Id)).CanCreate);
        var without = await OptionAsync(author, plan.Fos.Id, withoutRpd.Id);
        Assert.Null(without.RpdId);
        Assert.False(without.CanCreate);

        // РПД не названа в запросе — берётся РПД дисциплины. РПД и ФОС дисциплины друг другу не мешают (линии шаблонов разные).
        var fos = await CreatedAsync(author, plan.Fos.Id, withRpd.Id, null);
        Assert.Equal(rpd.Document.Id, fos.LinkedRpd!.Id);

        // Второй незавершённый ФОС по линии шаблона — 409 со ссылкой на первый; список показывает его вместо кнопки.
        var duplicate = await RefusedAsync(author, HttpStatusCode.Conflict, plan.Fos.Id, withRpd.Id, rpd.Document.Id);
        Assert.Equal("Документ уже есть", duplicate.Title);
        Assert.Equal(fos.Document.Id.ToString(), duplicate.Extensions["documentId"]!.ToString());
        var listed = await OptionAsync(author, plan.Fos.Id, withRpd.Id);
        Assert.Equal((fos.Document.Id, S.Draft, true), (listed.DocumentId, listed.DocumentStatus, listed.CanOpen));
        Assert.Equal(1, await FosCountAsync(rpd.Document.Id));
    }

    [Fact]
    public async Task Rpd_changed_appears_after_the_data_change_not_after_a_status_change_and_refresh_replaces_the_snapshot_but_keeps_the_passport()
    {
        var (author, _) = await AccountAsync(App, AppRoles.Teacher);
        var (methodist, _) = await AccountAsync(App, AppRoles.Methodist);
        var rpd = await FilledRpdAsync(author, (await DocumentData.PublishedTemplateAsync(App)).Id, "Б1.О.10");
        var rpdId = rpd.Document.Id;
        var fos = await CreatedAsync(author, (await FosTemplateAsync()).Id, rpd.Document.DisciplineId, rpdId);
        Assert.False(fos.LinkedRpd!.Changed);

        // Отправка РПД меняет статус и растит версию — данные те же: пометки нет.
        await ExpectAsync(author.PostAsJsonAsync(Url(rpd, "submit"), new DocumentVersionRequest(rpd.Document.Version)), S.UnderReview);
        var afterStatus = (await DetailsAsync(author, fos.Document.Id)).LinkedRpd!;
        Assert.Equal((S.UnderReview, false), (afterStatus.Status, afterStatus.Changed));
        Assert.Null(afterStatus.CurrentVersion); // «сейчас N» — только при расхождении данных (этап 8, A2)

        // РПД на доработке: автор добавил индикатор — наследуемые данные изменились.
        await SetStatusAsync(App, rpdId, S.Returned);
        var current = await DetailsAsync(author, rpdId);
        var competences = JsonNode.Parse(current.Payload.GetProperty("competences").GetRawText())!.AsArray();
        var target = competences.OfType<JsonObject>().First(c =>
            current.Metadata.CompetenceCandidates.Single(x => x.Code == (string)c["code"]!).Indicators.Count >= 2);
        var chosenCode = (string)target["indicators"]![0]!["code"]!;
        var extra = current.Metadata.CompetenceCandidates.Single(x => x.Code == (string)target["code"]!).Indicators.First(i => i.Code != chosenCode);
        target["indicators"]!.AsArray().Add(new JsonObject { ["code"] = extra.Code, ["text"] = extra.Text });
        await DocumentData.PatchAsync(author, rpdId, "competences", current.Document.Version, competences);

        var changed = await DetailsAsync(author, fos.Document.Id);
        Assert.True(changed.LinkedRpd!.Changed);
        Assert.True(changed.LinkedRpd.CurrentVersion > changed.LinkedRpd.SnapshotVersion, "при расхождении версия РПД названа");
        Assert.True(changed.Actions.RefreshFromRpd, "автор ФОС в статусе правки обновляет данные РПД");
        var asMethodist = await DetailsAsync(methodist, fos.Document.Id);
        Assert.Equal((true, false), (asMethodist.LinkedRpd!.Changed, asMethodist.Actions.RefreshFromRpd));

        // Автор ФОС ссылается в паспорте на добавленный индикатор.
        var passport = new JsonObject
        {
            ["indicators"] = new JsonArray(new JsonObject
            {
                ["competence_code"] = (string)target["code"]!, ["indicator_code"] = extra.Code,
                ["tasks"] = new JsonArray(new JsonObject { ["group"] = "Вопросы к зачёту" })
            })
        };
        var version = (await DocumentData.PatchAsync(author, fos.Document.Id, "passport", changed.Document.Version, passport)).Version;

        // «Обновить из РПД»: снимок и индикаторы заменены, версия ФОС выросла, ответ называет добавленный индикатор.
        var refreshed = await RefreshedAsync(author, fos.Document.Id, version);
        Assert.Contains($"Добавлены индикаторы: {extra.Code}.", refreshed.Notice);
        var updated = await DetailsAsync(author, fos.Document.Id);
        Assert.False(updated.LinkedRpd!.Changed);
        Assert.True(updated.Document.Version > version);
        Assert.Contains(extra.Code, Strings(JsonNode.Parse(updated.Payload.GetRawText())!["competence_indicators"]!.AsArray().Select(i => i!["code"])));
        Assert.True(updated.LinkedRpd.SnapshotVersion > fos.LinkedRpd.SnapshotVersion);
        Assert.False(updated.Actions.RefreshFromRpd);
        // Данные не менялись — обновлять нечего: 409.
        var again = await RefreshAsync(author, fos.Document.Id, updated.Document.Version);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("Снимок актуален", (await again.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);

        // Индикатор исчез из РПД: пометка, обновление называет его, а строка паспорта с ним остаётся — удалять решает автор.
        current = await DetailsAsync(author, rpdId);
        competences = JsonNode.Parse(current.Payload.GetProperty("competences").GetRawText())!.AsArray();
        var vanished = competences.OfType<JsonObject>().Single(c => (string)c["code"]! == (string)target["code"]!)["indicators"]!.AsArray();
        vanished.Remove(vanished.Single(i => (string)i!["code"]! == extra.Code));
        await DocumentData.PatchAsync(author, rpdId, "competences", current.Document.Version, competences);
        Assert.True((await DetailsAsync(author, fos.Document.Id)).LinkedRpd!.Changed);

        var cleared = await RefreshedAsync(author, fos.Document.Id, updated.Document.Version);
        Assert.Contains($"Исчезли из РПД: индикаторы {extra.Code}", cleared.Notice);
        Assert.Contains("не удалены", cleared.Notice);
        var final = await DetailsAsync(author, fos.Document.Id);
        var finalPayload = JsonNode.Parse(final.Payload.GetRawText())!;
        Assert.DoesNotContain(extra.Code, Strings(finalPayload["competence_indicators"]!.AsArray().Select(i => i!["code"])));
        Assert.Equal(extra.Code, (string)finalPayload["passport"]!["indicators"]![0]!["indicator_code"]!);
        // Каждое обновление — запись в журнале аудита: кто и что изменилось в снимке.
        await App.WithDbAsync(async db =>
        {
            var records = await db.AuditLog.Where(a => a.EntityId == fos.Document.Id && a.EventType == AuditEvents.RpdSnapshotRefreshed)
                .OrderBy(a => a.OccurredAt).ToListAsync();
            Assert.Equal(2, records.Count);
            var logged = JsonDocument.Parse(records[1].Payload).RootElement;
            Assert.Equal(rpdId.ToString(), logged.GetProperty("rpd").GetString());
            Assert.Equal(new[] { extra.Code }, logged.GetProperty("removed_indicators").EnumerateArray().Select(i => i.GetString()!));
        });
    }

    [Fact]
    public async Task The_snapshot_is_refreshed_by_the_author_in_editing_statuses_and_by_the_methodist_on_review_only()
    {
        var plan = await IsolatedPlanAsync("Б1.О.10", "Б1.О.11");
        var (author, _) = await AccountAsync(App, AppRoles.Teacher);
        var (stranger, _) = await AccountAsync(App, AppRoles.Teacher);
        var (methodist, _) = await AccountAsync(App, AppRoles.Methodist);
        var rpd = await FilledRpdAsync(author, plan.Rpd.Id, plan.Disciplines[0].Id);
        var fos = await CreatedAsync(author, plan.Fos.Id, plan.Disciplines[0].Id, rpd.Document.Id);

        // Тема переименована в РПД, пока она черновик: данные изменились.
        var topics = JsonNode.Parse(rpd.Payload.GetProperty("thematic_plan").GetRawText())!.AsArray();
        topics[0]!["section_title"] = "Переименованный раздел";
        await DocumentData.PatchAsync(author, rpd.Document.Id, "thematic_plan", rpd.Document.Version, topics);
        Assert.True((await DetailsAsync(author, fos.Document.Id)).LinkedRpd!.Changed);

        // Посторонний — 403; РПД — 409: «Обновить из РПД» есть только у ФОС; на проверке автору закрыто (409), методисту открыто.
        Assert.Equal(HttpStatusCode.Forbidden, (await RefreshAsync(stranger, fos.Document.Id, fos.Document.Version)).StatusCode);
        var onRpd = await RefreshAsync(author, rpd.Document.Id, rpd.Document.Version);
        Assert.Equal(HttpStatusCode.Conflict, onRpd.StatusCode);
        Assert.Equal("Действие недоступно для РПД", (await onRpd.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
        await SetStatusAsync(App, fos.Document.Id, S.UnderReview);
        Assert.Equal(HttpStatusCode.Conflict, (await RefreshAsync(author, fos.Document.Id, fos.Document.Version)).StatusCode);
        var review = await DetailsAsync(methodist, fos.Document.Id);
        Assert.True(review.Actions.RefreshFromRpd);
        var done = await RefreshedAsync(methodist, fos.Document.Id, review.Document.Version);
        Assert.Equal(S.UnderReview, done.Status);
        Assert.False((await DetailsAsync(methodist, fos.Document.Id)).LinkedRpd!.Changed);

        // После приёма снимок закрыт и для методиста: даже когда РПД снова изменилась.
        topics[0]!["section_title"] = "Ещё одно наименование раздела";
        await DocumentData.PatchAsync(author, rpd.Document.Id, "thematic_plan", (await DetailsAsync(author, rpd.Document.Id)).Document.Version, topics);
        await SetStatusAsync(App, fos.Document.Id, S.Accepted);
        var accepted = await DetailsAsync(methodist, fos.Document.Id);
        Assert.Equal((true, false), (accepted.LinkedRpd!.Changed, accepted.Actions.RefreshFromRpd));
        Assert.Equal(HttpStatusCode.Conflict, (await RefreshAsync(methodist, fos.Document.Id, accepted.Document.Version)).StatusCode);
    }

    [Fact]
    public async Task A_deleted_rpd_leaves_the_fos_with_the_last_snapshot_and_nothing_to_refresh_from()
    {
        var plan = await IsolatedPlanAsync("Б1.О.10");
        var (author, _) = await AccountAsync(App, AppRoles.Teacher);
        var rpd = await FilledRpdAsync(author, plan.Rpd.Id, plan.Disciplines[0].Id);
        var fos = await CreatedAsync(author, plan.Fos.Id, plan.Disciplines[0].Id, rpd.Document.Id);

        var deleted = await author.DeleteAsync($"/api/documents/{rpd.Document.Id}?version={rpd.Document.Version}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        // ФОС открывается: связь названа, РПД удалена, обновлять не из чего; снимок остался в содержимом.
        var details = await DetailsAsync(author, fos.Document.Id);
        var linked = details.LinkedRpd!;
        Assert.Equal((rpd.Document.Id, S.Deleted, true, false, false), (linked.Id, linked.Status, linked.Deleted, linked.Changed, linked.CanOpen));
        Assert.False(details.Actions.RefreshFromRpd);
        Assert.NotEmpty(JsonNode.Parse(details.Payload.GetRawText())!["linked_rpd"]!["competences"]!.AsArray());
        var refused = await RefreshAsync(author, fos.Document.Id, details.Document.Version);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("РПД удалена", (await refused.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
    }

    [Fact]
    public async Task The_author_of_a_fos_reads_the_linked_rpd_without_editing_it_and_others_do_not_see_it()
    {
        var (author, _) = await AccountAsync(App, AppRoles.Teacher);
        var (reader, readerRecord) = await AccountAsync(App, AppRoles.Teacher);
        var (stranger, _) = await AccountAsync(App, AppRoles.Teacher);
        var (methodist, _) = await AccountAsync(App, AppRoles.Methodist);
        var rpd = await FilledRpdAsync(author, (await DocumentData.PublishedTemplateAsync(App)).Id, "Б1.О.12");
        var fos = await CreatedAsync(methodist, (await FosTemplateAsync()).Id, rpd.Document.DisciplineId, rpd.Document.Id);

        // Пока ФОС не у него, РПД чужая; после передачи ФОС ему — читает её, правок и действий у него нет.
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"/api/documents/{rpd.Document.Id}")).StatusCode);
        var transfer = await methodist.PostAsJsonAsync(Url(fos, "transfer"), new TransferRequest(fos.Document.Version, readerRecord.Id, "Ведёт дисциплину"));
        Assert.True(transfer.IsSuccessStatusCode, await transfer.Content.ReadAsStringAsync());

        var read = await DetailsAsync(reader, rpd.Document.Id);
        Assert.False(read.Editable);
        Assert.Equal(new DocumentActions(false, false, false, false), read.Actions);
        var listed = Assert.Single(read.LinkedFos!);
        Assert.Equal((fos.Document.Id, true), (listed.Id, listed.CanOpen));
        Assert.True((await reader.GetAsync($"/api/documents/{rpd.Document.Id}/comments")).IsSuccessStatusCode);
        var forbidden = await reader.PatchAsJsonAsync($"/api/documents/{rpd.Document.Id}/sections/goals_and_objectives",
            new SectionUpdateRequest(read.Document.Version, JsonDocument.Parse("""{ "goals": "<p>Чужая цель</p>" }""").RootElement));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.False((await reader.PostAsync($"/api/documents/{rpd.Document.Id}/archive", null)).IsSuccessStatusCode);

        // Связанная РПД открывается из ФОС; автору РПД ссылка на ФОС по ней положена (этап 8, A4), постороннему РПД закрыта.
        Assert.True((await DetailsAsync(reader, fos.Document.Id)).LinkedRpd!.CanOpen);
        Assert.True(Assert.Single((await DetailsAsync(author, rpd.Document.Id)).LinkedFos!).CanOpen);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync($"/api/documents/{rpd.Document.Id}")).StatusCode);
    }

    [Fact]
    public async Task The_rpd_author_reads_every_fos_made_by_others_without_editing_and_strangers_do_not()
    {
        var (author, _) = await AccountAsync(App, AppRoles.Teacher);
        var (stranger, _) = await AccountAsync(App, AppRoles.Teacher);
        var (methodist, _) = await AccountAsync(App, AppRoles.Methodist);
        var plan = await IsolatedPlanAsync("Б1.О.10");
        var rpd = await FilledRpdAsync(author, plan.Rpd.Id, plan.Disciplines[0].Id);
        var fos = await CreatedAsync(methodist, plan.Fos.Id, plan.Disciplines[0].Id, rpd.Document.Id);

        // Из списка ФОС в РПД и по ссылке; правка, действия и удаление — нет.
        var listed = Assert.Single((await DetailsAsync(author, rpd.Document.Id)).LinkedFos!);
        Assert.Equal((fos.Document.Id, true), (listed.Id, listed.CanOpen));
        var read = await DetailsAsync(author, fos.Document.Id);
        Assert.False(read.Editable);
        Assert.Equal(new DocumentActions(false, false, false, false), read.Actions);
        Assert.Equal((rpd.Document.Id, true), (read.LinkedRpd!.Id, read.LinkedRpd.CanOpen));
        Assert.True(read.LinkedRpd.CrossCheck, "панель сквозной проверки — тем, кто читает ФОС по своей РПД");
        Assert.True((await author.GetAsync($"/api/documents/{fos.Document.Id}/comments")).IsSuccessStatusCode);
        var forbidden = await author.PatchAsJsonAsync($"/api/documents/{fos.Document.Id}/sections/linked_rpd",
            new SectionUpdateRequest(read.Document.Version, read.Payload.GetProperty("linked_rpd")));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await author.DeleteAsync($"/api/documents/{fos.Document.Id}?version={read.Document.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync($"/api/documents/{fos.Document.Id}")).StatusCode);
    }

    [Fact]
    public async Task The_reading_follows_the_current_rpd_author_after_a_transfer()
    {
        var (author, authorRecord) = await AccountAsync(App, AppRoles.Teacher);
        var (next, nextRecord) = await AccountAsync(App, AppRoles.Teacher);
        var (methodist, _) = await AccountAsync(App, AppRoles.Methodist);
        var plan = await IsolatedPlanAsync("Б1.О.10");
        var rpd = await FilledRpdAsync(author, plan.Rpd.Id, plan.Disciplines[0].Id);
        var fos = await CreatedAsync(methodist, plan.Fos.Id, plan.Disciplines[0].Id, rpd.Document.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await next.GetAsync($"/api/documents/{fos.Document.Id}")).StatusCode);
        Assert.True(await VisibleToAsync(fos.Document.Id, authorRecord.Id));
        Assert.False(await VisibleToAsync(fos.Document.Id, nextRecord.Id));

        var transfer = await methodist.PostAsJsonAsync(Url(rpd, "transfer"), new TransferRequest(rpd.Document.Version, nextRecord.Id, "Ведёт дисциплину"));
        Assert.True(transfer.IsSuccessStatusCode, await transfer.Content.ReadAsStringAsync());

        // Новый автор РПД читает ФОС, прежний — нет; правило ядра (напоминания, уведомления) говорит то же, что API.
        Assert.True((await next.GetAsync($"/api/documents/{fos.Document.Id}")).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await author.GetAsync($"/api/documents/{fos.Document.Id}")).StatusCode);
        Assert.True(await VisibleToAsync(fos.Document.Id, nextRecord.Id));
        Assert.False(await VisibleToAsync(fos.Document.Id, authorRecord.Id));
    }

    // Правило «кто откроет документ» вне API (DocumentVisibility): по ролям учётной записи из базы, без токена.
    private async Task<bool> VisibleToAsync(Guid documentId, Guid employeeId)
    {
        await using var scope = App.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var document = await services.GetRequiredService<AppDbContext>().Documents.AsNoTracking().SingleAsync(d => d.Id == documentId);
        return await DocumentVisibility.CanOpenAsync(
            services.GetRequiredService<DocumentApproval>(), services.GetRequiredService<DisciplineAssignments>(),
            services.GetRequiredService<LinkedDocuments>(), document, employeeId, [AppRoles.Teacher]);
    }

    [Fact]
    public async Task The_snapshot_sections_do_not_accept_manual_changes_and_a_fos_on_a_basis_is_not_created_yet()
    {
        var (author, _) = await AccountAsync(App, AppRoles.Teacher);
        var rpd = await FilledRpdAsync(author, (await DocumentData.PublishedTemplateAsync(App)).Id, "Б1.О.13");
        var fosTemplate = await FosTemplateAsync();

        // ФОС на основе РПД (документа другого типа) не создаётся (B10): отказ, документ не создан.
        var basis = await RefusedAsync(author, HttpStatusCode.UnprocessableEntity, fosTemplate.Id, rpd.Document.DisciplineId, rpd.Document.Id, basedOn: rpd.Document.Id);
        Assert.Equal("Основа другого типа", basis.Title);
        Assert.Equal(0, await FosCountAsync(rpd.Document.Id));

        var fos = await CreatedAsync(author, fosTemplate.Id, rpd.Document.DisciplineId, rpd.Document.Id);
        var link = JsonNode.Parse(fos.Payload.GetProperty("linked_rpd").GetRawText())!;
        var tampered = link.DeepClone();
        tampered["rpd_document_id"] = Guid.NewGuid().ToString();
        var indicators = JsonNode.Parse(fos.Payload.GetProperty("competence_indicators").GetRawText())!.AsArray();
        indicators.Add(new JsonObject { ["code"] = "УК-9.9", ["text"] = "Лишний" });

        // Связь и список индикаторов вручную не меняются (422); тот же раздел без изменений сохраняется.
        foreach (var (section, value) in new[] { ("linked_rpd", tampered), ("competence_indicators", (JsonNode)indicators) })
        {
            var response = await author.PatchAsJsonAsync($"/api/documents/{fos.Document.Id}/sections/{section}",
                new SectionUpdateRequest(fos.Document.Version, JsonDocument.Parse(value.ToJsonString()).RootElement));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Contains(section, await response.Content.ReadAsStringAsync());
        }
        var saved = await author.PatchAsJsonAsync($"/api/documents/{fos.Document.Id}/sections/linked_rpd",
            new SectionUpdateRequest(fos.Document.Version, JsonDocument.Parse(link.ToJsonString()).RootElement));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
    }

    // ───── банк заданий и билеты (этап 7, B5) ─────

    private const string BankSample = """
        [ { "group": "Вопросы к зачёту", "kind": "questions", "control_form": "intermediate", "semesters": [1],
            "tasks": [ { "number": 1, "text": "<p>Первый</p>" }, { "number": 3, "text": "<p>Третий</p>" },
                       { "number": 4, "text": "<p>Четвёртый</p>" }, { "number": 7, "text": "<p>Седьмой</p>" } ] },
          { "group": "Тест 1", "kind": "tests", "section_number": 1, "control_form": "current",
            "tasks": [ { "number": 1, "text": "<p>Вопрос</p>", "expected_answer": "а", "max_score": 2 } ] } ]
        """;

    private const string PassportSample = """
        { "sections": [ { "section_number": 1, "competence_codes": ["УК-1"],
            "tasks": [ { "group": "Вопросы к зачёту", "from": 3, "to": 4 }, { "group": "Вопросы к зачёту", "from": 7, "to": 7 },
                       { "group": "Тест 1" } ] } ],
          "indicators": [ { "competence_code": "УК-1", "indicator_code": "УК-1.1",
            "tasks": [ { "group": "Вопросы к зачёту", "from": 1, "to": 7 } ] } ] }
        """;

    /// <summary>ФОС по заполненной РПД с банком и паспортом из образцов: номера в группе идут с пропусками, паспорт ссылается на них.</summary>
    private async Task<(HttpClient Author, DocumentDetails Fos, int Version)> FosWithBankAsync(string code)
    {
        var (author, _) = await AccountAsync(App, AppRoles.Teacher);
        var rpd = await FilledRpdAsync(author, (await DocumentData.PublishedTemplateAsync(App)).Id, code);
        var fos = await CreatedAsync(author, (await FosTemplateAsync()).Id, rpd.Document.DisciplineId, rpd.Document.Id);
        var bank = await DocumentData.PatchAsync(author, fos.Document.Id, "task_bank", fos.Document.Version, JsonNode.Parse(BankSample));
        var passport = await DocumentData.PatchAsync(author, fos.Document.Id, "passport", bank.Version, JsonNode.Parse(PassportSample));
        return (author, fos, passport.Version);
    }

    [Fact]
    public async Task The_cross_check_holds_the_fos_by_the_rpd_status_and_reaches_both_documents()
    {
        var (author, fos, version) = await FosWithBankAsync("Б1.О.11");
        var rpdId = FosSnapshot.RpdIdOf(fos.Payload.GetRawText())!.Value;

        // РПД — черновик: отправка ФОС отказывает ошибкой правила; предупреждения идут рядом и сами не блокируют.
        var refused = await author.PostAsJsonAsync(Url(fos, "submit"), new DocumentVersionRequest(version));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var problem = (await refused.Content.ReadFromJsonAsync<ProblemDetails>())!;
        var errors = ((JsonElement)problem.Extensions["errors"]!).EnumerateArray().ToList();
        var held = Assert.Single(errors, e => e.GetProperty("rule").GetString() == "linked_rpd_must_be_approved_or_under_review");
        Assert.Equal(("/linked_rpd", "error"), (held.GetProperty("path").GetString(), held.GetProperty("level").GetString()));
        Assert.Contains("«Черновик»", held.GetProperty("message").GetString());

        // Панель — автору ФОС; предупреждения называют правило и лежат в разделах, к которым ведут.
        var details = await DetailsAsync(author, fos.Document.Id);
        Assert.True(details.LinkedRpd!.CrossCheck);
        var warnings = details.Metadata.Sections.SelectMany(s => s.Warnings).Where(w => w.Rule is not null).ToList();
        Assert.Contains(warnings, w => w.Rule == "all_indicators_have_means");
        Assert.Contains(warnings, w => w.Rule == "fos_regulation_thresholds");
        Assert.All(warnings, w => Assert.Equal("warning", w.Level));

        // Строка на странице РПД — то же число расхождений.
        var onRpd = await DetailsAsync(author, rpdId);
        Assert.Equal(warnings.Count, Assert.Single(onRpd.LinkedFos!).Discrepancies);

        // РПД на проверке — ошибка статуса уходит; предупреждения остаются и отправку не останавливают: если ФОС и не ушёл,
        // то по другим причинам (схема), а не из-за сквозной проверки.
        await ExpectAsync(author.PostAsJsonAsync(Url(onRpd, "submit"), new DocumentVersionRequest(onRpd.Document.Version)), S.UnderReview);
        var again = await author.PostAsJsonAsync(Url(fos, "submit"), new DocumentVersionRequest(version));
        if (again.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            var left = ((JsonElement)(await again.Content.ReadFromJsonAsync<ProblemDetails>())!.Extensions["errors"]!).EnumerateArray()
                .Where(e => e.GetProperty("level").GetString() == "error").ToList();
            Assert.All(left, e => Assert.Equal(JsonValueKind.Null, e.GetProperty("rule").ValueKind));
        }
        else Assert.True(again.IsSuccessStatusCode, $"{(int)again.StatusCode}");
    }

    private static Task<HttpResponseMessage> RenumberAsync(HttpClient client, Guid documentId, int version, string group) =>
        client.PostAsJsonAsync($"/api/documents/{documentId}/renumber-bank", new RenumberBankRequest(version, group));

    [Fact]
    public async Task The_author_renumbers_a_group_and_the_passport_references_change_in_the_same_save()
    {
        var (author, fos, version) = await FosWithBankAsync("Б1.О.11");

        var response = await RenumberAsync(author, fos.Document.Id, version, "Вопросы к зачёту");
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        var done = (await response.Content.ReadFromJsonAsync<DocumentReviewed>())!;

        // Одно сохранение: версия выросла на единицу, статус не менялся, в ответе — что сделано.
        Assert.Equal(version + 1, done.Version);
        Assert.Contains("перенумерованы: 1–4", done.Notice);
        var after = await DetailsAsync(author, fos.Document.Id);
        var payload = JsonNode.Parse(after.Payload.GetRawText())!;
        Assert.Equal(new[] { 1, 2, 3, 4 }, payload["task_bank"]![0]!["tasks"]!.AsArray().Select(t => (int)t!["number"]!));
        Assert.Equal(new[] { 1 }, payload["task_bank"]![1]!["tasks"]!.AsArray().Select(t => (int)t!["number"]!));
        var refs = payload["passport"]!["sections"]![0]!["tasks"]!.AsArray();
        Assert.Equal((2, 3), ((int)refs[0]!["from"]!, (int)refs[0]!["to"]!));
        Assert.Equal((4, 4), ((int)refs[1]!["from"]!, (int)refs[1]!["to"]!));
        Assert.Equal("Тест 1", (string)refs[2]!["group"]!);
        var wide = payload["passport"]!["indicators"]![0]!["tasks"]![0]!;
        Assert.Equal((1, 4), ((int)wide["from"]!, (int)wide["to"]!));

        // Снимок версии и запись аудита — как у любой правки.
        await App.WithDbAsync(async db =>
        {
            var last = await db.DocumentVersions.Where(v => v.DocumentId == fos.Document.Id).OrderByDescending(v => v.VersionNumber).FirstAsync();
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(last.Snapshot), payload));
            var audit = Assert.Single(await db.AuditLog.Where(a => a.EntityId == fos.Document.Id && a.EventType == AuditEvents.BankRenumbered).ToListAsync());
            var content = JsonNode.Parse(audit.Payload)!;
            Assert.Equal(("Вопросы к зачёту", 3), ((string)content["group"]!, (int)content["rewritten_references"]!));
        });

        // Повторно — нумерация уже сплошная: 409, версия не растёт.
        var again = await RenumberAsync(author, fos.Document.Id, done.Version, "Вопросы к зачёту");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("Нумерация уже сплошная", (await again.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
        Assert.Equal(done.Version, (await DetailsAsync(author, fos.Document.Id)).Document.Version);
    }

    [Fact]
    public async Task Renumbering_is_refused_for_a_stranger_an_rpd_an_unknown_group_and_a_stale_version()
    {
        var (author, fos, version) = await FosWithBankAsync("Б1.О.11");
        var (stranger, _) = await AccountAsync(App, AppRoles.Teacher);

        Assert.Equal(HttpStatusCode.Forbidden, (await RenumberAsync(stranger, fos.Document.Id, version, "Вопросы к зачёту")).StatusCode);
        var unknown = await RenumberAsync(author, fos.Document.Id, version, "Нет такой");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("Группа не найдена", (await unknown.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
        Assert.Equal(HttpStatusCode.Conflict, (await RenumberAsync(author, fos.Document.Id, version - 1, "Вопросы к зачёту")).StatusCode);
        var rpdId = Guid.Parse((string)JsonNode.Parse(fos.Payload.GetRawText())!["linked_rpd"]!["rpd_document_id"]!);
        var rpd = await DetailsAsync(author, rpdId);
        var onRpd = await RenumberAsync(author, rpdId, rpd.Document.Version, "Вопросы к зачёту");
        Assert.Equal(HttpStatusCode.Conflict, onRpd.StatusCode);
        Assert.Equal("Действие недоступно для РПД", (await onRpd.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);

        // Ни один отказ ничего не изменил: номера прежние.
        var payload = JsonNode.Parse((await DetailsAsync(author, fos.Document.Id)).Payload.GetRawText())!;
        Assert.Equal(new[] { 1, 3, 4, 7 }, payload["task_bank"]![0]!["tasks"]!.AsArray().Select(t => (int)t!["number"]!));
    }

    private static Task<HttpResponseMessage> SuggestAsync(HttpClient client, Guid documentId, params string[] covered) =>
        client.PostAsJsonAsync($"/api/documents/{documentId}/means-suggestions", new MeansSuggestionRequest(covered));

    [Fact]
    public async Task Suggesting_means_returns_rows_for_indicators_without_means_and_changes_nothing()
    {
        var (author, fos, version) = await FosWithBankAsync("Б1.О.11");
        var payload = JsonNode.Parse((await DetailsAsync(author, fos.Document.Id)).Payload.GetRawText())!;
        var indicators = payload["linked_rpd"]!["competences"]!.AsArray()
            .SelectMany(c => c!["indicators"]!.AsArray().Select(i => (string)i!["code"]!)).ToList();
        var inSection = (string)payload["linked_rpd"]!["sections"]![0]!["indicator_codes"]![0]!;

        var response = await SuggestAsync(author, fos.Document.Id);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        var all = (await response.Content.ReadFromJsonAsync<MeansSuggestionResponse>())!;

        // Форма раздела — «Контрольная работа»: письменный опрос с индикатором раздела; все коды — из снимка, критериев нет.
        var rows = all.Rows.OfType<JsonObject>().ToList();
        Assert.Contains(rows, r => (string)r["means_type"]! == "written_survey" &&
            r["indicator_codes"]!.AsArray().Any(c => (string)c! == inSection));
        Assert.All(rows, r => Assert.All(r["indicator_codes"]!.AsArray(), c => Assert.Contains((string)c!, indicators)));
        Assert.All(rows, r => Assert.Null(r["criteria"]));

        // Индикаторы, у которых средство уже есть, не получают ничего; когда есть у всех — предлагать нечего.
        var rest = (await (await SuggestAsync(author, fos.Document.Id, inSection)).Content.ReadFromJsonAsync<MeansSuggestionResponse>())!;
        Assert.DoesNotContain(rest.Rows.OfType<JsonObject>(), r => r["indicator_codes"]!.AsArray().Any(c => (string)c! == inSection));
        var none = (await (await SuggestAsync(author, fos.Document.Id, indicators.ToArray())).Content.ReadFromJsonAsync<MeansSuggestionResponse>())!;
        Assert.Empty(none.Rows);
        Assert.Contains("предлагать нечего", none.Notice);

        // Подбор ничего не сохраняет: версия документа прежняя.
        Assert.Equal(version, (await DetailsAsync(author, fos.Document.Id)).Document.Version);
    }

    [Fact]
    public async Task Suggesting_means_is_refused_for_a_stranger_and_an_rpd()
    {
        var (author, fos, _) = await FosWithBankAsync("Б1.О.11");
        var (stranger, _) = await AccountAsync(App, AppRoles.Teacher);

        Assert.Equal(HttpStatusCode.Forbidden, (await SuggestAsync(stranger, fos.Document.Id)).StatusCode);
        var rpdId = Guid.Parse((string)JsonNode.Parse(fos.Payload.GetRawText())!["linked_rpd"]!["rpd_document_id"]!);
        var onRpd = await SuggestAsync(author, rpdId);
        Assert.Equal(HttpStatusCode.Conflict, onRpd.StatusCode);
        Assert.Equal("Действие недоступно для РПД", (await onRpd.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
    }

    [Fact]
    public async Task The_section_tree_of_an_fos_shows_the_fill_of_passport_and_bank()
    {
        var (author, fos, _) = await FosWithBankAsync("Б1.О.11");
        var before = (await DetailsAsync(author, fos.Document.Id)).Metadata.Sections.ToDictionary(s => s.Key);

        // Все разделы схемы ФОС — в дереве; заполненные паспорт и банк «пустыми» не числятся.
        Assert.All(new[] { "passport", "task_bank", "evaluation_means", "current_control", "exam_tickets", "evaluation_scale" },
            key => Assert.True(before.ContainsKey(key), key));
        Assert.NotEqual(SectionFill.Empty, before["passport"].Fill);
        Assert.NotEqual(SectionFill.Empty, before["task_bank"].Fill);
        // Необязательные разделы, которых нет в содержимом, ничего не требуют.
        Assert.Equal(SectionFill.NotApplicable, before["exam_tickets"].Fill);
    }

    [Fact]
    public async Task The_client_gets_the_fos_with_the_title_page_and_the_approval_sheet_spelled_out_not_referenced()
    {
        var (author, fos, _) = await FosWithBankAsync("Б1.О.11");

        var properties = (await DetailsAsync(author, fos.Document.Id)).Schema.GetProperty("properties");

        // Клиент разбирает только локальные ссылки (точка проверки B7): титул и лист согласования приходят готовыми разделами.
        Assert.True(properties.GetProperty("title_page").GetProperty("properties").TryGetProperty("academic_year", out _));
        Assert.Equal("developer_picker",
            properties.GetProperty("approval_sheet").GetProperty("properties").GetProperty("developers").GetProperty("x-ui-widget").GetString());
        Assert.DoesNotContain("\"$ref\":\"https", properties.GetRawText().Replace(" ", ""));
    }

    [Fact]
    public async Task Rich_text_of_tasks_and_ticket_questions_is_sanitized()
    {
        var (author, fos, version) = await FosWithBankAsync("Б1.О.11");

        // Задание — форматированный текст с таблицей; вопрос билета — тоже форматированный: скрипт и обработчики снимаются.
        var bank = JsonNode.Parse(BankSample)!;
        bank[0]!["tasks"]![0]!["text"] = "<p>Задание <b>с</b> таблицей</p><table><tr><td colspan=\"2\" onclick=\"x()\">а</td></tr></table><script>x()</script>";
        version = (await DocumentData.PatchAsync(author, fos.Document.Id, "task_bank", version, bank)).Version;
        var tickets = JsonNode.Parse("""[ { "number": 1, "questions": ["<p onclick=\"x()\">Первый <i>вопрос</i></p><script>x()</script>", "<p>Второй</p>"] } ]""");
        version = (await DocumentData.PatchAsync(author, fos.Document.Id, "exam_tickets", version, tickets)).Version;

        var payload = JsonNode.Parse((await DetailsAsync(author, fos.Document.Id)).Payload.GetRawText())!;
        var text = (string)payload["task_bank"]![0]!["tasks"]![0]!["text"]!;
        Assert.Contains("<table>", text);
        Assert.Contains("colspan=\"2\"", text);
        Assert.DoesNotContain("script", text);
        Assert.DoesNotContain("onclick", text);
        var question = (string)payload["exam_tickets"]![0]!["questions"]![0]!;
        Assert.Contains("<i>вопрос</i>", question);
        Assert.DoesNotContain("script", question);
        Assert.DoesNotContain("onclick", question);
    }

    // ───── вспомогательное ─────

    /// <summary>Строки из значений-узлов JSON.</summary>
    private static string[] Strings(JsonNode? list) => list?.AsArray().Select(i => (string)i!).ToArray() ?? [];

    private static string[] Strings(IEnumerable<JsonNode?> nodes) => nodes.Select(i => (string)i!).ToArray();

    /// <summary>
    /// РПД, заполненная автором так, чтобы пройти проверку: компетенции, тематический план по семестрам — с выбранным индикатором
    /// и формой текущего контроля у каждой темы.
    /// </summary>
    private async Task<DocumentDetails> FilledRpdAsync(HttpClient author, Guid templateId, string code) =>
        await FilledRpdAsync(author, await DocumentData.CreateAsync(App, author, templateId, code));

    private async Task<DocumentDetails> FilledRpdAsync(HttpClient author, Guid templateId, Guid disciplineId) =>
        await FilledRpdAsync(author, await CreatedRpdAsync(author, templateId, disciplineId));

    private async Task<DocumentDetails> FilledRpdAsync(HttpClient author, DocumentDetails created)
    {
        var version = await DocumentData.FillForSubmissionAsync(author, created);
        var filled = await DetailsAsync(author, created.Document.Id);
        var chosen = JsonNode.Parse(filled.Payload.GetProperty("competences").GetRawText())!.AsArray();
        var plan = JsonNode.Parse(filled.Payload.GetProperty("thematic_plan").GetRawText())!.AsArray();
        foreach (var topic in plan.OfType<JsonObject>())
        {
            topic["competence_indicators"] = new JsonArray((string)chosen[0]!["indicators"]![0]!["code"]!);
            topic["current_control_forms"] = new JsonArray("Контрольная работа");
        }
        await DocumentData.PatchAsync(author, created.Document.Id, "thematic_plan", version, plan);
        return await DetailsAsync(author, created.Document.Id);
    }

    /// <summary>РПД по шаблону плана для дисциплины — как её создаёт автор.</summary>
    private static async Task<DocumentDetails> CreatedRpdAsync(HttpClient author, Guid templateId, Guid disciplineId)
    {
        var response = await author.PostAsJsonAsync("/api/documents", new CreateDocumentRequest(templateId, disciplineId));
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<DocumentDetails>())!;
    }

    private async Task<DocumentTemplate> FosTemplateAsync(Guid? planId = null)
    {
        var plan = planId ?? await App.RealPlanIdAsync();
        DocumentTemplate? template = null;
        await App.WithDbAsync(async db => template = await TemplateData.AddPublishedAsync(db, plan, DocumentTypeRegistry.Fos));
        return template!;
    }

    private static async Task<DocumentDetails> CreatedAsync(HttpClient client, Guid templateId, Guid disciplineId, Guid? rpdId)
    {
        var response = await client.PostAsJsonAsync("/api/documents", new CreateDocumentRequest(templateId, disciplineId, null, rpdId));
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<DocumentDetails>())!;
    }

    private static async Task<ProblemDetails> RefusedAsync(
        HttpClient client, HttpStatusCode status, Guid templateId, Guid disciplineId, Guid? rpdId, Guid? basedOn = null)
    {
        var response = await client.PostAsJsonAsync("/api/documents", new CreateDocumentRequest(templateId, disciplineId, basedOn, rpdId));
        Assert.True(response.StatusCode == status, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ProblemDetails>())!;
    }

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient client, Guid documentId, int version) =>
        client.PostAsJsonAsync($"/api/documents/{documentId}/refresh-from-rpd", new DocumentVersionRequest(version));

    /// <summary>«Обновить из РПД», который должен пройти.</summary>
    private static async Task<DocumentReviewed> RefreshedAsync(HttpClient client, Guid documentId, int version)
    {
        var response = await RefreshAsync(client, documentId, version);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<DocumentReviewed>())!;
    }

    private static async Task<DisciplineOption> OptionAsync(HttpClient client, Guid templateId, Guid disciplineId) =>
        (await client.GetFromJsonAsync<List<DisciplineOption>>($"/api/documents/disciplines?templateId={templateId}"))!
            .Single(o => o.Id == disciplineId);

    /// <summary>Сколько ФОС создано по РПД (в базе, в обход прав).</summary>
    private async Task<int> FosCountAsync(Guid rpdId)
    {
        var count = 0;
        await App.WithDbAsync(async db =>
            count = (await db.Documents.Where(d => d.Type == TemplateTypes.Fos).Select(d => d.Payload).ToListAsync())
                .Count(p => FosSnapshot.RpdIdOf(p) == rpdId));
        return count;
    }

    private sealed record IsolatedPlan(DocumentTemplate Rpd, DocumentTemplate Fos, List<Discipline> Disciplines);

    /// <summary>
    /// Отдельный учебный план с копиями дисциплин реального плана (часы, семестры, матрица компетенций — те же), опубликованными
    /// шаблонами РПД и ФОС: РПД других тестов в нём не встречаются. Код направления — обычного вида, чтобы документ проходил проверку.
    /// </summary>
    private async Task<IsolatedPlan> IsolatedPlanAsync(params string[] codes)
    {
        var realPlan = await App.RealPlanIdAsync();
        IsolatedPlan? result = null;
        await App.WithDbAsync(async db =>
        {
            var direction = new StudyDirection
            {
                Code = $"{Random.Shared.Next(50, 100)}.{Random.Shared.Next(10, 100)}.{Random.Shared.Next(10, 100)}",
                Name = $"Тестовое направление {Guid.NewGuid()}", EducationLevel = EducationLevels.Master
            };
            var profile = new StudyProfile { DirectionId = direction.Id, Name = "Тестовый профиль" };
            var plan = new CurriculumPlan { DirectionId = direction.Id, ProfileId = profile.Id, Year = 2026, CreatedAt = DateTimeOffset.UtcNow };
            db.AddRange(direction, profile, plan);

            var copies = new List<Discipline>();
            foreach (var code in codes)
            {
                var source = await db.Disciplines.AsNoTracking().Include(d => d.Semesters)
                    .SingleAsync(d => d.CurriculumPlanId == realPlan && d.Code == code);
                var copy = new Discipline
                {
                    CurriculumPlanId = plan.Id, DepartmentId = source.DepartmentId, Code = source.Code, Name = $"Дисциплина {source.Code} отдельного плана",
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
                db.Add(copy);
                var matrix = await db.CompetenceMatrix.Where(m => m.DisciplineId == source.Id).Select(m => m.CompetenceId).ToListAsync();
                db.CompetenceMatrix.AddRange(matrix.Select(id => new CompetenceMatrixEntry { DisciplineId = copy.Id, CompetenceId = id }));
                copies.Add(copy);
            }
            await db.SaveChangesAsync();
            result = new(await TemplateData.AddPublishedAsync(db, plan.Id), await TemplateData.AddPublishedAsync(db, plan.Id, DocumentTypeRegistry.Fos), copies);
        });
        return result!;
    }
}
