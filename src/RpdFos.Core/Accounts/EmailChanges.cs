using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Mail;

namespace RpdFos.Core.Accounts;

/// <summary>Чем кончился запрос на смену email.</summary>
public enum EmailChangeStart
{
    /// <summary>Оба письма отправлены, прежний запрос (если был) заменён.</summary>
    Requested,

    /// <summary>Адрес не разбирается как email или длиннее 255 знаков.</summary>
    InvalidEmail,

    /// <summary>Новый адрес совпал с текущим.</summary>
    SameAsCurrent,

    /// <summary>Адрес уже у другой учётной записи или у записи справочника сотрудников.</summary>
    Taken,

    /// <summary>Учётная запись отключена администратором.</summary>
    AccountDisabled,

    /// <summary>Не задан <see cref="MailSettings.PublicUrl"/>: ссылки собрать не из чего.</summary>
    NoPublicUrl,

    /// <summary>Почтовый сервер недоступен или оборвал соединение.</summary>
    ServerUnavailable,

    /// <summary>Сервер отказал в письме — например, не принял адрес.</summary>
    Rejected
}

/// <summary>Чем кончилось подтверждение по ссылке.</summary>
public enum EmailConfirmation
{
    /// <summary>Ссылка не найдена: устарела, использована или заменена новым запросом.</summary>
    Invalid,

    /// <summary>Подтверждено, ждём вторую ссылку.</summary>
    Waiting,

    /// <summary>Подтверждены обе ссылки, адрес изменён.</summary>
    Completed,

    /// <summary>Адрес успели занять, пока шло подтверждение; запрос снят, адрес прежний.</summary>
    Taken,

    /// <summary>Учётную запись отключили; запрос снят.</summary>
    AccountDisabled
}

/// <param name="Outcome">Чем кончилось подтверждение.</param>
/// <param name="NewEmail">Новый адрес запроса (для текста страницы).</param>
/// <param name="ConfirmedOld">Ссылка была со старого адреса (иначе — с нового).</param>
public sealed record EmailConfirmResult(EmailConfirmation Outcome, string? NewEmail = null, bool ConfirmedOld = false);

/// <summary>
/// Смена email пользователем (этап 8, A8; FR-USR-01, решение 7): новый адрес → два письма, на прежний и на новый, у
/// каждого своя ссылка. Адрес меняется, когда подтверждены обе, — у учётной записи (вход по новому адресу) и у записи
/// справочника сотрудников. До этого вход по старому. Запрос один на пользователя, срок — <see cref="AccountSettings.ResetLinkHours"/>.
/// <para>
/// Токены — случайные, в базе только хеши (<see cref="EmailChangeRequest"/>). Ссылка, как и в A6, держит токен во
/// фрагменте адреса: подтверждение выполняет страница формой (POST), а не переход по ссылке — иначе почтовые сканеры,
/// открывающие ссылки из писем, подтвердили бы смену за человека. Ссылка и токен в журнал не пишутся.
/// </para>
/// Старый ящик недоступен — адрес меняет администратор, без писем (отдельный путь, не этот).
/// </summary>
public sealed class EmailChanges(
    AppDbContext db, UserManager<AppUser> users, IMailSender mail, IOptions<MailSettings> mailSettings,
    IOptions<AccountSettings> accountSettings, TimeProvider clock, ILogger<EmailChanges> log)
{
    private const int MaxEmailLength = 255;

    /// <summary>Действующий (не просроченный) запрос пользователя или <c>null</c>.</summary>
    public async Task<EmailChangeRequest?> PendingAsync(Guid userId, CancellationToken ct = default)
    {
        var request = await db.EmailChangeRequests.AsNoTracking().FirstOrDefaultAsync(r => r.UserId == userId, ct);
        return request is not null && request.ExpiresAt > clock.GetUtcNow() ? request : null;
    }

    public async Task<EmailChangeStart> RequestAsync(AppUser user, string? newEmail, CancellationToken ct = default)
    {
        if (user.IsDisabled) return EmailChangeStart.AccountDisabled;
        var email = newEmail?.Trim() ?? "";
        if (!IsEmail(email)) return EmailChangeStart.InvalidEmail;
        var oldEmail = user.Email ?? "";
        if (string.Equals(email, oldEmail, StringComparison.OrdinalIgnoreCase)) return EmailChangeStart.SameAsCurrent;
        if (await IsTakenAsync(user, email, ct)) return EmailChangeStart.Taken;
        var publicUrl = mailSettings.Value.PublicUrl.TrimEnd('/');
        if (publicUrl.Length == 0) return EmailChangeStart.NoPublicUrl;

        var now = clock.GetUtcNow();
        var oldToken = NewToken();
        var newToken = NewToken();
        var validUntil = now.AddHours(accountSettings.Value.ResetLinkHours);

        // Новый запрос заменяет прежний: ключ — учётная запись, поэтому прежнюю строку убираем до вставки, и ссылки
        // прежнего запроса сразу перестают находиться.
        await db.EmailChangeRequests.Where(r => r.UserId == user.Id).ExecuteDeleteAsync(ct);
        db.EmailChangeRequests.Add(new EmailChangeRequest
        {
            UserId = user.Id, NewEmail = email, OldTokenHash = Hash(oldToken), NewTokenHash = Hash(newToken),
            CreatedAt = now, ExpiresAt = validUntil
        });
        await db.SaveChangesAsync(ct);

        try
        {
            await using var connection = await mail.OpenAsync(ct);
            await connection.SendAsync(AccountLetters.EmailChangeOld(
                oldEmail, user.FullName, email, Link(publicUrl, oldToken), validUntil, clock.LocalTimeZone), ct);
            await connection.SendAsync(AccountLetters.EmailChangeNew(
                email, user.FullName, oldEmail, Link(publicUrl, newToken), validUntil, clock.LocalTimeZone), ct);
            return EmailChangeStart.Requested;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Письма не дошли — запрос без ссылок у людей только занимает место: снимаем его, пользователь повторит.
            db.ChangeTracker.Clear();
            await db.EmailChangeRequests.Where(r => r.UserId == user.Id).ExecuteDeleteAsync(CancellationToken.None);
            if (e is MailServerUnavailableException)
            {
                log.LogWarning(e, "Почтовый сервер недоступен: письма о смене email для {UserId} не отправлены", user.Id);
                return EmailChangeStart.ServerUnavailable;
            }
            log.LogWarning(e, "Почтовый сервер не принял письмо о смене email для {UserId}", user.Id);
            return EmailChangeStart.Rejected;
        }
    }

    /// <summary>
    /// Подтверждает по ссылке из письма. Повторное нажатие уже подтверждённой ссылки безвредно (ответ тот же), пока вторая
    /// сторона не подтвердила; после смены адреса запрос удалён, и ссылка недействительна.
    /// </summary>
    public async Task<EmailConfirmResult> ConfirmAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return new(EmailConfirmation.Invalid);
        var hash = Hash(token.Trim());
        var request = await db.EmailChangeRequests.FirstOrDefaultAsync(r => r.OldTokenHash == hash || r.NewTokenHash == hash, ct);
        if (request is null) return new(EmailConfirmation.Invalid);

        var now = clock.GetUtcNow();
        if (request.ExpiresAt <= now)
        {
            db.EmailChangeRequests.Remove(request);
            await db.SaveChangesAsync(ct);
            return new(EmailConfirmation.Invalid);
        }

        var old = request.OldTokenHash == hash;
        if (old) request.OldConfirmedAt ??= now;
        else request.NewConfirmedAt ??= now;
        if (request.OldConfirmedAt is null || request.NewConfirmedAt is null)
        {
            await db.SaveChangesAsync(ct);
            return new(EmailConfirmation.Waiting, request.NewEmail, old);
        }
        return await ApplyAsync(request, old, ct);
    }

    private async Task<EmailConfirmResult> ApplyAsync(EmailChangeRequest request, bool old, CancellationToken ct)
    {
        var newEmail = request.NewEmail;
        var user = await users.FindByIdAsync(request.UserId.ToString());
        if (user is null || user.IsDisabled)
        {
            await DropAsync(request.UserId);
            return new(EmailConfirmation.AccountDisabled, newEmail, old);
        }
        // Пока шло подтверждение, адрес могли занять: проверяем заново, а не доверяем проверке при запросе.
        if (await IsTakenAsync(user, newEmail, ct))
        {
            await DropAsync(request.UserId);
            return new(EmailConfirmation.Taken, newEmail, old);
        }

        // Учётная запись и запись справочника — вместе или ничего.
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var result = await users.SetEmailAsync(user, newEmail);
            if (result.Succeeded) result = await users.SetUserNameAsync(user, newEmail);
            if (result.Succeeded)
            {
                // SetEmailAsync сбрасывает признак подтверждения, а новый адрес как раз подтверждён — ссылкой из письма.
                user.EmailConfirmed = true;
                result = await users.UpdateAsync(user);
            }
            if (!result.Succeeded)
            {
                await tx.RollbackAsync(ct);
                await DropAsync(request.UserId);
                return new(EmailConfirmation.Taken, newEmail, old);
            }

            var subject = user.Id.ToString();
            var employee = await db.Employees.FirstOrDefaultAsync(e => e.IdentitySubject == subject, ct);
            if (employee is not null)
            {
                employee.Email = newEmail;
                employee.UpdatedAt = clock.GetUtcNow();
            }
            var pending = await db.EmailChangeRequests.FirstOrDefaultAsync(r => r.UserId == request.UserId, ct);
            if (pending is not null) db.EmailChangeRequests.Remove(pending);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException e)
        {
            // Уникальный индекс справочника сработал раньше проверки — гонка двух смен на один адрес.
            log.LogWarning(e, "Смена email для {UserId} не применена: адрес занят", request.UserId);
            await DropAsync(request.UserId);
            return new(EmailConfirmation.Taken, newEmail, old);
        }
        return new(EmailConfirmation.Completed, newEmail, old);
    }

    private async Task DropAsync(Guid userId)
    {
        db.ChangeTracker.Clear();
        await db.EmailChangeRequests.Where(r => r.UserId == userId).ExecuteDeleteAsync();
    }

    // Занят: другой учётной записью (по имени входа или email) или любой записью справочника, кроме собственной.
    private async Task<bool> IsTakenAsync(AppUser user, string email, CancellationToken ct)
    {
        if (await users.FindByEmailAsync(email) is { } byEmail && byEmail.Id != user.Id) return true;
        if (await users.FindByNameAsync(email) is { } byName && byName.Id != user.Id) return true;
        var subject = user.Id.ToString();
        var lower = email.ToLower();
        return await db.Employees.AnyAsync(e => e.Email != null && e.Email.ToLower() == lower && e.IdentitySubject != subject, ct);
    }

    private static bool IsEmail(string email) =>
        email.Length is > 0 and <= MaxEmailLength && MailAddress.TryCreate(email, out var parsed)
        && parsed.Address == email && parsed.Host.Contains('.');

    private static string Link(string publicUrl, string token) => $"{publicUrl}/Account/ConfirmEmail#t={token}";

    // Токен — 32 случайных байта в URL-безопасном виде: экранировать в адресе нечего.
    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
