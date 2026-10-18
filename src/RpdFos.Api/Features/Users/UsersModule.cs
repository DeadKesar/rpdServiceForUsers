using Carter;
using Microsoft.AspNetCore.Identity;
using RpdFos.Api.Infrastructure;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Api.Features.Users;

/// <summary>Срез «Пользователи»: учётные записи создаёт Администратор, самостоятельной регистрации нет.</summary>
public class UsersModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/users").WithTags("Users").RequireAuthorization(Policies.Admin);

        g.MapPost("/", async (CreateUserRequest rq, UserManager<AppUser> users) =>
        {
            if (!AppRoles.All.Contains(rq.Role))
                return Results.Problem(
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "Неизвестная роль",
                    detail: $"Роль «{rq.Role}» не существует.");

            var user = new AppUser { UserName = rq.Email, Email = rq.Email, EmailConfirmed = true, FullName = rq.FullName };
            var created = await users.CreateAsync(user, rq.Password);
            if (!created.Succeeded)
                return ValidationProblem(created);

            // Учётная запись без роли остаться не должна: роль не назначилась — запись удаляем.
            var assigned = await users.AddToRoleAsync(user, rq.Role);
            if (!assigned.Succeeded)
            {
                await users.DeleteAsync(user);
                return ValidationProblem(assigned);
            }

            return Results.Created($"/api/admin/users/{user.Id}",
                new UserItem(user.Id, rq.Email, rq.FullName, rq.Role));
        });
    }

    private static IResult ValidationProblem(IdentityResult result) =>
        Results.ValidationProblem(result.Errors
            .GroupBy(e => e.Code)
            .ToDictionary(x => x.Key, x => x.Select(e => e.Description).ToArray()));
}

public record CreateUserRequest(string Email, string FullName, string Password, string Role);

public record UserItem(Guid Id, string Email, string FullName, string Role);
