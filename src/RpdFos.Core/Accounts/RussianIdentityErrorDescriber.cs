using Microsoft.AspNetCore.Identity;

namespace RpdFos.Core.Accounts;

/// <summary>
/// Русские тексты ошибок Identity: их видит пользователь на странице «Новый пароль» и администратор в списке пользователей.
/// Коды ошибок не меняются — по ним работают клиенты и тесты.
/// </summary>
public sealed class RussianIdentityErrorDescriber : IdentityErrorDescriber
{
    public override IdentityError DefaultError() => new() { Code = nameof(DefaultError), Description = "Неизвестная ошибка." };
    public override IdentityError InvalidToken() => new() { Code = nameof(InvalidToken), Description = "Ссылка недействительна: она устарела или уже использована." };
    public override IdentityError PasswordMismatch() => new() { Code = nameof(PasswordMismatch), Description = "Неверный пароль." };
    public override IdentityError PasswordTooShort(int length) =>
        new() { Code = nameof(PasswordTooShort), Description = $"Пароль не короче {length} символов." };
    public override IdentityError PasswordRequiresNonAlphanumeric() =>
        new() { Code = nameof(PasswordRequiresNonAlphanumeric), Description = "В пароле нужен хотя бы один знак, не буква и не цифра." };
    public override IdentityError PasswordRequiresDigit() =>
        new() { Code = nameof(PasswordRequiresDigit), Description = "В пароле нужна хотя бы одна цифра." };
    public override IdentityError PasswordRequiresLower() =>
        new() { Code = nameof(PasswordRequiresLower), Description = "В пароле нужна хотя бы одна строчная буква." };
    public override IdentityError PasswordRequiresUpper() =>
        new() { Code = nameof(PasswordRequiresUpper), Description = "В пароле нужна хотя бы одна заглавная буква." };
    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) =>
        new() { Code = nameof(PasswordRequiresUniqueChars), Description = $"В пароле нужно не меньше {uniqueChars} разных символов." };
    public override IdentityError DuplicateEmail(string email) =>
        new() { Code = nameof(DuplicateEmail), Description = $"Email {email} уже занят." };
    public override IdentityError DuplicateUserName(string userName) =>
        new() { Code = nameof(DuplicateUserName), Description = $"Имя пользователя {userName} уже занято." };
    public override IdentityError InvalidEmail(string? email) =>
        new() { Code = nameof(InvalidEmail), Description = $"Email {email} некорректен." };
    public override IdentityError InvalidUserName(string? userName) =>
        new() { Code = nameof(InvalidUserName), Description = $"Имя пользователя {userName} некорректно." };
}
