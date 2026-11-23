using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RpdFos.Core.Accounts;

namespace RpdFos.Api.Pages.Account;

/// <summary>
/// «Забыли пароль?» (FR-AUTH-03, этап 8, A6). Ответ один и тот же на любой адрес — известный, неизвестный или отключённый:
/// иначе страница выдавала бы, у кого в системе есть учётная запись.
/// </summary>
public class ForgotPasswordModel(PasswordLinks links) : PageModel
{
    [BindProperty, Required(ErrorMessage = "Введите email")]
    public string Email { get; set; } = "";

    public bool Sent { get; private set; }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return Page();

        await links.RequestResetAsync(Email, ct);
        Sent = true;
        return Page();
    }
}
