using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RpdFos.Core.Accounts;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Api.Pages.Account;

/// <summary>
/// «Смена пароля при входе» (этап 8, A7): сюда ведут страница входа, когда администратор задал человеку временный
/// пароль, и точка авторизации OIDC, когда сеанс открыт раньше. Страница не опирается на вход: сеанса при временном
/// пароле нет, поэтому текущий (временный) пароль вводится здесь снова и проверяется со счётом неудачных попыток. После
/// смены человек входит сразу и возвращается по <see cref="ReturnUrl"/>.
/// </summary>
public class ChangePasswordModel(UserManager<AppUser> users, SignInManager<AppUser> signIn, AccountPasswords passwords) : PageModel
{
    [BindProperty, Required(ErrorMessage = "Введите email")]
    public string Email { get; set; } = "";

    [BindProperty, Required(ErrorMessage = "Введите временный пароль"), DataType(DataType.Password)]
    public string CurrentPassword { get; set; } = "";

    [BindProperty, Required(ErrorMessage = "Введите новый пароль"), DataType(DataType.Password)]
    public string NewPassword { get; set; } = "";

    [BindProperty, DataType(DataType.Password)]
    public string Confirm { get; set; } = "";

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public List<string> Errors { get; } = [];

    // Адрес приходит со страницы входа или пуст (переход из точки авторизации): вводить его заново не нужно, если известен.
    public void OnGet(string? email) => Email = email ?? "";

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
            return Page();
        if (NewPassword != Confirm)
        {
            Errors.Add("Пароли не совпадают.");
            return Page();
        }

        // Неизвестный адрес и неверный пароль отвечают одинаково: страница не выдаёт, есть ли такая запись.
        const string wrong = "Неверный email или временный пароль.";
        if (await users.FindByEmailAsync(Email) is not { } user)
        {
            Errors.Add(wrong);
            return Page();
        }

        var result = await passwords.ChangeAsync(user, CurrentPassword, NewPassword);
        if (!result.Succeeded)
        {
            if (result.Outcome == PasswordChange.WrongCurrent) Errors.Add(wrong);
            else Errors.AddRange(result.Errors);
            return Page();
        }

        // Пароль сменён и признак снят: входим сразу, чтобы человек не вводил новый пароль второй раз.
        await signIn.SignInAsync(user, isPersistent: false);
        return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/");
    }
}
