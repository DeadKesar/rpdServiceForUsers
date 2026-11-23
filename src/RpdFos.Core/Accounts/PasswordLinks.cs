using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Mail;

namespace RpdFos.Core.Accounts;

/// <summary>Чем кончилась отправка служебного письма.</summary>
public enum LetterResult
{
    Sent,

    /// <summary>Учётная запись отключена администратором: ей писем о пароле нет.</summary>
    AccountDisabled,

    /// <summary>Не задан <see cref="MailSettings.PublicUrl"/>: ссылку собрать не из чего.</summary>
    NoPublicUrl,

    /// <summary>Почтовый сервер недоступен или оборвал соединение.</summary>
    ServerUnavailable,

    /// <summary>Сервер отказал именно в этом письме — например, не принял адрес.</summary>
    Rejected
}

/// <summary>
/// Ссылки «Задайте пароль» (этап 8, A6): восстановление пароля (FR-AUTH-03) и приглашение (FR-ADM-01). Обе ведут на одну
/// страницу «Новый пароль»; различаются провайдером токена и сроком.
/// <para>
/// Токен кладётся во <b>фрагмент</b> адреса (<c>#u=…&amp;t=…</c>): браузер его серверу не отправляет, поэтому он не попадает
/// ни в журнал хоста, ни в журнал обратного прокси (nginx пишет строку запроса целиком), ни в заголовок Referer. Страница
/// читает его скриптом и передаёт в теле POST. Одноразовость даёт штамп безопасности: смена пароля обновляет его, и все
/// выданные ссылки перестают проходить проверку.
/// </para>
/// Ни ссылка, ни токен в журнал не пишутся.
/// </summary>
public sealed class PasswordLinks(
    UserManager<AppUser> users, IMailSender mail, IOptions<MailSettings> mailSettings, IOptions<AccountSettings> accountSettings,
    TimeProvider clock, ILogger<PasswordLinks> log)
{
    /// <summary>Имя провайдера токена приглашения (регистрируется хостом) и назначение токена.</summary>
    public const string InvitationProvider = "Invitation";
    private const string InvitationPurpose = "Invitation";

    public const string InvalidTokenCode = "InvalidToken";
    public const string DisabledCode = "AccountDisabled";

    /// <summary>
    /// «Забыли пароль?». Результат вызывающему не нужен: ответ пользователю один и тот же на любой адрес, иначе страница
    /// выдавала бы, зарегистрирован ли адрес. Поэтому и сбой почты здесь не поднимается — он только в журнале хоста.
    /// </summary>
    public async Task RequestResetAsync(string email, CancellationToken ct = default)
    {
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is null || user.IsDisabled) return;
        var result = await SendAsync(user, invitation: false, ct);
        if (result != LetterResult.Sent)
            log.LogWarning("Письмо восстановления пароля для учётной записи {UserId} не отправлено: {Result}", user.Id, result);
    }

    /// <summary>«Отправить приглашение» на странице пользователей. Письмо можно отправить повторно: прежнее остаётся действующим до смены пароля.</summary>
    public Task<LetterResult> InviteAsync(AppUser user, CancellationToken ct = default) =>
        user.IsDisabled ? Task.FromResult(LetterResult.AccountDisabled) : SendAsync(user, invitation: true, ct);

    private async Task<LetterResult> SendAsync(AppUser user, bool invitation, CancellationToken ct)
    {
        var publicUrl = mailSettings.Value.PublicUrl.TrimEnd('/');
        if (publicUrl.Length == 0) return LetterResult.NoPublicUrl;

        var token = invitation
            ? await users.GenerateUserTokenAsync(user, InvitationProvider, InvitationPurpose)
            : await users.GeneratePasswordResetTokenAsync(user);
        var link = $"{publicUrl}/Account/SetPassword#u={user.Id:N}&t={Uri.EscapeDataString(token)}";

        var hours = invitation ? accountSettings.Value.InviteLinkHours : accountSettings.Value.ResetLinkHours;
        var validUntil = clock.GetUtcNow().AddHours(hours);
        var to = user.Email!;
        var letter = invitation
            ? AccountLetters.Invitation(to, user.FullName, link, validUntil, clock.LocalTimeZone)
            : AccountLetters.Reset(to, user.FullName, link, validUntil, clock.LocalTimeZone);
        try
        {
            await mail.SendAsync(letter, ct);
            return LetterResult.Sent;
        }
        catch (MailServerUnavailableException e)
        {
            log.LogWarning(e, "Почтовый сервер недоступен: письмо о пароле для {UserId} не отправлено", user.Id);
            return LetterResult.ServerUnavailable;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // В сообщении исключения может быть адрес получателя, но не тело письма со ссылкой.
            log.LogWarning(e, "Почтовый сервер не принял письмо о пароле для {UserId}", user.Id);
            return LetterResult.Rejected;
        }
    }

    /// <summary>
    /// Проверяет ссылку и задаёт пароль. Ссылка не гасится при отказе по самому паролю (слишком простой): человек исправит
    /// пароль и отправит форму снова. Успех снимает блокировку после неудачных попыток входа; отключённую администратором
    /// запись не возвращает.
    /// </summary>
    public async Task<IdentityResult> SetPasswordAsync(string? userId, string? token, string? password)
    {
        var invalid = Failed(InvalidTokenCode, "Ссылка недействительна: она устарела или уже использована.");
        if (!Guid.TryParseExact(userId, "N", out var id) || string.IsNullOrEmpty(token)
            || await users.FindByIdAsync(id.ToString()) is not { } user)
            return invalid;

        string? resetToken = null;
        if (await users.VerifyUserTokenAsync(user, TokenOptions.DefaultProvider, UserManager<AppUser>.ResetPasswordTokenPurpose, token))
            resetToken = token;
        else if (await users.VerifyUserTokenAsync(user, InvitationProvider, InvitationPurpose, token))
            // Приглашение проверено своим провайдером; пароль задаётся обычным путём — по свежему токену сброса.
            resetToken = await users.GeneratePasswordResetTokenAsync(user);
        if (resetToken is null) return invalid;
        if (user.IsDisabled)
            return Failed(DisabledCode, "Учётная запись отключена администратором.");

        var reset = await users.ResetPasswordAsync(user, resetToken, password ?? "");
        if (!reset.Succeeded) return reset;

        await users.ResetAccessFailedCountAsync(user);
        await users.SetLockoutEndDateAsync(user, null);
        // Человек сам задал пароль по ссылке — временный пароль администратора больше не действует (этап 8, A7).
        if (user.MustChangePassword)
        {
            user.MustChangePassword = false;
            await users.UpdateAsync(user);
        }
        return IdentityResult.Success;
    }

    private static IdentityResult Failed(string code, string description) =>
        IdentityResult.Failed(new IdentityError { Code = code, Description = description });
}
