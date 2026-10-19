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
                .Select(u => new { u.Id, u.Email, u.FullName }).ToListAsync(ct);
            var roles = (await db.UserRoles.Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, r.Name })
                    .ToListAsync(ct))
                .ToLookup(x => x.UserId, x => x.Name);
            // Запись справочника связана с учётной записью через identity_subject = id учётной записи.
            var subjects = accounts.Select(a => a.Id.ToString()).ToList();
            var linked = db.Employees.Where(e => e.IdentitySubject != null && subjects.Contains(e.IdentitySubject));
            var subjectOf = await linked.ToDictionaryAsync(e => e.Id, e => e.IdentitySubject!, ct);
            var recordBySubject = (await DirectoryRecordItem.Project(linked, db).ToListAsync(ct))
                .ToDictionary(r => subjectOf[r.Id]);

            // В прототипе у пользователя одна роль.
            return accounts.Select(a => new AccountItem(a.Id, a.Email ?? "", a.FullName, roles[a.Id].FirstOrDefault(),
                recordBySubject.GetValueOrDefault(a.Id.ToString()))).ToList();
        });

        g.MapPost("/", async (CreateUserRequest rq, UserManager<AppUser> users, DirectoryAccounts directory,
            AppDbContext db, CancellationToken ct) =>
        {
            if (!AppRoles.All.Contains(rq.Role))
                return Results.Problem(
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "Неизвестная роль",
                    detail: $"Роль «{rq.Role}» не существует.");

            // Учётная запись, роль и запись справочника — вместе или ничего: без commit транзакция откатится,
            // и учётная запись без роли или без записи справочника не останется.
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var user = new AppUser { UserName = rq.Email, Email = rq.Email, EmailConfirmed = true, FullName = rq.FullName };
            var created = await users.CreateAsync(user, rq.Password);
            if (!created.Succeeded)
                return ValidationProblem(created);

            var assigned = await users.AddToRoleAsync(user, rq.Role);
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
                new UserItem(user.Id, rq.Email, rq.FullName, rq.Role, employee!.Id));
        });
    }

    private static IResult ValidationProblem(IdentityResult result) =>
        Results.ValidationProblem(result.Errors
            .GroupBy(e => e.Code)
            .ToDictionary(x => x.Key, x => x.Select(e => e.Description).ToArray()));
}

/// <summary>
/// <paramref name="DirectoryUserId"/> — необязательный: привязать учётную запись к существующей записи
/// справочника сотрудников (например, загруженной из файла должностей) вместо создания новой.
/// </summary>
public record CreateUserRequest(string Email, string FullName, string Password, string Role, Guid? DirectoryUserId = null);

public record UserItem(Guid Id, string Email, string FullName, string Role, Guid DirectoryUserId);

/// <summary>Учётная запись: роль и связанная запись справочника сотрудников (null — не связана).</summary>
public record AccountItem(Guid Id, string Email, string FullName, string? Role, DirectoryRecordItem? Directory);
