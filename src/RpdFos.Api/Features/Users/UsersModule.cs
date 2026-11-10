using Carter;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RpdFos.Api.Features.Directory;
using RpdFos.Api.Infrastructure;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Directory;

namespace RpdFos.Api.Features.Users;

/// <summary>
/// Срез «Пользователи»: учётные записи создаёт Администратор, самостоятельной регистрации нет.
/// У каждой учётной записи — запись в справочнике сотрудников (новая или существующая, из файла должностей).
/// Ролей у учётной записи может быть несколько (этап 6, B1): заведующий кафедрой — и преподаватель, и согласующий.
/// </summary>
public class UsersModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/users").WithTags("Users").RequireAuthorization(Policies.Admin);

        // Учётные записи с ролью и связанной записью справочника сотрудников.
        g.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
        {
            var accounts = await db.Users.OrderBy(u => u.Email)
                .Select(u => new { u.Id, u.Email, u.FullName, u.LockoutEnd }).ToListAsync(ct);
            var roles = (await db.UserRoles.Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, r.Name })
                    .ToListAsync(ct))
                .ToLookup(x => x.UserId, x => x.Name);
            // Запись справочника связана с учётной записью через identity_subject = id учётной записи.
            var subjects = accounts.Select(a => a.Id.ToString()).ToList();
            var linked = db.Employees.Where(e => e.IdentitySubject != null && subjects.Contains(e.IdentitySubject));
            var subjectOf = await linked.ToDictionaryAsync(e => e.Id, e => e.IdentitySubject!, ct);
            var recordBySubject = (await DirectoryRecordItem.Project(linked, db).ToListAsync(ct))
                .ToDictionary(r => subjectOf[r.Id]);

            return accounts.Select(a => new AccountItem(a.Id, a.Email ?? "", a.FullName, Ordered(roles[a.Id]),
                recordBySubject.GetValueOrDefault(a.Id.ToString()), a.LockoutEnd != DateTimeOffset.MaxValue)).ToList();
        });

        g.MapPost("/{id:guid}/password", SetPassword);
        g.MapPut("/{id:guid}/roles", SetRoles);
        g.MapPost("/{id:guid}/deactivate", (Guid id, HttpContext http, UserManager<AppUser> users) => SetEnabled(id, false, http, users));
        g.MapPost("/{id:guid}/activate", (Guid id, HttpContext http, UserManager<AppUser> users) => SetEnabled(id, true, http, users));

        g.MapPost("/", async (CreateUserRequest rq, UserManager<AppUser> users, DirectoryAccounts directory,
            AppDbContext db, CancellationToken ct) =>
        {
            if (RolesProblem(rq.Roles) is { } wrong) return wrong;
            var newRoles = Ordered(rq.Roles!);

            // Учётная запись, роль и запись справочника — вместе или ничего: без commit транзакция откатится,
            // и учётная запись без роли или без записи справочника не останется.
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var user = new AppUser { UserName = rq.Email, Email = rq.Email, EmailConfirmed = true, FullName = rq.FullName };
            var created = await users.CreateAsync(user, rq.Password);
            if (!created.Succeeded)
                return ValidationProblem(created);

            var assigned = await users.AddToRolesAsync(user, newRoles);
            if (!assigned.Succeeded)
                return ValidationProblem(assigned);

            var (employee, problem) = await directory.LinkAsync(user, rq.DirectoryUserId, ct);
            if (problem is not null)
                return problem switch
                {
                    DirectoryLinkProblem.NotFound => Results.Problem(statusCode: StatusCodes.Status404NotFound,
                        title: "Запись справочника не найдена", detail: $"Сотрудника с id {rq.DirectoryUserId} нет."),
                    DirectoryLinkProblem.Inactive => Results.Problem(statusCode: StatusCodes.Status409Conflict,
                        title: "Запись справочника неактивна", detail: "На должности уже другой сотрудник."),
                    DirectoryLinkProblem.AlreadyLinked => Results.Problem(statusCode: StatusCodes.Status409Conflict,
                        title: "Запись справочника уже связана", detail: "У сотрудника уже есть учётная запись."),
                    _ => Results.Problem(statusCode: StatusCodes.Status409Conflict,
                        title: "Email уже в справочнике", detail: $"Email {rq.Email} уже у другой записи справочника сотрудников.")
                };

            await tx.CommitAsync(ct);
            return Results.Created($"/api/admin/users/{user.Id}",
                new UserItem(user.Id, rq.Email, rq.FullName, newRoles, employee!.Id));
        });
    }

    // Роли — в порядке списка ролей системы, без повторов: порядок выбора флажков значения не имеет.
    private static string[] Ordered(IEnumerable<string?> roles) => AppRoles.All.Where(roles.Contains).ToArray();

    private static IResult? RolesProblem(IReadOnlyCollection<string>? roles) =>
        roles is null || roles.Count == 0
            ? Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Не выбрана роль",
                detail: "У учётной записи должна быть хотя бы одна роль.")
            : roles.FirstOrDefault(r => !AppRoles.All.Contains(r)) is { } unknown
                ? Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Неизвестная роль",
                    detail: $"Роль «{unknown}» не существует.")
                : null;

    /// <summary>
    /// Роли учётной записи меняет администратор (FR-ADM-01): новый набор заменяет прежний. Снять с себя роль
    /// администратора нельзя. У вошедшего пользователя роли обновятся при следующем входе: они записаны в токене.
    /// </summary>
    private static async Task<IResult> SetRoles(Guid id, RolesRequest rq, HttpContext http, UserManager<AppUser> users, AppDbContext db,
        CancellationToken ct)
    {
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null) return AccountNotFound();
        if (RolesProblem(rq.Roles) is { } wrong) return wrong;
        var wanted = Ordered(rq.Roles!);
        if (!wanted.Contains(AppRoles.Admin) && http.User.FindFirst(DirectoryAccounts.SubjectClaim)?.Value == user.Id.ToString())
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Нельзя снять с себя роль администратора",
                detail: "Администратор потерял бы доступ к странице пользователей.");

        // Снять и добавить — вместе или ничего: учётная запись не останется без ролей.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var current = await users.GetRolesAsync(user);
        var removed = await users.RemoveFromRolesAsync(user, current.Except(wanted));
        if (!removed.Succeeded) return ValidationProblem(removed);
        var added = await users.AddToRolesAsync(user, wanted.Except(current));
        if (!added.Succeeded) return ValidationProblem(added);
        await tx.CommitAsync(ct);
        return Results.Ok(wanted);
    }

    // Учётная запись отключена администратором — бессрочная блокировка Identity (этап 5.2, FR-ADM-01).
    public static bool IsDisabled(AppUser user) => user.LockoutEnd == DateTimeOffset.MaxValue;

    /// <summary>
    /// Новый временный пароль (этап 5.2): потерянную учётную запись иначе не вернуть. Требования к паролю — как при
    /// создании; счётчик неудачных попыток сбрасывается.
    /// </summary>
    private static async Task<IResult> SetPassword(Guid id, PasswordRequest rq, UserManager<AppUser> users)
    {
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null) return AccountNotFound();
        var token = await users.GeneratePasswordResetTokenAsync(user);
        var reset = await users.ResetPasswordAsync(user, token, rq.Password ?? "");
        if (!reset.Succeeded) return ValidationProblem(reset);
        await users.ResetAccessFailedCountAsync(user);
        // Временная блокировка за неудачные попытки снимается; отключённая администратором — остаётся.
        if (!IsDisabled(user)) await users.SetLockoutEndDateAsync(user, null);
        return Results.NoContent();
    }

    /// <summary>
    /// Отключить или вернуть учётную запись: отключённая не может войти (бессрочная блокировка), действующие сеансы
    /// прерываются сменой штампа безопасности. Запись справочника сотрудников не меняется. Себя отключить нельзя.
    /// </summary>
    private static async Task<IResult> SetEnabled(Guid id, bool enabled, HttpContext http, UserManager<AppUser> users)
    {
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null) return AccountNotFound();
        if (!enabled && http.User.FindFirst(DirectoryAccounts.SubjectClaim)?.Value == user.Id.ToString())
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Нельзя отключить себя",
                detail: "Отключить свою учётную запись нельзя: администратор потерял бы доступ.");
        await users.SetLockoutEnabledAsync(user, true);
        await users.SetLockoutEndDateAsync(user, enabled ? null : DateTimeOffset.MaxValue);
        if (enabled) await users.ResetAccessFailedCountAsync(user);
        else await users.UpdateSecurityStampAsync(user);
        return Results.NoContent();
    }

    private static IResult AccountNotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Учётная запись не найдена");

    private static IResult ValidationProblem(IdentityResult result) =>
        Results.ValidationProblem(result.Errors
            .GroupBy(e => e.Code)
            .ToDictionary(x => x.Key, x => x.Select(e => e.Description).ToArray()));
}

/// <summary>
/// <paramref name="DirectoryUserId"/> — необязательный: привязать учётную запись к существующей записи
/// справочника сотрудников (например, загруженной из файла должностей) вместо создания новой.
/// </summary>
public record CreateUserRequest(string Email, string FullName, string Password, string[]? Roles, Guid? DirectoryUserId = null);

public record UserItem(Guid Id, string Email, string FullName, IReadOnlyList<string> Roles, Guid DirectoryUserId);

/// <summary>Учётная запись: роли и связанная запись справочника сотрудников (null — не связана).</summary>
/// <param name="IsActive">Учётная запись не отключена администратором (этап 5.2).</param>
public record AccountItem(
    Guid Id, string Email, string FullName, IReadOnlyList<string> Roles, DirectoryRecordItem? Directory, bool IsActive = true);

/// <summary>Новый набор ролей учётной записи.</summary>
public record RolesRequest(string[]? Roles);

/// <summary>Новый временный пароль учётной записи.</summary>
public record PasswordRequest(string? Password);
