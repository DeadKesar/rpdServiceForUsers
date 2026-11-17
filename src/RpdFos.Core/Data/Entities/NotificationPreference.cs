namespace RpdFos.Core.Data.Entities;

/// <summary>
/// Настройки уведомлений пользователя (таблица notification_preferences, раздел 2.4 ВКР, FR-REV-08; этап 7, A5): одна
/// строка на запись справочника сотрудников.
/// <para>
/// <see cref="Preferences"/> — jsonb по модели данных:
/// <c>{"&lt;тип события&gt;": {"channels": ["ui", "email"], "frequency": "immediate" | "daily"}}</c>.
/// <c>ui</c> — колокольчик в интерфейсе, <c>email</c> — почта; <c>frequency</c> относится к почте и есть, только когда
/// <c>email</c> в списке каналов. Хранятся лишь отклонения от умолчаний (<c>NotificationEvents.All</c>): событие без
/// записи работает по умолчанию, поэтому события, добавленные позже, не замораживаются на значениях первого сохранения.
/// Разбор и запись — <c>NotificationSettings</c>.
/// </para>
/// </summary>
public class NotificationPreference
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Запись справочника сотрудников; у одной записи — не больше одной строки настроек.</summary>
    public Guid UserId { get; set; }

    /// <summary>Настройки по событиям (jsonb).</summary>
    public string Preferences { get; set; } = "{}";
}
