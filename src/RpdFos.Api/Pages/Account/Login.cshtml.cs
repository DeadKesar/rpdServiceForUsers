using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Api.Pages.Account;

/// <summary>
/// Страница входа. Сюда сервер авторизации отправляет пользователя, который ещё не вошёл;
/// после входа он возвращается по ReturnUrl к выдаче кода авторизации.
/// Регистрации нет: учётные записи создаёт Администратор.
/// </summary>
public class LoginModel(SignInManager<AppUser> signIn, UserManager<AppUser> users) : PageModel
{
    [BindProperty, Required(ErrorMessage = "Введите email")]
    public string Email { get; set; } = "";

    [BindProperty, Required(ErrorMessage = "Введите пароль"), DataType(DataType.Password)]
    public string Password { get; set; } = "";

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? Error { get; private set; }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
            return Page();

        // Имя пользователя совпадает с email: так учётные записи создаются при засеве и Администратором.
        var result = await signIn.PasswordSignInAsync(Email, Password, isPersistent: false, lockoutOnFailure: true);
        if (result.Succeeded)
            return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/");

        // Отключённая администратором учётная запись — бессрочная блокировка (этап 5.2): сообщение своё.
        Error = !result.IsLockedOut ? "Неверный email или пароль."
            : await users.FindByEmailAsync(Email) is { } user && Features.Users.UsersModule.IsDisabled(user)
                ? "Учётная запись отключена администратором."
                : "Учётная запись временно заблокирована после нескольких неудачных попыток.";
        return Page();
    }
}
