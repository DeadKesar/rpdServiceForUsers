using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RpdFos.Api.Features.Documents;
using RpdFos.Api.Features.Notifications;
using RpdFos.Api.Features.Profile;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Directory;
using RpdFos.Core.Documents;
using RpdFos.Core.Mail;
using RpdFos.Core.Reminders;
using Xunit;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// Напоминания о сроке доработки (этап 7, A6, FR-REV-09): автору — за три дня до срока, заведующему кафедрой дисциплины и
/// методистам — когда срок вышел; каждое — один раз за одно пребывание документа на доработке. Время подменено
/// (<see cref="TestClock"/>, пояс сервера — UTC+3), задача запускается вызовом — расписание в тестовом хосте выключено
/// (<c>Reminders:Enabled=false</c>).
/// <para>
/// Документ проходит настоящий путь до «На доработке» через API, а время перехода в историю статусов ставится прямо в базу:
/// так сдвигает дату и автор при проверке в браузере. База общая для тестов коллекции, а задача смотрит все документы на
/// доработке, поэтому у каждого теста свои сутки отсчёта — за сто с лишним суток до настоящих и через сорок суток друг от
/// друга: часы теста идут не дальше двадцати суток от его отсчёта и до документов других тестов не доходят — те лежат в
/// будущем этих часов или давно обработаны. Проверки — по уведомлениям своего документа, а не по числу, которое вернула
/// задача.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class ReworkReminderTests(DatabaseFixture app)
{
    private const string Discipline = "Б1.О.10";
    private static readonly TimeZoneInfo Zone = TestClock.ZoneWithOffset(3);
    private static int _windows;

    // Отсчёт теста: 13:00 по местному (дата по местному и по UTC совпадает) за 140, 180, 220… суток до настоящих.
    private readonly DateTimeOffset _entered =
        new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(-(100 + 40 * Interlocked.Increment(ref _windows))).AddHours(10);

    private FakeMailSender Mail => app.Services.GetRequiredService<FakeMailSender>();

    // Момент через столько суток после возврата на доработку.
    private DateTimeOffset At(double days) => _entered.AddDays(days);

    [Fact]
    public async Task The_author_is_warned_three_days_before_the_term_once_and_not_earlier()
    {
        var c = await ReturnedAsync();
        var id = c.Created.Document.Id;

        // 10 суток — ничего: до предупреждения ещё сутки.
        await RunAsync(At(10));
        Assert.Empty(await RemindersAsync(id));

        // 11 суток — автору, со ссылкой и сроком; заведующему и методистам рано.
        await RunAsync(At(11));
        var warning = Assert.Single(await RemindersAsync(id));
        Assert.Equal((NotificationEvents.ReworkDeadlineNear, c.AuthorRecord.Id, id, At(11)),
            (warning.EventType, warning.RecipientId, warning.DocumentId, warning.CreatedAt));
        Assert.Equal($"Срок доработки истекает: {c.Created.Document.Title}. На доработке с {Local(At(0))}. Срок доработки — до {Local(At(14))}.",
            warning.Text);
        Assert.Equal((c.Created.Document.Title, At(0), At(14)),
            (warning.Payload.GetProperty("title").GetString(), warning.Payload.GetProperty("since").GetDateTimeOffset(),
                warning.Payload.GetProperty("deadline").GetDateTimeOffset()));
        var item = Assert.Single(await FeedAsync(c.Author), n => n.EventType == NotificationEvents.ReworkDeadlineNear);
        Assert.Equal((id, null), (item.DocumentId, item.ReadAt));

        // Тот же день и следующие, пока срок не вышел, второго предупреждения не дают.
        foreach (var day in new[] { 11.5, 12, 13, 13.9 })
            await RunAsync(At(day));
        Assert.Single(await RemindersAsync(id));
    }

    [Fact]
    public async Task The_head_and_methodists_get_the_escalation_when_the_term_is_over_once_and_the_author_is_not_warned_again()
    {
        var c = await ReturnedAsync();
        var id = c.Created.Document.Id;
        var (_, head) = await DocumentData.SignatoryAsync(app, c.Created.Document.DisciplineId, SignatoryRoles.DepartmentHead);
        await AddSeatAsync(id, head.Id);
        await AccountData.CreateAsync(app, [AppRoles.Methodist]);

        // Задача идёт каждые сутки — с десятых по двадцатые.
        for (var day = 10; day <= 20; day++)
            await RunAsync(At(day));

        var reminders = await RemindersAsync(id);
        var warning = Assert.Single(reminders, r => r.EventType == NotificationEvents.ReworkDeadlineNear);
        Assert.Equal((c.AuthorRecord.Id, At(11)), (warning.RecipientId, warning.CreatedAt));

        // На 14-е сутки — заведующему и каждому действующему методисту, по одному уведомлению; автору — нет.
        var overdue = reminders.Where(r => r.EventType == NotificationEvents.ReworkOverdue).ToList();
        var expected = (await ActiveMethodistsAsync()).Append(head.Id).Distinct().Order().ToList();
        Assert.Equal(expected, overdue.Select(r => r.RecipientId).Order());
        Assert.DoesNotContain(c.AuthorRecord.Id, overdue.Select(r => r.RecipientId));
        Assert.Contains(c.ReviewerRecord.Id, overdue.Select(r => r.RecipientId));
        Assert.All(overdue, r =>
        {
            Assert.Equal((id, At(14)), (r.DocumentId, r.CreatedAt));
            Assert.StartsWith($"На доработке больше 14 дней: {c.Created.Document.Title}. ", r.Text);
            Assert.Contains($"Автор: {c.AuthorRecord.ShortName}", r.Text);
            Assert.EndsWith($"На доработке с {Local(At(0))}.", r.Text);
            Assert.Equal((14, c.AuthorRecord.ShortName, At(0)),
                (r.Payload.GetProperty("days").GetInt32(), r.Payload.GetProperty("author").GetString(),
                    r.Payload.GetProperty("since").GetDateTimeOffset()));
        });

        // Методист видит уведомление в колокольчике со ссылкой на документ.
        var item = Assert.Single(await FeedAsync(c.Reviewer), n => n.EventType == NotificationEvents.ReworkOverdue);
        Assert.Equal((id, null), (item.DocumentId, item.ReadAt));
        Assert.DoesNotContain(await FeedAsync(c.Author), n => n.EventType == NotificationEvents.ReworkOverdue);
    }

    [Fact]
    public async Task A_new_return_starts_the_count_again_and_a_document_not_on_rework_is_left_alone()
    {
        var c = await ReturnedAsync();
        var id = c.Created.Document.Id;
        await RunAsync(At(11));
        await RunAsync(At(14));
        var (warned, firstOverdue) = Counts(await RemindersAsync(id));
        Assert.Equal(1, warned);
        Assert.True(firstOverdue > 0);

        // Автор исправил и отправил: документ на проверке, а не на доработке — сколько бы суток ни прошло, ничего нет.
        var version = await ExpectAsync(c.Author.PostAsJsonAsync(Url(c.Created, "submit"), new DocumentVersionRequest(c.Version)),
            DocumentStatuses.UnderReview);
        await RunAsync(At(60));
        Assert.Equal((1, firstOverdue), Counts(await RemindersAsync(id)));

        // Методист вернул снова — на 16-е сутки первого пребывания: отсчёт заново.
        Assert.Equal(HttpStatusCode.Created, (await c.Reviewer.PostAsJsonAsync(Url(c.Created, "comments"),
            new CommentRequest("/thematic_plan/0/hours", "Ещё раз проверьте часы"))).StatusCode);
        await ExpectAsync(c.Reviewer.PostAsJsonAsync(Url(c.Created, "return"), new ReturnRequest(version, null)), DocumentStatuses.Returned);
        await EnteredReworkAsync(id, At(16));

        await RunAsync(At(26));
        Assert.Equal((1, firstOverdue), Counts(await RemindersAsync(id)));

        // 11 суток нового пребывания — снова предупреждение автору; 14 — снова эскалация. Прежние остаются на месте.
        await RunAsync(At(27));
        var reminders = await RemindersAsync(id);
        Assert.Equal((2, firstOverdue), Counts(reminders));
        var second = reminders.Where(r => r.EventType == NotificationEvents.ReworkDeadlineNear).OrderBy(r => r.CreatedAt).Last();
        Assert.Equal((c.AuthorRecord.Id, At(27)), (second.RecipientId, second.CreatedAt));
        Assert.Contains($"На доработке с {Local(At(16))}", second.Text);

        await RunAsync(At(30));
        reminders = await RemindersAsync(id);
        Assert.Equal((2, firstOverdue * 2), Counts(reminders));
        Assert.All(reminders.Where(r => r.EventType == NotificationEvents.ReworkOverdue && r.CreatedAt == At(30)),
            r => Assert.Contains($"На доработке с {Local(At(16))}", r.Text));

        // И в новом пребывании каждое напоминание — один раз.
        await RunAsync(At(31));
        await RunAsync(At(45));
        Assert.Equal((2, firstOverdue * 2), Counts(await RemindersAsync(id)));
    }

    [Fact]
    public async Task When_the_term_is_already_over_at_the_first_run_only_the_escalation_goes()
    {
        var c = await ReturnedAsync();
        var id = c.Created.Document.Id;

        // Задачу включили поздно (или сервер не работал): «срок истекает» о прошедшем сроке было бы неправдой.
        await RunAsync(At(15));
        await RunAsync(At(16));

        var reminders = await RemindersAsync(id);
        Assert.DoesNotContain(reminders, r => r.EventType == NotificationEvents.ReworkDeadlineNear);
        Assert.Contains(reminders, r => r.EventType == NotificationEvents.ReworkOverdue && r.RecipientId == c.ReviewerRecord.Id);
        Assert.All(reminders, r => Assert.Equal(At(15), r.CreatedAt));
    }

    [Fact]
    public async Task The_terms_come_from_the_settings()
    {
        var c = await ReturnedAsync();
        var id = c.Created.Document.Id;
        var terms = new RemindersSettings { ReworkDays = 5, WarnDaysBefore = 2 };

        await RunAsync(At(2.9), terms);
        Assert.Empty(await RemindersAsync(id));

        await RunAsync(At(3), terms);
        var warning = Assert.Single(await RemindersAsync(id));
        Assert.Equal((NotificationEvents.ReworkDeadlineNear, c.AuthorRecord.Id), (warning.EventType, warning.RecipientId));
        Assert.Contains($"Срок доработки — до {Local(At(5))}", warning.Text);

        await RunAsync(At(5), terms);
        var overdue = (await RemindersAsync(id)).Where(r => r.EventType == NotificationEvents.ReworkOverdue).ToList();
        Assert.Contains(overdue, r => r.RecipientId == c.ReviewerRecord.Id);
        Assert.All(overdue, r => Assert.StartsWith($"На доработке больше 5 дней: {c.Created.Document.Title}. ", r.Text));
    }

    [Fact]
    public async Task The_head_gets_a_link_only_when_he_can_open_the_document_and_is_reminded_once_either_way()
    {
        // Первый документ — прямо с проверки: заведующий в нём не участник. Во втором у него строка согласования.
        var outsider = await ReturnedAsync();
        var member = await ReturnedAsync();
        var (headClient, head) = await DocumentData.SignatoryAsync(app, outsider.Created.Document.DisciplineId, SignatoryRoles.DepartmentHead);
        await AddSeatAsync(member.Created.Document.Id, head.Id);

        await RunAsync(At(14));
        await RunAsync(At(15));
        await RunAsync(At(20));

        // Заведующий получает оба уведомления по одному разу; ссылка — только на документ, который он откроет.
        var outsiderNote = Assert.Single(await RemindersAsync(outsider.Created.Document.Id), r => r.RecipientId == head.Id);
        var memberNote = Assert.Single(await RemindersAsync(member.Created.Document.Id), r => r.RecipientId == head.Id);
        Assert.Equal((null, member.Created.Document.Id), (outsiderNote.DocumentId, memberNote.DocumentId));
        Assert.Equal(outsider.Created.Document.Id, outsiderNote.Payload.GetProperty("document_id").GetGuid());
        Assert.StartsWith($"На доработке больше 14 дней: {outsider.Created.Document.Title}. ", outsiderNote.Text);

        // Ссылка есть там, где API документ покажет, и нет там, где ответил бы 403.
        Assert.Equal(HttpStatusCode.Forbidden, (await headClient.GetAsync($"/api/documents/{outsider.Created.Document.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await headClient.GetAsync($"/api/documents/{member.Created.Document.Id}")).StatusCode);

        // Методисту ссылка — на оба документа: он видит любой.
        foreach (var d in new[] { outsider, member })
        {
            var note = Assert.Single(await RemindersAsync(d.Created.Document.Id), r => r.RecipientId == d.ReviewerRecord.Id);
            Assert.Equal(d.Created.Document.Id, note.DocumentId);
        }
    }

    [Fact]
    public async Task Only_methodists_get_the_escalation_when_the_head_is_not_found_or_cannot_sign_in()
    {
        var disciplineId = await DocumentData.DisciplineAsync(app, Discipline);
        var original = await DepartmentOfAsync(disciplineId);
        try
        {
            // 1. У дисциплины нет кафедры — заведующего правило не находит.
            await SetDepartmentAsync(disciplineId, original);
            var noDepartment = await ReturnedAsync();
            await SetDepartmentAsync(disciplineId, null);
            await RunAsync(At(14));
            await AssertOverdueToAsync(noDepartment, await ActiveMethodistsAsync());

            // 2. Заведующий на кафедре есть, но учётной записи у него нет: уведомление ему никто не прочтёт.
            var withoutAccount = await AddDepartmentAsync();
            await AddHeadAsync(withoutAccount.Id, SignatoryRoles.DepartmentHead);
            await SetDepartmentAsync(disciplineId, original);
            var noAccount = await ReturnedAsync();
            await SetDepartmentAsync(disciplineId, withoutAccount.Id);
            await RunAsync(At(14));
            await AssertOverdueToAsync(noAccount, await ActiveMethodistsAsync());

            // 3. Заведующего нет, а исполняющий обязанности — с учётной записью: ему уведомление идёт, как в листе согласования.
            var withActing = await AddDepartmentAsync();
            var acting = await AddHeadAsync(withActing.Id, SignatoryRoles.DepartmentHeadActing);
            await AccountData.CreateAsync(app, [AppRoles.Approver], acting.Id);
            await SetDepartmentAsync(disciplineId, original);
            var actingCase = await ReturnedAsync();
            await SetDepartmentAsync(disciplineId, withActing.Id);
            await RunAsync(At(14));
            await AssertOverdueToAsync(actingCase, (await ActiveMethodistsAsync()).Append(acting.Id));
        }
        finally
        {
            await SetDepartmentAsync(disciplineId, original);
        }
    }

    [Fact]
    public async Task Reminders_are_mailed_at_once_by_default_and_follow_the_users_settings()
    {
        var c = await ReturnedAsync();
        var id = c.Created.Document.Id;
        var (muted, _, mutedEmail, _) = await AccountData.CreateAsync(app, [AppRoles.Methodist]);
        await AccountData.SaveSettingsAsync(muted, (NotificationEvents.ReworkOverdue, true, "off"));

        // Оба события — в таблице настроек профиля, «в интерфейсе» включено, почта — «сразу».
        var table = (await c.Author.GetFromJsonAsync<List<NotificationSettingItem>>("/api/profile/notification-settings"))!;
        Assert.Equal([(NotificationEvents.ReworkDeadlineNear, "Срок доработки документа истекает", true, "immediate"),
                (NotificationEvents.ReworkOverdue, "Документ на доработке дольше срока", true, "immediate")],
            table.Where(r => r.EventType is NotificationEvents.ReworkDeadlineNear or NotificationEvents.ReworkOverdue)
                .Select(r => (r.EventType, r.Title, r.InApp, r.Mail)));

        // Предупреждение — автору письмом в пределах минуты: тема — первое предложение, ниже ссылка на документ.
        await RunAsync(At(11));
        await MailJobAsync(At(11).AddMinutes(1));
        var warning = Assert.Single(Mail.SentTo(c.AuthorEmail));
        Assert.Equal($"Срок доработки истекает: {c.Created.Document.Title}", warning.Subject);
        Assert.Contains($"https://rpdfos.test/documents/{id}", warning.Body);

        // Эскалация — методисту, у которого она не выключена; у выключившего письма нет, а событие в ленте остаётся.
        await RunAsync(At(14));
        await MailJobAsync(At(14).AddMinutes(1));
        var escalation = Assert.Single(Mail.SentTo(c.ReviewerEmail));
        Assert.Equal($"На доработке больше 14 дней: {c.Created.Document.Title}", escalation.Subject);
        Assert.Contains($"https://rpdfos.test/documents/{id}", escalation.Body);
        Assert.Empty(Mail.SentTo(mutedEmail));
        Assert.Single(await FeedAsync(muted), n => n.EventType == NotificationEvents.ReworkOverdue && n.DocumentId == id);
        Assert.Single(Mail.SentTo(c.AuthorEmail));
    }

    // Документ автора с учётной записью дошёл до «На доработке»: отправлен, методист оставил замечание и вернул. Время
    // перехода — отсчёт теста.
    private async Task<Case> ReturnedAsync(string code = Discipline)
    {
        var (author, authorRecord, authorEmail, _) = await AccountData.CreateAsync(app, [AppRoles.Teacher]);
        var (reviewer, reviewerRecord, reviewerEmail, _) = await AccountData.CreateAsync(app, [AppRoles.Methodist]);
        var created = await DocumentData.CreateAsync(app, author, (await DocumentData.PublishedTemplateAsync(app)).Id, code);
        var version = await DocumentData.FillForSubmissionAsync(author, created);
        version = await ExpectAsync(author.PostAsJsonAsync(Url(created, "submit"), new DocumentVersionRequest(version)), DocumentStatuses.UnderReview);
        Assert.Equal(HttpStatusCode.Created, (await reviewer.PostAsJsonAsync(Url(created, "comments"),
            new CommentRequest("/thematic_plan/0/hours", "Разбейте часы по темам"))).StatusCode);
        version = await ExpectAsync(reviewer.PostAsJsonAsync(Url(created, "return"), new ReturnRequest(version, null)), DocumentStatuses.Returned);
        await EnteredReworkAsync(created.Document.Id, _entered);
        return new Case(author, authorRecord, authorEmail, reviewer, reviewerRecord, reviewerEmail, created, version);
    }

    // Время последнего перехода в «На доработке» — как сдвигает дату автор при проверке в базе разработки.
    private Task EnteredReworkAsync(Guid documentId, DateTimeOffset at) => app.WithDbAsync(async db =>
    {
        var row = await db.DocumentStatusHistory.Where(h => h.DocumentId == documentId && h.ToStatus == DocumentStatuses.Returned)
            .OrderByDescending(h => h.ChangedAt).FirstAsync();
        row.ChangedAt = at;
        await db.SaveChangesAsync();
    });

    // Задача на подменённых часах; сроки — по умолчанию (14 и 3 суток) или заданные тестом.
    private async Task<int> RunAsync(DateTimeOffset now, RemindersSettings? terms = null)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var job = new ReworkReminders(
            services.GetRequiredService<AppDbContext>(), services.GetRequiredService<DocumentNotifications>(),
            services.GetRequiredService<SignatoryResolution>(), services.GetRequiredService<DirectoryAccounts>(),
            services.GetRequiredService<DocumentApproval>(), services.GetRequiredService<DisciplineAssignments>(),
            services.GetRequiredService<LinkedDocuments>(), Options.Create(terms ?? new RemindersSettings()), new TestClock(now, Zone), NullLogger<ReworkReminders>.Instance);
        return await job.RunAsync(CancellationToken.None);
    }

    private async Task<int> MailJobAsync(DateTimeOffset now)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var job = new NotificationMailJob(
            scope.ServiceProvider.GetRequiredService<AppDbContext>(), scope.ServiceProvider.GetRequiredService<IMailSender>(),
            Options.Create(new MailSettings { DigestHour = 8, MaxAgeHours = 24, PublicUrl = "https://rpdfos.test" }),
            new TestClock(now, Zone), NullLogger<NotificationMailJob>.Instance);
        return await job.RunAsync(CancellationToken.None);
    }

    // Напоминания по документу — из базы, по payload.document_id: у уведомления без ссылки document_id пуст.
    private async Task<List<Reminder>> RemindersAsync(Guid documentId)
    {
        var found = new List<Reminder>();
        await app.WithDbAsync(async db =>
        {
            var rows = await db.Notifications.AsNoTracking()
                .Where(n => n.EventType == NotificationEvents.ReworkDeadlineNear || n.EventType == NotificationEvents.ReworkOverdue)
                .ToListAsync();
            foreach (var row in rows)
            {
                var payload = JsonDocument.Parse(row.Payload).RootElement.Clone();
                if (payload.TryGetProperty("document_id", out var of) && of.GetGuid() == documentId)
                    found.Add(new Reminder(row.RecipientId, row.EventType, row.DocumentId, payload.GetProperty("text").GetString()!, payload,
                        row.CreatedAt));
            }
        });
        return found;
    }

    // Предупреждений и эскалаций в списке — для краткой сверки.
    private static (int Warnings, int Escalations) Counts(IEnumerable<Reminder> reminders)
    {
        var list = reminders.ToList();
        return (list.Count(r => r.EventType == NotificationEvents.ReworkDeadlineNear),
            list.Count(r => r.EventType == NotificationEvents.ReworkOverdue));
    }

    private async Task AssertOverdueToAsync(Case c, IEnumerable<Guid> recipients)
    {
        var overdue = (await RemindersAsync(c.Created.Document.Id)).Where(r => r.EventType == NotificationEvents.ReworkOverdue).ToList();
        Assert.Equal(recipients.Distinct().Order(), overdue.Select(r => r.RecipientId).Order());
    }

    // Строка согласования заведующего в документе: он участник и открывает его (как после прежнего круга согласования).
    private Task AddSeatAsync(Guid documentId, Guid signatoryId) => app.WithDbAsync(async db =>
    {
        db.DocumentSignatures.Add(new DocumentSignature
        {
            DocumentId = documentId, Role = SignatoryRoles.DepartmentHead, Resolver = DepartmentHeadResolver.Rule,
            ResolvedUserId = signatoryId, CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    });

    private async Task<Guid?> DepartmentOfAsync(Guid disciplineId)
    {
        Guid? department = null;
        await app.WithDbAsync(async db =>
            department = await db.Disciplines.Where(d => d.Id == disciplineId).Select(d => d.DepartmentId).SingleAsync());
        return department;
    }

    // Кафедру дисциплины меняет сам тест (и возвращает): тесты коллекции идут по очереди, другим он не мешает.
    private Task SetDepartmentAsync(Guid disciplineId, Guid? departmentId) => app.WithDbAsync(async db =>
        await db.Disciplines.Where(d => d.Id == disciplineId).ExecuteUpdateAsync(s => s.SetProperty(d => d.DepartmentId, departmentId)));

    private async Task<Department> AddDepartmentAsync()
    {
        var key = Guid.NewGuid().ToString("N")[..12];
        var department = new Department { Code = $"T{key}", Name = $"Тестовая кафедра {key}", ShortName = $"ТК {key}" };
        await app.WithDbAsync(async db =>
        {
            db.Departments.Add(department);
            await db.SaveChangesAsync();
        });
        return department;
    }

    // Запись справочника без учётной записи — как заведующий из файла должностей.
    private async Task<Employee> AddHeadAsync(Guid departmentId, string role)
    {
        var now = DateTimeOffset.UtcNow;
        var employee = new Employee
        {
            ShortName = "Заведующий З. З.", NameForDocument = "З. З. Заведующий", SignatoryRole = role, DepartmentId = departmentId,
            CanLogin = false, CreatedAt = now, UpdatedAt = now
        };
        await app.WithDbAsync(async db =>
        {
            db.Employees.Add(employee);
            await db.SaveChangesAsync();
        });
        return employee;
    }

    // Действующие методисты — независимая от кода сверка получателей: роль учётной записи, вход разрешён, не отключена.
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

    private static async Task<List<NotificationItem>> FeedAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<NotificationItem>>($"/api/notifications?limit={NotificationsModule.MaxLimit}"))!;

    // Дата в тексте уведомления — по поясу сервера, в формате «дд.мм.гггг».
    private static string Local(DateTimeOffset moment) =>
        TimeZoneInfo.ConvertTime(moment, Zone).ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    private static string Url(DocumentDetails document, string action) => $"/api/documents/{document.Document.Id}/{action}";

    // Ответ на смену статуса: ожидаемый статус; возвращает новую версию документа.
    private static async Task<int> ExpectAsync(Task<HttpResponseMessage> request, string status)
    {
        var response = await request;
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        var changed = (await response.Content.ReadFromJsonAsync<StatusAnswer>())!;
        Assert.Equal(status, changed.Status);
        return changed.Version;
    }

    private sealed record StatusAnswer(int Version, string Status);

    private sealed record Reminder(Guid RecipientId, string EventType, Guid? DocumentId, string Text, JsonElement Payload, DateTimeOffset CreatedAt);

    private sealed record Case(
        HttpClient Author, Employee AuthorRecord, string AuthorEmail, HttpClient Reviewer, Employee ReviewerRecord, string ReviewerEmail,
        DocumentDetails Created, int Version);
}
