using Carter;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Accounts;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Directory;
using RpdFos.Core.Notifications;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace RpdFos.Api.Features.Profile;

/// <summary>
/// Срез «Профиль и настройки» (S-02, FR-REV-08; этап 7, A5): свои данные только для чтения — ФИО, должность,
/// подразделение, email, роли — и настройки уведомлений по событиям: показывать ли в интерфейсе и как слать на почту
/// (сразу, раз в день, выключено). Доступен любой роли и только о себе: чужого профиля и чужих настроек в API нет.
/// <para>
/// У пользователя без записи справочника сотрудников настроек нет: уведомления создаются только записям справочника,
/// и настраивать нечего — 403, как у остальных срезов. Профиль у него есть — имя и роли берутся из токена.
/// </para>
/// </summary>
public class ProfileModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/profile").WithTags("Profile").RequireAuthorization();
        g.MapGet("/", Get);
        g.MapGet("/notification-settings", GetSettings);
        g.MapPut("/notification-settings", SaveSettings);
        g.MapPost("/password", ChangePassword);
        g.MapPost("/email-change", ChangeEmail);
    }

    /// <summary>
    /// Профиль вошедшего. ФИО, должность и подразделение — из записи справочника, email и роли — из учётной записи: письма
    /// уходят на её адрес, а роли в базе актуальнее ролей в токене (их меняет администратор, токен обновляется при входе).
    /// Нет записи справочника — только имя и роли из токена. Нет и учётной записи в базе (так бывает лишь в тестах) —
    /// роли из токена.
    /// </summary>
    private static async Task<IResult> Get(HttpContext http, AppDbContext db, EmailChanges emailChanges, CancellationToken ct)
    {
        var subject = http.User.FindFirst(DirectoryAccounts.SubjectClaim)?.Value;
        var record = string.IsNullOrEmpty(subject)
            ? null
            : await db.Employees.AsNoTracking().Where(e => e.IdentitySubject == subject && e.IsActive)
                .Select(e => new
                {
                    e.FullName, e.ShortName, e.Email, e.Position,
                    // Подразделение — кафедра, а у подписантов уровня института — институт.
                    Unit = db.Departments.Where(d => d.Id == e.DepartmentId).Select(d => d.Name).FirstOrDefault()
                           ?? db.Institutes.Where(i => i.Id == e.InstituteId).Select(i => i.Name).FirstOrDefault()
                })
                .FirstOrDefaultAsync(ct);

        var accountId = Guid.TryParse(subject, out var id) ? id : (Guid?)null;
        var account = accountId is null
            ? null
            : await db.Users.AsNoTracking().Where(u => u.Id == accountId).Select(u => new { u.FullName, u.Email }).FirstOrDefaultAsync(ct);
        var roles = account is null
            ? http.User.FindAll(Claims.Role).Select(c => c.Value).ToList()
            : await (from ur in db.UserRoles
                     join r in db.Roles on ur.RoleId equals r.Id
                     where ur.UserId == accountId
                     select r.Name!).ToListAsync(ct);

        var fullName = Filled(record?.FullName) ?? Filled(account?.FullName) ?? Filled(record?.ShortName)
            ?? Filled(http.User.Identity?.Name) ?? "";
        return Results.Ok(new ProfileItem(
            fullName, Filled(account?.Email) ?? Filled(record?.Email), Filled(record?.Position), Filled(record?.Unit),
            // Роли — в порядке списка ролей системы; чужих названий в токене быть не должно, но показывать их незачем.
            AppRoles.All.Where(roles.Contains).Select(r => new ProfileRole(r, AppRoles.Title(r))).ToList(),
            record is not null,
            accountId is Guid me && await emailChanges.PendingAsync(me, ct) is { } pending
                ? new EmailChangeItem(pending.NewEmail, pending.OldConfirmedAt is not null, pending.NewConfirmedAt is not null, pending.ExpiresAt)
                : null));
    }

    /// <summary>Таблица настроек: все события перечня с действующей настройкой — сохранённой или умолчанием.</summary>
    private static async Task<IResult> GetSettings(
        HttpContext http, DirectoryAccounts directory, NotificationSettings settings, CancellationToken ct) =>
        await directory.FindIdAsync(http.User, ct) is Guid me ? Results.Ok(Table(await settings.GetAsync(me, ct))) : NoDirectoryRecord();

    /// <summary>
    /// Сохраняет настройки названных событий, остальные не меняются. Неверное событие или режим почты — 422, и тогда не
    /// сохраняется ничего. В ответе — вся таблица после сохранения. Два сохранения из разных вкладок не сверяются: побеждает
    /// последнее — настройки личные, и версии у них нет.
    /// </summary>
    private static async Task<IResult> SaveSettings(
        List<NotificationSettingRequest>? request, HttpContext http, DirectoryAccounts directory, NotificationSettings settings,
        CancellationToken ct)
    {
        if (await directory.FindIdAsync(http.User, ct) is not Guid me) return NoDirectoryRecord();

        var changes = new Dictionary<string, EventSetting>();
        foreach (var item in request ?? [])
        {
            if (item.EventType is null || NotificationEvents.Find(item.EventType) is null)
                return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Неизвестное событие",
                    detail: $"События «{item.EventType}» нет в списке событий уведомлений.");
            if (!MailModeCodes.TryParse(item.Mail, out var mail))
                return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Неизвестный режим почты",
                    detail: $"Режим «{item.Mail}» не существует: допустимы «{MailModeCodes.Immediate}», «{MailModeCodes.Daily}» и «{MailModeCodes.Off}».");
            changes[item.EventType] = new EventSetting(item.InApp, mail);
        }

        await settings.SaveAsync(me, changes, ct);
        return Results.Ok(Table(await settings.GetAsync(me, ct)));
    }

    /// <summary>
    /// «Сменить пароль» (этап 8, A7): текущий пароль и новый. Неверный текущий пароль, совпадение нового с текущим и слабый
    /// новый — 422; запись заблокирована или отключена — 409. Повтор нового пароля проверяет форма: серверу он не нужен.
    /// Смена обновляет штамп безопасности: открытые сеансы входа в других браузерах прекратятся.
    /// </summary>
    private static async Task<IResult> ChangePassword(
        PasswordChangeRequest? request, HttpContext http, UserManager<AppUser> users, AccountPasswords passwords)
    {
        var subject = http.User.FindFirst(DirectoryAccounts.SubjectClaim)?.Value;
        if (await users.FindByIdAsync(subject ?? "") is not { } user)
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Учётная запись не найдена");

        var result = await passwords.ChangeAsync(user, request?.CurrentPassword, request?.NewPassword);
        if (result.Succeeded) return Results.NoContent();
        return Results.Problem(
            statusCode: result.Outcome is PasswordChange.LockedOut or PasswordChange.Disabled
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status422UnprocessableEntity,
            title: "Пароль не изменён", detail: string.Join(" ", result.Errors));
    }

    /// <summary>
    /// «Изменить email» (этап 8, A8; решение 7): письма на прежний и на новый адрес, у каждого своя ссылка; адрес меняется,
    /// когда подтверждены обе. 204 — письма отправлены (прежний запрос заменён); 422 — адрес не разбирается, совпал с
    /// текущим или сервер не принял письмо; 409 — адрес занят, запись отключена или не задан адрес системы для ссылок;
    /// 503 — почтовый сервер недоступен; 404 — нет учётной записи.
    /// </summary>
    private static async Task<IResult> ChangeEmail(
        EmailChangeRequestBody? request, HttpContext http, UserManager<AppUser> users, EmailChanges emailChanges, CancellationToken ct)
    {
        var subject = http.User.FindFirst(DirectoryAccounts.SubjectClaim)?.Value;
        if (await users.FindByIdAsync(subject ?? "") is not { } user)
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Учётная запись не найдена");

        var email = request?.NewEmail?.Trim();
        return await emailChanges.RequestAsync(user, email, ct) switch
        {
            EmailChangeStart.Requested => Results.NoContent(),
            EmailChangeStart.InvalidEmail => Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Email не изменён", detail: "Укажите адрес электронной почты в виде name@example.ru."),
            EmailChangeStart.SameAsCurrent => Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Email не изменён", detail: "Новый адрес совпадает с текущим."),
            EmailChangeStart.Taken => Results.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "Адрес уже занят", detail: $"Адрес {email} уже используется другой учётной записью или записью справочника сотрудников."),
            EmailChangeStart.AccountDisabled => Results.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "Учётная запись отключена", detail: "Администратор отключил учётную запись: адрес изменить нельзя."),
            EmailChangeStart.NoPublicUrl => Results.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "Письма отправить нельзя", detail: "В настройках системы не задан адрес для ссылок в письмах (Mail:PublicUrl). Обратитесь к администратору."),
            EmailChangeStart.ServerUnavailable => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Почтовый сервер недоступен", detail: "Письма не отправлены, адрес не изменён. Повторите позже."),
            _ => Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Почтовый сервер не принял письмо", detail: $"Проверьте адреса {user.Email} и {email}: сервер отказал в доставке.")
        };
    }

    private static List<NotificationSettingItem> Table(IReadOnlyList<(NotificationEventInfo Event, EventSetting Setting)> rows) =>
        rows.Select(r => new NotificationSettingItem(r.Event.Type, r.Event.Title, r.Setting.InApp, MailModeCodes.Code(r.Setting.Mail)))
            .ToList();

    private static string? Filled(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    private static IResult NoDirectoryRecord() =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Нет записи в справочнике сотрудников",
            detail: "Учётная запись не связана со справочником сотрудников: уведомления ей не создаются, настраивать нечего — обратитесь к администратору.");
}

/// <summary>
/// Профиль: ФИО, email учётной записи, должность и подразделение (из справочника), роли и признак «есть запись
/// справочника» — без неё настроек уведомлений нет.
/// </summary>
public record ProfileItem(
    string FullName, string? Email, string? Position, string? Unit, IReadOnlyList<ProfileRole> Roles, bool HasDirectoryRecord,
    EmailChangeItem? EmailChange = null);

/// <summary>Незавершённая смена email: новый адрес и какие из двух ссылок уже подтверждены.</summary>
public record EmailChangeItem(string NewEmail, bool OldConfirmed, bool NewConfirmed, DateTimeOffset ExpiresAt);

/// <summary>Запрос смены email: новый адрес.</summary>
public record EmailChangeRequestBody(string? NewEmail);

public record ProfileRole(string Code, string Title);

/// <summary>
/// Строка таблицы настроек: событие (тип и название), показывать ли в интерфейсе и режим почты —
/// <c>immediate</c> (сразу), <c>daily</c> (раз в день) или <c>off</c> (выключено).
/// </summary>
public record NotificationSettingItem(string EventType, string Title, bool InApp, string Mail);

/// <summary>Новая настройка события; <c>Mail</c> — как в <see cref="NotificationSettingItem"/>.</summary>
public record NotificationSettingRequest(string? EventType, bool InApp, string? Mail);

/// <summary>Смена своего пароля: текущий и новый.</summary>
public record PasswordChangeRequest(string? CurrentPassword, string? NewPassword);
