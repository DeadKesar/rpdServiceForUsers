using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.Notifications;

/// <summary>
/// Настройка одного события у пользователя (этап 7, A5): показывать ли его в интерфейсе (колокольчик и счётчик) и как
/// отправлять на почту — сразу, дайджестом раз в день или никак. Два канала независимы: письмо уходит и тогда, когда в
/// интерфейсе событие скрыто.
/// </summary>
public readonly record struct EventSetting(bool InApp, MailMode Mail)
{
    /// <summary>Умолчание события: в интерфейсе показывается всегда, почта — по <see cref="NotificationEventInfo.DefaultMail"/>.</summary>
    public static EventSetting DefaultOf(NotificationEventInfo info) => new(true, info.DefaultMail);
}

/// <summary>Коды режимов почты — в API и в <c>frequency</c> настроек (у «выключено» частоты нет: нет канала <c>email</c>).</summary>
public static class MailModeCodes
{
    public const string Off = "off", Immediate = "immediate", Daily = "daily";

    public static string Code(MailMode mode) => mode switch
    {
        MailMode.Immediately => Immediate,
        MailMode.Daily => Daily,
        _ => Off
    };

    /// <summary>Режим по коду; неизвестный код — <c>false</c>.</summary>
    public static bool TryParse(string? code, out MailMode mode)
    {
        (var known, mode) = code switch
        {
            Off => (true, MailMode.Off),
            Immediate => (true, MailMode.Immediately),
            Daily => (true, MailMode.Daily),
            _ => (false, MailMode.Off)
        };
        return known;
    }
}

/// <summary>
/// Настройки уведомлений пользователя (FR-REV-08; этап 7, A5): что действует сейчас — настройка пользователя или
/// умолчание события — и запись изменений в <c>notification_preferences</c>. Список событий — <see cref="NotificationEvents.All"/>,
/// другого перечня нет: событие, добавленное в него, появляется в таблице настроек само.
/// <para>
/// В базе хранятся только отклонения от умолчаний: настройка, равная умолчанию, удаляется, и пока пользователь событие не
/// менял, оно следует умолчанию. Строка, у которой отклонений не осталось, удаляется целиком.
/// </para>
/// </summary>
public sealed class NotificationSettings(AppDbContext db)
{
    // Каналы в jsonb — по модели данных: «ui» — интерфейс, «email» — почта.
    private const string UiChannel = "ui", EmailChannel = "email";

    /// <summary>Таблица настроек: все события в порядке перечня, с действующей настройкой.</summary>
    public async Task<IReadOnlyList<(NotificationEventInfo Event, EventSetting Setting)>> GetAsync(
        Guid employeeId, CancellationToken ct = default)
    {
        var stored = await LoadAsync(employeeId, ct);
        return NotificationEvents.All.Select(e => (e, Effective(e.Type, stored))).ToList();
    }

    /// <summary>
    /// Меняет настройки названных событий, остальные остаются как были. Неизвестное событие — ошибка вызывающего:
    /// проверка данных запроса — на нём.
    /// </summary>
    public async Task SaveAsync(Guid employeeId, IReadOnlyDictionary<string, EventSetting> changes, CancellationToken ct = default)
    {
        var row = await db.NotificationPreferences.SingleOrDefaultAsync(p => p.UserId == employeeId, ct);
        var stored = Parse(row?.Preferences);
        foreach (var (type, setting) in changes)
        {
            var info = NotificationEvents.Find(type) ?? throw new ArgumentException($"Неизвестное событие «{type}».", nameof(changes));
            if (setting == EventSetting.DefaultOf(info)) stored.Remove(type);
            else stored[type] = setting;
        }
        // События, которых уже нет в перечне, из настроек уходят: они ничего не меняют и копились бы вечно.
        foreach (var gone in stored.Keys.Where(type => NotificationEvents.Find(type) is null).ToList())
            stored.Remove(gone);

        if (stored.Count == 0)
        {
            if (row is not null) db.NotificationPreferences.Remove(row);
        }
        else if (row is null)
            db.NotificationPreferences.Add(new NotificationPreference { UserId = employeeId, Preferences = Serialize(stored) });
        else
            row.Preferences = Serialize(stored);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>События, скрытые у пользователя в интерфейсе: в колокольчик и в счётчик они не попадают.</summary>
    public async Task<List<string>> HiddenInAppAsync(Guid employeeId, CancellationToken ct = default) =>
        (await LoadAsync(employeeId, ct)).Where(s => !s.Value.InApp).Select(s => s.Key).ToList();

    private async Task<Dictionary<string, EventSetting>> LoadAsync(Guid employeeId, CancellationToken ct) =>
        Parse(await db.NotificationPreferences.AsNoTracking().Where(p => p.UserId == employeeId)
            .Select(p => p.Preferences).SingleOrDefaultAsync(ct));

    /// <summary>Действующая настройка события: сохранённая или умолчание; неизвестное событие — только интерфейс.</summary>
    public static EventSetting Effective(string eventType, IReadOnlyDictionary<string, EventSetting> stored) =>
        stored.TryGetValue(eventType, out var setting)
            ? setting
            : new EventSetting(true, NotificationEvents.DefaultMailOf(eventType));

    /// <summary>Разбор jsonb настроек. Запись, которую не удалось понять, пропускается: событие работает по умолчанию.</summary>
    public static Dictionary<string, EventSetting> Parse(string? json)
    {
        var result = new Dictionary<string, EventSetting>();
        if (string.IsNullOrWhiteSpace(json)) return result;

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return result;
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object) continue;
            var channels = entry.Value.TryGetProperty("channels", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()).ToList()
                : [];
            var frequency = entry.Value.TryGetProperty("frequency", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
            var mail = !channels.Contains(EmailChannel) ? MailMode.Off
                : frequency == MailModeCodes.Daily ? MailMode.Daily
                : MailMode.Immediately;
            result[entry.Name] = new EventSetting(channels.Contains(UiChannel), mail);
        }
        return result;
    }

    /// <summary>Запись настроек в jsonb: каналы события и, если включена почта, её частота.</summary>
    public static string Serialize(IReadOnlyDictionary<string, EventSetting> settings)
    {
        var root = new JsonObject();
        foreach (var (type, setting) in settings.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            var channels = new JsonArray();
            if (setting.InApp) channels.Add(UiChannel);
            if (setting.Mail != MailMode.Off) channels.Add(EmailChannel);
            var entry = new JsonObject { ["channels"] = channels };
            if (setting.Mail != MailMode.Off) entry["frequency"] = MailModeCodes.Code(setting.Mail);
            root[type] = entry;
        }
        return root.ToJsonString();
    }
}
