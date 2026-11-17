namespace RpdFos.Web.Models;

// Контракт API профиля (Features/Profile) на стороне клиента.

/// <summary>
/// Профиль вошедшего: ФИО, email учётной записи, должность и подразделение из справочника, роли. Без записи справочника
/// (<c>HasDirectoryRecord</c> — <c>false</c>) настроек уведомлений нет.
/// </summary>
public sealed record ProfileItem(
    string FullName, string? Email, string? Position, string? Unit, List<ProfileRole> Roles, bool HasDirectoryRecord);

public sealed record ProfileRole(string Code, string Title);

/// <summary>
/// Строка таблицы настроек уведомлений: событие, показывать ли в интерфейсе и режим почты —
/// <c>immediate</c> (сразу), <c>daily</c> (раз в день) или <c>off</c> (выключено).
/// </summary>
public sealed record NotificationSettingItem(string EventType, string Title, bool InApp, string Mail);

/// <summary>Новая настройка события — то, что отправляет кнопка «Сохранить».</summary>
public sealed record NotificationSettingRequest(string EventType, bool InApp, string Mail);
