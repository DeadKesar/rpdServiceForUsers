using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RpdFos.Core.Accounts;

namespace RpdFos.Api.Pages.Account;

/// <summary>
/// «Новый пароль» (этап 8, A6): сюда ведут ссылки восстановления пароля и приглашения. Идентификатор и токен приходят
/// во фрагменте адреса и передаются формой в теле запроса — в адресе запроса их нет (см. <see cref="PasswordLinks"/>).
/// Проверка ссылки — при отправке формы: до неё сервер ссылки не видит.
/// </summary>
public class SetPasswordModel(PasswordLinks links) : PageModel
{
    [BindProperty]
    public string? UserId { get; set; }

    [BindProperty]
    public string? Token { get; set; }

    [BindProperty, Required(ErrorMessage = "Введите пароль"), DataType(DataType.Password)]
    public string Password { get; set; } = "";

    [BindProperty, DataType(DataType.Password)]
    public string Confirm { get; set; } = "";

    public bool Done { get; private set; }

    public List<string> Errors { get; } = [];

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
            return Page();
        if (Password != Confirm)
        {
            Errors.Add("Пароли не совпадают.");
            return Page();
        }

        var result = await links.SetPasswordAsync(UserId, Token, Password);
        if (result.Succeeded)
        {
            Done = true;
            return Page();
        }

        foreach (var error in result.Errors)
            Errors.Add(error.Code == PasswordLinks.InvalidTokenCode
                ? error.Description + " Запросите новую ссылку («Забыли пароль?») или попросите администратора отправить приглашение."
                : error.Description);
        return Page();
    }
}
