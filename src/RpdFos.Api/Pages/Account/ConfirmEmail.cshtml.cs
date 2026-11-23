using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RpdFos.Core.Accounts;

namespace RpdFos.Api.Pages.Account;

/// <summary>
/// «Смена email» (этап 8, A8): сюда ведут ссылки из двух писем о смене адреса. Токен приходит во фрагменте адреса и
/// передаётся формой в теле запроса (как на странице «Новый пароль»); подтверждает нажатие кнопки, а не открытие ссылки.
/// Вход не нужен: ссылка из письма и есть доказательство доступа к ящику.
/// </summary>
public class ConfirmEmailModel(EmailChanges emailChanges) : PageModel
{
    [BindProperty]
    public string? Token { get; set; }

    public EmailConfirmResult? Result { get; private set; }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        Result = await emailChanges.ConfirmAsync(Token, ct);
        return Page();
    }
}
