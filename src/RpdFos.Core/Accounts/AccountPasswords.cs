using Microsoft.AspNetCore.Identity;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.Accounts;

/// <summary>Чем кончилась смена своего пароля.</summary>
public enum PasswordChange
{
    Changed,

    /// <summary>Текущий пароль не подошёл (или у записи нет пароля): попытка засчитана в блокировку.</summary>
    WrongCurrent,

    /// <summary>Учётная запись отключена администратором.</summary>
    Disabled,

    /// <summary>Запись заблокирована после неудачных попыток входа, пароль не проверялся.</summary>
    LockedOut,

    /// <summary>Новый пароль совпал с текущим: временный пароль «сменой» на самого себя не заменить.</summary>
    SameAsCurrent,

    /// <summary>Новый пароль не прошёл требования; описание — в <see cref="PasswordChangeResult.Errors"/>.</summary>
    Weak
}

public sealed record PasswordChangeResult(PasswordChange Outcome, IReadOnlyList<string> Errors)
{
    public bool Succeeded => Outcome == PasswordChange.Changed;
}

/// <summary>
/// Смена своего пароля (этап 8, A7): одна для страницы входа (временный пароль) и для профиля (<c>POST /api/profile/password</c>).
/// Текущий пароль проверяется с учётом блокировки: иначе кто-то, севший за чужой открытый сеанс, подбирал бы пароль здесь,
/// мимо счётчика неудачных попыток входа.
/// </summary>
public sealed class AccountPasswords(UserManager<AppUser> users)
{
    public async Task<PasswordChangeResult> ChangeAsync(AppUser user, string? current, string? next)
    {
        if (user.IsDisabled)
            return Failed(PasswordChange.Disabled);
        if (await users.IsLockedOutAsync(user))
            return Failed(PasswordChange.LockedOut);
        if (string.IsNullOrEmpty(current) || !await users.CheckPasswordAsync(user, current))
        {
            await users.AccessFailedAsync(user);
            return Failed(await users.IsLockedOutAsync(user) ? PasswordChange.LockedOut : PasswordChange.WrongCurrent);
        }
        await users.ResetAccessFailedCountAsync(user);

        if (next == current)
            return Failed(PasswordChange.SameAsCurrent);

        var result = await users.ChangePasswordAsync(user, current, next ?? "");
        if (!result.Succeeded)
            return new PasswordChangeResult(PasswordChange.Weak, [.. result.Errors.Select(e => e.Description)]);

        await ClearMustChangeAsync(user);
        return new PasswordChangeResult(PasswordChange.Changed, []);
    }

    /// <summary>Снимает признак «сменить при входе»; ничего не пишет, если он и так не стоит.</summary>
    public async Task ClearMustChangeAsync(AppUser user)
    {
        if (!user.MustChangePassword) return;
        user.MustChangePassword = false;
        await users.UpdateAsync(user);
    }

    /// <summary>Текст для человека — один на страницу входа и на профиль.</summary>
    public static string Describe(PasswordChange outcome) => outcome switch
    {
        PasswordChange.WrongCurrent => "Текущий пароль указан неверно.",
        PasswordChange.Disabled => "Учётная запись отключена администратором.",
        PasswordChange.LockedOut => "Учётная запись временно заблокирована после нескольких неудачных попыток. Попробуйте позже.",
        PasswordChange.SameAsCurrent => "Новый пароль должен отличаться от текущего.",
        _ => "Новый пароль не принят."
    };

    private static PasswordChangeResult Failed(PasswordChange outcome) => new(outcome, [Describe(outcome)]);
}
