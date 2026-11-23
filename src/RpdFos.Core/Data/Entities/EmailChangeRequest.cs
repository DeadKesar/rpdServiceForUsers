namespace RpdFos.Core.Data.Entities;

/// <summary>
/// Незавершённая смена email (таблица <c>email_change_requests</c>; этап 8, A8) — дополнение модели данных: в проекте
/// (docs/design) такой таблицы нет. Одна строка на учётную запись: новый запрос заменяет прежний, и прежние ссылки
/// перестают находиться. Адрес меняется, когда подтверждены обе ссылки — со старого и с нового адреса.
/// <para>
/// Токены в базе — только хеши: чтение таблицы не даёт ссылок, которыми можно подтвердить смену.
/// </para>
/// </summary>
public class EmailChangeRequest
{
    /// <summary>Учётная запись (<see cref="AppUser.Id"/>); она же ключ: запрос на пользователя один.</summary>
    public Guid UserId { get; set; }

    public string NewEmail { get; set; } = "";

    /// <summary>SHA-256 токена из письма на прежний адрес (hex).</summary>
    public string OldTokenHash { get; set; } = "";

    /// <summary>SHA-256 токена из письма на новый адрес (hex).</summary>
    public string NewTokenHash { get; set; } = "";

    public DateTimeOffset? OldConfirmedAt { get; set; }
    public DateTimeOffset? NewConfirmedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
