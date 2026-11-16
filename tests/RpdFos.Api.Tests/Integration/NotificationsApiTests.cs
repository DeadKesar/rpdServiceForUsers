using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using RpdFos.Api.Features.Approvals;
using RpdFos.Api.Features.Documents;
using RpdFos.Api.Features.Notifications;
using RpdFos.Api.Features.Users;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;
using Xunit;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// Уведомления (этап 6.1, C3; FR-REV-08): кому и когда создаются события документа, лента, число непрочитанных и
/// «прочитано». Получатели «всем методистам» определяются по ролям учётных записей в базе, а не по заголовку тестового
/// клиента, поэтому методисты здесь — настоящие учётные записи, созданные через страницу администратора. База общая
/// для всех тестов коллекции: уведомления документа отбираются по его id, а получатели рассылки сверяются с базой.
/// Имя у всех созданных здесь учётных записей одно — «Получатель У. О.».
/// </summary>
[Collection(DatabaseCollection.Name)]
public class NotificationsApiTests(DatabaseFixture app)
{
    [Fact]
    public async Task Submission_goes_to_every_active_methodist_but_not_to_the_author_a_teacher_or_a_disabled_account()
    {
        // Автор — сам методист (и преподаватель): о собственной отправке ему уведомление не нужно.
        var (author, authorRecord) = await AccountAsync(AppRoles.Teacher, AppRoles.Methodist);
        var (first, firstRecord) = await AccountAsync(AppRoles.Methodist);
        var (second, secondRecord) = await AccountAsync(AppRoles.Methodist);
        var (_, disabledRecord) = await DisabledAccountAsync(AppRoles.Methodist);
        var (_, teacherRecord) = await AccountAsync(AppRoles.Teacher);
        var created = await DocumentData.CreateAsync(app, author, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.10");
        var version = await DocumentData.FillForSubmissionAsync(author, created);
        Assert.Empty(await FeedAsync(first, created));

        version = await ExpectAsync(author.PostAsJsonAsync(Url(created, "submit"), new DocumentVersionRequest(version)), DocumentStatuses.UnderReview);

        // Получатели — действующие методисты без автора; отключённый, преподаватель и сам автор — вне списка.
        var recipients = await RecipientsAsync(created, NotificationEvents.Submitted);
        Assert.Equal((await ActiveMethodistsAsync()).Where(id => id != authorRecord.Id).Order(), recipients.Order());
        Assert.Contains(firstRecord.Id, recipients);
        Assert.Contains(secondRecord.Id, recipients);
        Assert.DoesNotContain(authorRecord.Id, recipients);
        Assert.DoesNotContain(disabledRecord.Id, recipients);
        Assert.DoesNotContain(teacherRecord.Id, recipients);
        Assert.Empty(await FeedAsync(author, created));

        var note = Assert.Single(await FeedAsync(first, created));
        Assert.Equal(($"Отправлен на проверку: {created.Document.Title}. Автор: {authorRecord.ShortName}", false), (note.Text, note.ReadAt is not null));
        var payload = await PayloadAsync(note);
        Assert.Equal((created.Document.Title, "Б1.О.10", authorRecord.ShortName),
            (payload.GetProperty("title").GetString(), payload.GetProperty("discipline_code").GetString(), payload.GetProperty("by").GetString()));
        Assert.False(string.IsNullOrEmpty(payload.GetProperty("discipline").GetString()));

        // Возврат и повторная отправка с доработки: методистам — «повторно»; автору — только возврат, отправку он совершил сам.
        Assert.Equal(HttpStatusCode.Created, (await first.PostAsJsonAsync(Url(created, "comments"),
            new CommentRequest("/thematic_plan/0/hours", "Разбейте часы по темам"))).StatusCode);
        version = await ExpectAsync(first.PostAsJsonAsync(Url(created, "return"), new ReturnRequest(version, null)), DocumentStatuses.Returned);
        await ExpectAsync(author.PostAsJsonAsync(Url(created, "submit"), new DocumentVersionRequest(version)), DocumentStatuses.UnderReview);

        Assert.Equal([$"Повторно отправлен на проверку: {created.Document.Title}. Автор: {authorRecord.ShortName}", note.Text],
            (await FeedAsync(second, created, NotificationEvents.Submitted)).Select(n => n.Text));
        Assert.Equal([NotificationEvents.ReturnedForRework], (await FeedAsync(author, created)).Select(n => n.EventType));
        await AssertCleanTextsAsync(created);
    }

    [Fact]
    public async Task Author_is_told_about_return_for_rework_and_about_acceptance_but_methodists_are_not()
    {
        var (author, _) = await DocumentData.UserAsync(app);
        var (reviewer, reviewerRecord) = await AccountAsync(AppRoles.Methodist);
        var (other, _) = await AccountAsync(AppRoles.Methodist);
        var created = await DocumentData.CreateAsync(app, author, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.13");
        var (head, _) = await DocumentData.SignatoryAsync(app, created.Document.DisciplineId, SignatoryRoles.DepartmentHead);
        var (umu, _) = await DocumentData.SignatoryAsync(app, created.Document.DisciplineId, SignatoryRoles.UmuHead);
        var version = await DocumentData.FillForSubmissionAsync(author, created);
        version = await ExpectAsync(author.PostAsJsonAsync(Url(created, "submit"), new DocumentVersionRequest(version)), DocumentStatuses.UnderReview);

        // Возврат с замечанием и общим комментарием: автору — число замечаний и комментарий.
        Assert.Equal(HttpStatusCode.Created, (await reviewer.PostAsJsonAsync(Url(created, "comments"),
            new CommentRequest("/thematic_plan/0/hours", "Разбейте часы по темам"))).StatusCode);
        version = await ExpectAsync(reviewer.PostAsJsonAsync(Url(created, "return"), new ReturnRequest(version, " Исправьте до пятницы ")),
            DocumentStatuses.Returned);
        var returned = Assert.Single(await FeedAsync(author, created, NotificationEvents.ReturnedForRework));
        Assert.Equal($"Возвращён на доработку: {created.Document.Title}. Замечаний: 1. Методист: {reviewerRecord.ShortName} Комментарий: Исправьте до пятницы.",
            returned.Text);
        var payload = await PayloadAsync(returned);
        Assert.Equal((1, "Исправьте до пятницы", reviewerRecord.ShortName),
            (payload.GetProperty("remarks").GetInt32(), payload.GetProperty("comment").GetString(), payload.GetProperty("by").GetString()));
        // Это событие автора: методистам — ни совершившему, ни остальным.
        Assert.Empty(await FeedAsync(reviewer, created, NotificationEvents.ReturnedForRework));
        Assert.Empty(await FeedAsync(other, created, NotificationEvents.ReturnedForRework));

        // Автор исправил и отправил снова; приём — автору «согласование начато», согласующим «ждёт отметки».
        var comments = (await author.GetFromJsonAsync<List<CommentItem>>(Url(created, "comments")))!;
        Assert.Equal(HttpStatusCode.OK, (await author.PostAsync(Url(created, $"comments/{comments[0].Id}/resolve"), null)).StatusCode);
        version = await ExpectAsync(author.PostAsJsonAsync(Url(created, "submit"), new DocumentVersionRequest(version)), DocumentStatuses.UnderReview);
        await ExpectAsync(reviewer.PostAsJsonAsync(Url(created, "accept"), new DocumentVersionRequest(version)), DocumentStatuses.Approval);

        var accepted = Assert.Single(await FeedAsync(author, created, NotificationEvents.Accepted));
        Assert.Equal($"Принят: {created.Document.Title}. Согласование начато. Методист: {reviewerRecord.ShortName}", accepted.Text);
        Assert.Equal(DocumentNotifications.ApprovalStarted, (await PayloadAsync(accepted)).GetProperty("approval").GetString());
        foreach (var methodist in new[] { reviewer, other })
        {
            Assert.Empty(await FeedAsync(methodist, created, NotificationEvents.Accepted));
            Assert.Empty(await FeedAsync(methodist, created, NotificationEvents.AwaitingMark));
        }
        var seats = (await DetailsAsync(reviewer, created)).Approval!.Seats;
        foreach (var (signatory, role) in new[] { (head, SignatoryRoles.DepartmentHead), (umu, SignatoryRoles.UmuHead) })
        {
            var seat = seats.Single(s => s.Role == role).Title;
            var awaiting = Assert.Single(await FeedAsync(signatory, created));
            Assert.Equal((NotificationEvents.AwaitingMark, $"Ждёт вашего согласования: {created.Document.Title}. Место в листе согласования: {seat}."),
                (awaiting.EventType, awaiting.Text));
            Assert.Equal([seat], (await PayloadAsync(awaiting)).GetProperty("seats").EnumerateArray().Select(s => s.GetString()));
        }
        await AssertCleanTextsAsync(created);
    }

    [Fact]
    public async Task Author_is_told_that_a_methodist_took_the_draft_for_review()
    {
        var (author, _) = await DocumentData.UserAsync(app);
        var (reviewer, reviewerRecord) = await AccountAsync(AppRoles.Methodist);
        var (other, _) = await AccountAsync(AppRoles.Methodist);
        var created = await DocumentData.CreateAsync(app, author, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.10");

        await ExpectAsync(reviewer.PostAsJsonAsync(Url(created, "take"), new TakeRequest(created.Document.Version, " Автор в отпуске ")),
            DocumentStatuses.UnderReview);

        var taken = Assert.Single(await FeedAsync(author, created));
        Assert.Equal((NotificationEvents.TakenForReview,
                $"Взят на проверку методистом {reviewerRecord.ShortName}: {created.Document.Title}. Причина: Автор в отпуске."),
            (taken.EventType, taken.Text));
        Assert.Equal("Автор в отпуске", (await PayloadAsync(taken)).GetProperty("comment").GetString());
        // Документ взял методист, а не автор отправил: остальным методистам сообщать нечего.
        Assert.Empty(await FeedAsync(reviewer, created));
        Assert.Empty(await FeedAsync(other, created));
        await AssertCleanTextsAsync(created);
    }

    [Fact]
    public async Task Accepted_document_waiting_for_assignment_and_plan_correction_return_reach_the_author()
    {
        var (author, _) = await DocumentData.UserAsync(app);
        var (reviewer, reviewerRecord) = await AccountAsync(AppRoles.Methodist);
        // Председателя УМК правилом не найти — документ остаётся «Принят», пока его не назначат (этап 6, B2).
        var created = await DocumentData.CreateAsync(app, author,
            (await DocumentData.PublishedTemplateAsync(app, DocumentData.SeatsWithMissingSignatory)).Id, "Б1.О.10");
        var version = await DocumentData.FillForSubmissionAsync(author, created);
        version = await ExpectAsync(author.PostAsJsonAsync(Url(created, "submit"), new DocumentVersionRequest(version)), DocumentStatuses.UnderReview);

        version = await ExpectAsync(reviewer.PostAsJsonAsync(Url(created, "accept"), new DocumentVersionRequest(version)), DocumentStatuses.Accepted);

        var accepted = Assert.Single(await FeedAsync(author, created));
        var chair = (await DetailsAsync(reviewer, created)).Approval!.Seats.Single(s => s.SignatoryId is null).Title;
        Assert.Equal($"Принят: {created.Document.Title}. Согласование ждёт назначения согласующих: {chair}. Методист: {reviewerRecord.ShortName}",
            accepted.Text);
        var acceptedPayload = await PayloadAsync(accepted);
        Assert.Equal(DocumentNotifications.ApprovalAwaitingAssignment, acceptedPayload.GetProperty("approval").GetString());
        Assert.Equal([chair], acceptedPayload.GetProperty("seats").EnumerateArray().Select(s => s.GetString()));

        // Исправление данных плана, после которого документ неполон, возвращает его автору: замечание по разделу — и уведомление.
        var hours = JsonNode.Parse((await DetailsAsync(reviewer, created)).Payload.GetProperty("hours_distribution").GetRawText())!;
        hours["semesters"]![0]!["control_types"] = new JsonArray("exam", "course_project");
        var corrected = await reviewer.PatchAsJsonAsync(Url(created, "sections/hours_distribution"),
            new SectionUpdateRequest(version, JsonDocument.Parse(hours.ToJsonString()).RootElement, true));
        Assert.Equal(DocumentStatuses.Returned, (await corrected.Content.ReadFromJsonAsync<SectionSaved>())!.Status);

        var remarks = 0;
        await app.WithDbAsync(async db => remarks = await db.DocumentComments.CountAsync(c => c.DocumentId == created.Document.Id));
        Assert.True(remarks > 0);
        var feed = await FeedAsync(author, created);
        Assert.Equal([NotificationEvents.ReturnedAfterPlanCorrection, NotificationEvents.Accepted], feed.Select(n => n.EventType));
        Assert.Equal($"Возвращён на доработку после исправления данных учебного плана: {created.Document.Title}. Замечаний: {remarks}. " +
                     $"Исправление данных плана: {reviewerRecord.ShortName}",
            feed[0].Text);
        Assert.Equal(remarks, (await PayloadAsync(feed[0])).GetProperty("remarks").GetInt32());
        // Исправил методист, он же принял: автору — оба события, ему самому — только «отправлен на проверку» автора.
        Assert.Equal([NotificationEvents.Submitted], (await FeedAsync(reviewer, created)).Select(n => n.EventType));
        await AssertCleanTextsAsync(created);
    }

    [Fact]
    public async Task Approval_round_notifies_signatories_then_methodists_about_the_return_and_everyone_about_approval()
    {
        var (author, _) = await DocumentData.UserAsync(app);
        var (reviewer, _) = await AccountAsync(AppRoles.Methodist);
        var (other, _) = await AccountAsync(AppRoles.Methodist);
        var created = await DocumentData.CreateAsync(app, author, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.12");
        var (head, headRecord) = await DocumentData.SignatoryAsync(app, created.Document.DisciplineId, SignatoryRoles.DepartmentHead);
        var (umu, umuRecord) = await DocumentData.SignatoryAsync(app, created.Document.DisciplineId, SignatoryRoles.UmuHead);
        var version = await DocumentData.FillForSubmissionAsync(author, created);
        version = await ExpectAsync(author.PostAsJsonAsync(Url(created, "submit"), new DocumentVersionRequest(version)), DocumentStatuses.UnderReview);
        await ExpectAsync(reviewer.PostAsJsonAsync(Url(created, "accept"), new DocumentVersionRequest(version)), DocumentStatuses.Approval);
        Assert.Single(await FeedAsync(head, created, NotificationEvents.AwaitingMark));
        Assert.Single(await FeedAsync(umu, created, NotificationEvents.AwaitingMark));

        // Возврат с согласования: методистам — кто и комментарий; автору и согласующим — ничего.
        version = await ExpectAsync(umu.PostAsJsonAsync(Url(created, "approval/reject"), new RejectRequest(" Нет литературы ")),
            DocumentStatuses.UnderReview);
        foreach (var methodist in new[] { reviewer, other })
        {
            var back = Assert.Single(await FeedAsync(methodist, created, NotificationEvents.ReturnedFromApproval));
            Assert.StartsWith($"Возвращён с согласования: {created.Document.Title}. Согласующий: {umuRecord.ShortName}", back.Text);
            Assert.EndsWith("Комментарий: Нет литературы.", back.Text);
            var payload = await PayloadAsync(back);
            Assert.Equal((umuRecord.ShortName, "Нет литературы"), (payload.GetProperty("by").GetString(), payload.GetProperty("comment").GetString()));
            // Замечаний к полям согласующий не оставил: в тексте о них ни слова, в параметрах — ноль (этап 7, A2).
            Assert.DoesNotContain("Замечаний", back.Text);
            Assert.Equal(0, payload.GetProperty("remarks").GetInt32());
        }
        foreach (var client in new[] { author, head, umu })
            Assert.Empty(await FeedAsync(client, created, NotificationEvents.ReturnedFromApproval));

        // Новый круг: согласующие получают «ждёт вашего согласования» снова, автор — «принят» второй раз.
        await ExpectAsync(reviewer.PostAsJsonAsync(Url(created, "accept"), new DocumentVersionRequest(version)), DocumentStatuses.Approval);
        Assert.Equal(2, (await FeedAsync(head, created, NotificationEvents.AwaitingMark)).Count);
        Assert.Equal(2, (await FeedAsync(umu, created, NotificationEvents.AwaitingMark)).Count);
        Assert.Equal(2, (await FeedAsync(author, created, NotificationEvents.Accepted)).Count);

        // Первая отметка не уведомляет никого; последняя — автора и методистов, но не согласующих.
        await ExpectAsync(umu.PostAsync(Url(created, "approval/approve"), null), DocumentStatuses.Approval);
        Assert.Empty(await FeedAsync(author, created, NotificationEvents.Approved));
        await ExpectAsync(head.PostAsync(Url(created, "approval/approve"), null), DocumentStatuses.Approved);
        foreach (var client in new[] { author, reviewer, other })
        {
            var done = Assert.Single(await FeedAsync(client, created, NotificationEvents.Approved));
            Assert.Equal($"Согласован: {created.Document.Title}.", done.Text);
            Assert.Equal(headRecord.ShortName, (await PayloadAsync(done)).GetProperty("by").GetString());
        }
        foreach (var client in new[] { head, umu })
            Assert.Empty(await FeedAsync(client, created, NotificationEvents.Approved));
        await AssertCleanTextsAsync(created);
    }

    /// <summary>
    /// Этап 7, A2: возврат с замечаниями к полям — в уведомлении методистам их число: неснятые замечания согласующего,
    /// снятое им самим не считается.
    /// </summary>
    [Fact]
    public async Task Return_from_approval_tells_methodists_how_many_field_comments_the_approver_left()
    {
        var (author, _) = await DocumentData.UserAsync(app);
        var (reviewer, _) = await AccountAsync(AppRoles.Methodist);
        var created = await DocumentData.CreateAsync(app, author, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.12");
        var (umu, umuRecord) = await DocumentData.SignatoryAsync(app, created.Document.DisciplineId, SignatoryRoles.UmuHead);
        var version = await DocumentData.FillForSubmissionAsync(author, created);
        version = await ExpectAsync(author.PostAsJsonAsync(Url(created, "submit"), new DocumentVersionRequest(version)), DocumentStatuses.UnderReview);
        await ExpectAsync(reviewer.PostAsJsonAsync(Url(created, "accept"), new DocumentVersionRequest(version)), DocumentStatuses.Approval);

        var ids = new List<Guid>();
        foreach (var (path, text) in new[] { ("/thematic_plan", "Темы"), ("/goals_and_objectives", "Цели"), ("/title_page", "Год") })
        {
            var response = await umu.PostAsJsonAsync(Url(created, "comments"), new CommentRequest(path, text));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            ids.Add((await response.Content.ReadFromJsonAsync<CommentItem>())!.Id);
        }
        Assert.True((await umu.PostAsync(Url(created, $"comments/{ids[0]}/resolve"), null)).IsSuccessStatusCode);
        await ExpectAsync(umu.PostAsJsonAsync(Url(created, "approval/reject"), new RejectRequest("Замечания у полей")),
            DocumentStatuses.UnderReview);

        var back = Assert.Single(await FeedAsync(reviewer, created, NotificationEvents.ReturnedFromApproval));
        Assert.StartsWith($"Возвращён с согласования: {created.Document.Title}. Согласующий: {umuRecord.ShortName}", back.Text);
        Assert.EndsWith(" Замечаний к полям: 2. Комментарий: Замечания у полей.", back.Text);
        var payload = await PayloadAsync(back);
        Assert.Equal((2, "Замечания у полей"), (payload.GetProperty("remarks").GetInt32(), payload.GetProperty("comment").GetString()));
        await AssertCleanTextsAsync(created);
    }

    /// <summary>
    /// Решение автора 15.11.2026: заведующий кафедрой — автор и согласующий. О собственных действиях (отправил,
    /// последним отметил) уведомлений он не получает, а два уведомления одного приёма — «принят» и «ждёт вашего
    /// согласования» — стоят в ленте в порядке событий.
    /// </summary>
    [Fact]
    public async Task Author_who_is_also_the_signatory_gets_no_notice_of_own_actions_and_the_rest_in_event_order()
    {
        var disciplineId = await DocumentData.DisciplineAsync(app, "Б1.О.13");
        var (head, _) = await DocumentData.SignatoryAsync(app, disciplineId, SignatoryRoles.DepartmentHead, $"{AppRoles.Teacher},{AppRoles.Approver}");
        var (umu, _) = await DocumentData.SignatoryAsync(app, disciplineId, SignatoryRoles.UmuHead);
        var (reviewer, _) = await AccountAsync(AppRoles.Methodist);
        var created = await DocumentData.CreateAsync(app, head, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.13");
        var version = await DocumentData.FillForSubmissionAsync(head, created);

        version = await ExpectAsync(head.PostAsJsonAsync(Url(created, "submit"), new DocumentVersionRequest(version)), DocumentStatuses.UnderReview);
        Assert.Single(await FeedAsync(reviewer, created, NotificationEvents.Submitted));
        Assert.Empty(await FeedAsync(head, created));

        await ExpectAsync(reviewer.PostAsJsonAsync(Url(created, "accept"), new DocumentVersionRequest(version)), DocumentStatuses.Approval);
        var feed = await FeedAsync(head, created);
        Assert.Equal([NotificationEvents.AwaitingMark, NotificationEvents.Accepted], feed.Select(n => n.EventType));
        Assert.True(feed[0].CreatedAt > feed[1].CreatedAt);

        // Последним отмечает автор: «согласован» получают методисты, он сам — нет.
        await ExpectAsync(umu.PostAsync(Url(created, "approval/approve"), null), DocumentStatuses.Approval);
        await ExpectAsync(head.PostAsync(Url(created, "approval/approve"), null), DocumentStatuses.Approved);
        Assert.Single(await FeedAsync(reviewer, created, NotificationEvents.Approved));
        Assert.Equal([NotificationEvents.AwaitingMark, NotificationEvents.Accepted], (await FeedAsync(head, created)).Select(n => n.EventType));
        await AssertCleanTextsAsync(created);
    }

    [Fact]
    public async Task Manual_assignment_notifies_the_assigned_only_while_the_round_is_running()
    {
        var (author, _) = await DocumentData.UserAsync(app);
        var (reviewer, _) = await AccountAsync(AppRoles.Methodist);
        var (admin, _) = await DocumentData.UserAsync(app, AppRoles.Admin);
        var (chair, chairRecord) = await DocumentData.UserAsync(app, AppRoles.Approver);
        var (deputy, deputyRecord) = await DocumentData.UserAsync(app, AppRoles.Approver);
        var created = await DocumentData.CreateAsync(app, author,
            (await DocumentData.PublishedTemplateAsync(app, DocumentData.SeatsWithMissingSignatory)).Id, "Б1.О.12");
        var (head, _) = await DocumentData.SignatoryAsync(app, created.Document.DisciplineId, SignatoryRoles.DepartmentHead);
        var (umu, _) = await DocumentData.SignatoryAsync(app, created.Document.DisciplineId, SignatoryRoles.UmuHead);
        var version = await DocumentData.FillForSubmissionAsync(author, created);
        version = await ExpectAsync(author.PostAsJsonAsync(Url(created, "submit"), new DocumentVersionRequest(version)), DocumentStatuses.UnderReview);
        await ExpectAsync(reviewer.PostAsJsonAsync(Url(created, "accept"), new DocumentVersionRequest(version)), DocumentStatuses.Accepted);
        string Seat(string role) => Url(created, $"approval/seats/{role}/assign");

        // «Принят»: круга ещё нет, назначенного председателя уведомит старт, а не назначение.
        var assigned = await admin.PostAsJsonAsync(Seat(SignatoryRoles.UmkChair), new AssignRequest(chairRecord.Id));
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        Assert.Empty(await FeedAsync(chair, created));
        Assert.Empty(await FeedAsync(head, created));

        var afterAssign = (await assigned.Content.ReadFromJsonAsync<ApprovalChanged>())!.Version;
        await ExpectAsync(reviewer.PostAsJsonAsync(Url(created, "approval/start"), new DocumentVersionRequest(afterAssign)), DocumentStatuses.Approval);
        foreach (var signatory in new[] { chair, head, umu })
            Assert.Single(await FeedAsync(signatory, created, NotificationEvents.AwaitingMark));
        Assert.Empty(await FeedAsync(reviewer, created, NotificationEvents.AwaitingMark));

        // Круг идёт: назначенный вместо начальника УМУ получает уведомление сразу, прежний нового не получает.
        Assert.Empty(await FeedAsync(deputy, created));
        Assert.Equal(HttpStatusCode.OK, (await reviewer.PostAsJsonAsync(Seat(SignatoryRoles.UmuHead), new AssignRequest(deputyRecord.Id))).StatusCode);
        var replaced = Assert.Single(await FeedAsync(deputy, created));
        var seat = (await DetailsAsync(reviewer, created)).Approval!.Seats.Single(s => s.Role == SignatoryRoles.UmuHead).Title;
        Assert.Equal((NotificationEvents.AwaitingMark, $"Ждёт вашего согласования: {created.Document.Title}. Место в листе согласования: {seat}."),
            (replaced.EventType, replaced.Text));
        Assert.Single(await FeedAsync(umu, created, NotificationEvents.AwaitingMark));
        // Тот же человек: повторное назначение ничего не меняет и не уведомляет.
        Assert.Equal(HttpStatusCode.OK, (await reviewer.PostAsJsonAsync(Seat(SignatoryRoles.UmuHead), new AssignRequest(deputyRecord.Id))).StatusCode);
        Assert.Single(await FeedAsync(deputy, created));
        await AssertCleanTextsAsync(created);
    }

    [Fact]
    public async Task Template_without_electronic_seats_tells_the_author_about_acceptance_and_approval_in_order()
    {
        var (author, _) = await DocumentData.UserAsync(app);
        var (reviewer, _) = await AccountAsync(AppRoles.Methodist);
        var (other, _) = await AccountAsync(AppRoles.Methodist);
        var created = await DocumentData.CreateAsync(app, author, (await DocumentData.PublishedTemplateAsync(app, [])).Id, "Б1.О.10");
        var version = await DocumentData.FillForSubmissionAsync(author, created);
        version = await ExpectAsync(author.PostAsJsonAsync(Url(created, "submit"), new DocumentVersionRequest(version)), DocumentStatuses.UnderReview);

        await ExpectAsync(reviewer.PostAsJsonAsync(Url(created, "accept"), new DocumentVersionRequest(version)), DocumentStatuses.Approved);

        // Отмечать некому: документ согласован при приёме. Автору — оба события, в порядке истории статусов.
        var feed = await FeedAsync(author, created);
        Assert.Equal([NotificationEvents.Approved, NotificationEvents.Accepted], feed.Select(n => n.EventType));
        Assert.True(feed[0].CreatedAt > feed[1].CreatedAt);
        Assert.Equal(DocumentNotifications.ApprovalNotRequired, (await PayloadAsync(feed[1])).GetProperty("approval").GetString());
        Assert.Contains("Электронных согласующих в листе согласования нет", feed[1].Text);
        // Принявший методист совершил действие; остальным — «согласован».
        Assert.Empty(await FeedAsync(reviewer, created, NotificationEvents.Approved));
        Assert.Single(await FeedAsync(other, created, NotificationEvents.Approved));
        await AssertCleanTextsAsync(created);
    }

    [Fact]
    public async Task Feed_count_and_read_marks_work_for_own_notifications_only()
    {
        var (me, mine) = await DocumentData.UserAsync(app);
        var (_, theirs) = await DocumentData.UserAsync(app);
        var now = DateTimeOffset.UtcNow;
        Notification Make(Guid recipient, string text, int minutes) => new()
        {
            RecipientId = recipient, EventType = NotificationEvents.Submitted, CreatedAt = now.AddMinutes(minutes),
            Payload = new JsonObject { ["text"] = text }.ToJsonString()
        };
        Notification[] own = [Make(mine.Id, "Первое.", -3), Make(mine.Id, "Второе.", -2), Make(mine.Id, "Третье.", -1)];
        var foreign = Make(theirs.Id, "Чужое.", -1);
        await app.WithDbAsync(async db =>
        {
            db.Notifications.AddRange(own);
            db.Notifications.Add(foreign);
            await db.SaveChangesAsync();
        });
        var theirClient = app.CreateClientFor(AppRoles.Teacher, theirs.IdentitySubject);

        // Лента — от новых к старым, только свои; limit ограничивает число и держится в пределах 1…100.
        var feed = (await me.GetFromJsonAsync<List<NotificationItem>>("/api/notifications"))!;
        Assert.Equal(["Третье.", "Второе.", "Первое."], feed.Select(n => n.Text));
        Assert.All(feed, n =>
        {
            Assert.Equal(NotificationEvents.Submitted, n.EventType);
            Assert.Null(n.DocumentId);
            Assert.Null(n.ReadAt);
        });
        Assert.Equal(["Третье.", "Второе."], (await me.GetFromJsonAsync<List<NotificationItem>>("/api/notifications?limit=2"))!.Select(n => n.Text));
        Assert.Single((await me.GetFromJsonAsync<List<NotificationItem>>("/api/notifications?limit=0"))!);
        Assert.Equal(3, (await me.GetFromJsonAsync<List<NotificationItem>>("/api/notifications?limit=100000"))!.Count);
        Assert.Equal((3, 1), (await UnreadAsync(me), await UnreadAsync(theirClient)));

        // «Прочитано» для одного: счётчик уменьшается, время ставится один раз.
        var target = feed[1];
        Assert.Equal(HttpStatusCode.NoContent, (await me.PostAsync($"/api/notifications/{target.Id}/read", null)).StatusCode);
        Assert.Equal(2, await UnreadAsync(me));
        var readAt = (await ItemAsync(me, target.Id)).ReadAt;
        Assert.NotNull(readAt);
        Assert.Equal(HttpStatusCode.NoContent, (await me.PostAsync($"/api/notifications/{target.Id}/read", null)).StatusCode);
        Assert.Equal((readAt, 2), ((await ItemAsync(me, target.Id)).ReadAt, await UnreadAsync(me)));

        // Чужое уведомление неотличимо от несуществующего — 404, и оно остаётся непрочитанным.
        Assert.Equal(HttpStatusCode.NotFound, (await me.PostAsync($"/api/notifications/{foreign.Id}/read", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await me.PostAsync($"/api/notifications/{Guid.NewGuid()}/read", null)).StatusCode);
        await app.WithDbAsync(async db => Assert.Null((await db.Notifications.SingleAsync(n => n.Id == foreign.Id)).ReadAt));

        // «Прочитать все»: только свои непрочитанные; прочитанное не перезаписывается, повтор безвреден.
        Assert.Equal(HttpStatusCode.NoContent, (await me.PostAsync("/api/notifications/read-all", null)).StatusCode);
        Assert.Equal((0, 1), (await UnreadAsync(me), await UnreadAsync(theirClient)));
        Assert.Equal(readAt, (await ItemAsync(me, target.Id)).ReadAt);
        Assert.Equal(HttpStatusCode.NoContent, (await me.PostAsync("/api/notifications/read-all", null)).StatusCode);
        Assert.All((await me.GetFromJsonAsync<List<NotificationItem>>("/api/notifications"))!, n => Assert.NotNull(n.ReadAt));
    }

    [Fact]
    public async Task User_without_a_directory_record_has_an_empty_feed_and_anonymous_is_refused()
    {
        // Учётная запись не связана со справочником: получателем быть не может — пустая лента, а не ошибка у колокольчика.
        var unlinked = app.CreateClientFor(AppRoles.Teacher);
        Assert.Empty((await unlinked.GetFromJsonAsync<List<NotificationItem>>("/api/notifications"))!);
        Assert.Equal(0, await UnreadAsync(unlinked));
        Assert.Equal(HttpStatusCode.NotFound, (await unlinked.PostAsync($"/api/notifications/{Guid.NewGuid()}/read", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await unlinked.PostAsync("/api/notifications/read-all", null)).StatusCode);

        var anonymous = app.CreateClientFor(null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/notifications")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/notifications/unread-count")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/notifications/read-all", null)).StatusCode);
    }

    // Учётная запись с ролями — через страницу администратора, как в жизни: получатели «всем методистам» берутся из
    // ролей учётных записей. Отключённая — бессрочная блокировка (этап 5.2).
    private Task<(HttpClient Client, Employee Employee)> AccountAsync(params string[] roles) => CreateAccountAsync(roles, disabled: false);

    private Task<(HttpClient Client, Employee Employee)> DisabledAccountAsync(string role) => CreateAccountAsync([role], disabled: true);

    private async Task<(HttpClient Client, Employee Employee)> CreateAccountAsync(string[] roles, bool disabled)
    {
        var admin = app.CreateClientFor(AppRoles.Admin);
        var created = await admin.PostAsJsonAsync("/api/admin/users", new CreateUserRequest(
            $"notice-{Guid.NewGuid():N}@test.local", "Получатель Уведомлений Обезличенный", "Test-Passw0rd", roles));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var account = (await created.Content.ReadFromJsonAsync<UserItem>())!;
        if (disabled)
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/admin/users/{account.Id}/deactivate", null)).StatusCode);
        Employee? employee = null;
        await app.WithDbAsync(async db => employee = await db.Employees.SingleAsync(e => e.Id == account.DirectoryUserId));
        return (app.CreateClientFor(string.Join(',', roles), employee!.IdentitySubject), employee);
    }

    // Записи справочника с действующей учётной записью методиста — независимая от кода сверка получателей рассылки.
    private async Task<List<Guid>> ActiveMethodistsAsync()
    {
        var found = new List<Guid>();
        await app.WithDbAsync(async db =>
        {
            var accounts = await (
                    from ur in db.UserRoles
                    join r in db.Roles on ur.RoleId equals r.Id
                    join u in db.Users on ur.UserId equals u.Id
                    where r.Name == AppRoles.Methodist
                    select new { u.Id, u.LockoutEnd })
                .ToListAsync();
            var subjects = accounts.Where(a => a.LockoutEnd != DateTimeOffset.MaxValue).Select(a => a.Id.ToString()).ToList();
            found = await db.Employees.Where(e => e.IsActive && e.CanLogin && e.IdentitySubject != null && subjects.Contains(e.IdentitySubject))
                .Select(e => e.Id).ToListAsync();
        });
        return found;
    }

    private async Task<List<Guid>> RecipientsAsync(DocumentDetails document, string eventType)
    {
        var recipients = new List<Guid>();
        await app.WithDbAsync(async db => recipients = await db.Notifications
            .Where(n => n.DocumentId == document.Document.Id && n.EventType == eventType).Select(n => n.RecipientId).ToListAsync());
        return recipients;
    }

    // Уведомления пользователя по документу — от новых к старым, как в ленте колокольчика.
    private static async Task<List<NotificationItem>> FeedAsync(HttpClient client, DocumentDetails document, string? eventType = null) =>
        (await client.GetFromJsonAsync<List<NotificationItem>>($"/api/notifications?limit={NotificationsModule.MaxLimit}"))!
            .Where(n => n.DocumentId == document.Document.Id && (eventType is null || n.EventType == eventType)).ToList();

    private static async Task<NotificationItem> ItemAsync(HttpClient client, Guid id) =>
        Assert.Single((await client.GetFromJsonAsync<List<NotificationItem>>("/api/notifications"))!, n => n.Id == id);

    private async Task<JsonElement> PayloadAsync(NotificationItem item)
    {
        var payload = default(JsonElement);
        await app.WithDbAsync(async db =>
            payload = JsonDocument.Parse((await db.Notifications.SingleAsync(n => n.Id == item.Id)).Payload).RootElement.Clone());
        return payload;
    }

    // Фразы уведомлений документа читаются: точка в конце, без «..» и двойных пробелов («Фамилия И. О.» уже кончается точкой).
    private async Task AssertCleanTextsAsync(DocumentDetails document)
    {
        var texts = new List<string>();
        await app.WithDbAsync(async db => texts = (await db.Notifications.Where(n => n.DocumentId == document.Document.Id)
            .Select(n => n.Payload).ToListAsync()).Select(p => JsonDocument.Parse(p).RootElement.GetProperty("text").GetString()!).ToList());
        Assert.NotEmpty(texts);
        Assert.All(texts, t =>
        {
            Assert.EndsWith(".", t);
            Assert.DoesNotContain("..", t);
            Assert.DoesNotContain("  ", t);
        });
    }

    private static async Task<int> UnreadAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<UnreadCount>("/api/notifications/unread-count"))!.Unread;

    private static async Task<DocumentDetails> DetailsAsync(HttpClient client, DocumentDetails document) =>
        (await client.GetFromJsonAsync<DocumentDetails>(Url(document, "")))!;

    private static string Url(DocumentDetails document, string action) =>
        $"/api/documents/{document.Document.Id}" + (action.Length > 0 ? $"/{action}" : "");

    // Ответ на смену статуса или действие согласования: ожидаемый статус; возвращает новую версию.
    private static async Task<int> ExpectAsync(Task<HttpResponseMessage> request, string status)
    {
        var response = await request;
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        var changed = (await response.Content.ReadFromJsonAsync<StatusAnswer>())!;
        Assert.Equal(status, changed.Status);
        return changed.Version;
    }

    private sealed record StatusAnswer(int Version, string Status);
}
