using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.Documents;

public enum FosRefreshProblem
{
    /// <summary>Связанная РПД удалена или её нет: обновлять не из чего — 409.</summary>
    RpdMissing,

    /// <summary>Данные РПД не менялись с момента снимка: обновлять нечего — 409.</summary>
    UpToDate
}

/// <param name="Notice">Что изменилось в снимке — текст для автора; у отказа пусто.</param>
public sealed record FosRefreshResult(FosRefreshProblem? Problem, string? Detail = null, string? Notice = null);

/// <summary>
/// «Обновить из РПД» (этап 7, B3, решение 8): снимок наследуемых данных ФОС заменяется снимком по нынешнему содержимому РПД.
/// Меняются только связь и список индикаторов; паспорт, оценочные средства, банк заданий и билеты остаются как есть — строки
/// с индикаторами и разделами, которых в РПД больше нет, не удаляются, а их список называется в ответе: автор решает сам, а
/// правила ФОС укажут на такие строки (B8). Версия РПД в связи и время снимка обновляются.
/// <para>
/// Кто обновляет, решает вызывающий — автор ФОС в статусах правки и методист на проверке (<see cref="DocumentTransitions.CanRefreshFromRpd"/>).
/// Правка — как любое изменение содержимого: снимок версии, история статусов (сохранённый черновик снова черновик), запись в
/// журнале аудита (NFR-SEC-07). Всё добавляется в контекст, а сохраняет вызывающий — с версией клиента.
/// </para>
/// </summary>
public sealed class FosRefresh(AppDbContext db, LinkedDocuments links, DocumentHistory history)
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // Сколько кодов называет сообщение: правка РПД может затронуть десятки индикаторов, а сообщение должно читаться.
    private const int Named = 8;

    public async Task<FosRefreshResult> RefreshAsync(Document fos, Guid by, CancellationToken ct = default)
    {
        var rpd = await links.RpdOfAsync(fos, ct);
        if (rpd is null || rpd.IsDeleted)
            return new(FosRefreshProblem.RpdMissing, "Связанная РПД удалена: обновлять не из чего.");

        var payload = JsonNode.Parse(fos.Payload)!.AsObject();
        var stored = payload[FosSnapshot.LinkSection];
        var fresh = FosSnapshot.Take(JsonNode.Parse(rpd.Payload));
        if (!FosSnapshot.Differs(stored, fresh))
            return new(FosRefreshProblem.UpToDate, "Данные РПД не менялись со времени снимка: обновлять нечего.");

        var changes = FosSnapshot.Changes(stored, fresh);
        var (was, _) = FosSnapshot.Taken(stored);
        var at = history.NextMoment();
        FosSnapshot.Apply(payload, rpd.Id, rpd.Version, at, fresh);
        FosSnapshot.RenamePassportSections(payload, stored);

        if (fos.Edit(payload.ToJsonString(Json)) is { } previous) history.StatusChanged(fos, previous, by);
        await history.SnapshotAsync(fos, by, VersionTriggers.AutoSave, ct);
        db.AuditLog.Add(new AuditLogEntry
        {
            UserId = by, EventType = AuditEvents.RpdSnapshotRefreshed, EntityType = AuditEntities.Document, EntityId = fos.Id,
            OccurredAt = at,
            Payload = new JsonObject
            {
                ["rpd"] = rpd.Id, ["from_version"] = was, ["to_version"] = rpd.Version,
                ["added_indicators"] = Codes(changes.AddedIndicators), ["removed_indicators"] = Codes(changes.RemovedIndicators),
                ["added_sections"] = Codes(changes.AddedSections.Select(n => n.ToString())),
                ["removed_sections"] = Codes(changes.RemovedSections.Select(n => n.ToString()))
            }.ToJsonString(Json)
        });
        return new(null, null, Notice(changes));
    }

    /// <summary>«Данные РПД обновлены. Добавлены индикаторы: …. Исчезли из РПД: ….» — что изменилось и что проверить.</summary>
    public static string Notice(SnapshotChanges changes)
    {
        var parts = new List<string> { "Данные РПД обновлены." };
        if (changes.AddedIndicators.Count > 0) parts.Add($"Добавлены индикаторы: {Listed(changes.AddedIndicators)}.");
        if (changes.AddedSections.Count > 0) parts.Add($"Добавлены разделы: {Listed(changes.AddedSections.Select(n => n.ToString()).ToList())}.");
        var gone = new List<string>();
        if (changes.RemovedIndicators.Count > 0) gone.Add($"индикаторы {Listed(changes.RemovedIndicators)}");
        if (changes.RemovedSections.Count > 0) gone.Add($"разделы {Listed(changes.RemovedSections.Select(n => n.ToString()).ToList())}");
        if (gone.Count > 0)
            parts.Add($"Исчезли из РПД: {string.Join("; ", gone)}. Строки паспорта и оценочных средств с ними не удалены — проверьте их.");
        return string.Join(' ', parts);
    }

    private static string Listed(IReadOnlyList<string> codes) =>
        string.Join(", ", codes.Take(Named)) + (codes.Count > Named ? $" и ещё {codes.Count - Named}" : "");

    private static JsonArray Codes(IEnumerable<string> codes)
    {
        var result = new JsonArray();
        foreach (var code in codes) result.Add(code);
        return result;
    }
}
