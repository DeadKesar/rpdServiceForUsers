using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.Documents;

public enum BankRenumberProblem
{
    /// <summary>В банке нет группы с таким названием — 404.</summary>
    NoSuchGroup,

    /// <summary>Два блока с одним названием: ссылки паспорта их не различают, перенумеровать нельзя — 409.</summary>
    DuplicateGroup,

    /// <summary>Номера уже идут подряд с единицы: перенумеровывать нечего — 409.</summary>
    AlreadyContinuous
}

/// <param name="Notice">Что сделано и что проверить — текст для автора; у отказа пусто.</param>
public sealed record BankRenumberResult(BankRenumberProblem? Problem, string? Detail = null, string? Notice = null,
    int Rewritten = 0, IReadOnlyList<string>? Dropped = null);

/// <summary>
/// Банк заданий ФОС (этап 7, B5; FR-DOC-22): правила нумерации. Номер хранится у задания и сам не меняется: новое задание
/// получает следующий за наибольшим, а удаление оставляет пропуск — ссылки паспорта на другие задания остаются верными.
/// «Перенумеровать» — отдельное действие автора: номера группы становятся сплошными с единицы, а ссылки паспорта на её задания
/// переписываются тем же сохранением (<see cref="Renumber"/>). Правила — чистые функции над содержимым, сохранение — в
/// <see cref="FosBankRenumbering"/>.
/// </summary>
public static class FosBank
{
    public const string BankSection = "task_bank", PassportSection = "passport";

    /// <summary>Номер для нового задания группы: следующий за наибольшим, а в пустой группе — первый.</summary>
    public static int NextNumber(JsonNode? tasks) =>
        (tasks as JsonArray ?? []).OfType<JsonObject>().Select(t => Number(t["number"]) ?? 0).DefaultIfEmpty(0).Max() + 1;

    /// <summary>
    /// Нумерует группу <paramref name="group"/> заново и переписывает ссылки паспорта (<c>{ group, from?, to? }</c>) на неё.
    /// Задания встают по старому номеру (при равных — как в списке). Диапазон ссылки пересчитывается по заданиям, которые в нём
    /// были: пропуск в старой нумерации не сдвигает чужие задания в диапазон. Ссылка на диапазон без единого задания не может
    /// сохранить смысл и удаляется — её называет результат. Ссылка на группу целиком не меняется. Содержимое меняется на месте,
    /// только если результат без отказа.
    /// </summary>
    public static BankRenumberResult Renumber(JsonObject payload, string group)
    {
        var blocks = string.IsNullOrEmpty(group) ? [] : (payload[BankSection] as JsonArray ?? []).OfType<JsonObject>()
            .Where(b => Text(b["group"]) == group).ToList();
        if (blocks.Count == 0)
            return new(BankRenumberProblem.NoSuchGroup, $"В банке нет группы «{group}».");
        if (blocks.Count > 1)
            return new(BankRenumberProblem.DuplicateGroup,
                $"В банке несколько групп с названием «{group}»: ссылки паспорта их не различают. Дайте группам разные названия.");

        var block = blocks[0];
        var tasks = (block["tasks"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        // Порядок по старому номеру, при равных (и у значения не числом) — как в списке: OrderBy устойчив.
        var ordered = tasks.OrderBy(t => Number(t["number"]) ?? int.MaxValue).ToList();
        var old = ordered.Select(t => Number(t["number"]) ?? int.MaxValue).ToList();
        if (ordered.SequenceEqual(tasks) && old.SequenceEqual(Enumerable.Range(1, old.Count)))
            return new(BankRenumberProblem.AlreadyContinuous, $"Задания группы «{group}» уже пронумерованы подряд.");

        var rewritten = 0;
        var dropped = new List<string>();
        foreach (var references in References(payload))
            for (var i = 0; i < references.Count; i++)
            {
                if (references[i] is not JsonObject reference || Text(reference["group"]) != group) continue;
                switch (Remap(reference, old))
                {
                    case Remapped.Same: break;
                    case Remapped.Changed: rewritten++; break;
                    case Remapped.Empty:
                        dropped.Add(Describe(reference));
                        references.RemoveAt(i--);
                        break;
                }
            }

        var fresh = new JsonArray();
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i]["number"] = i + 1;
            fresh.Add(ordered[i].DeepClone());
        }
        block["tasks"] = fresh;

        var notice = $"Задания группы «{group}» перенумерованы: 1–{ordered.Count}.";
        if (rewritten > 0) notice += $" Ссылок паспорта обновлено: {rewritten}.";
        if (dropped.Count > 0)
            notice += $" Удалены ссылки паспорта на несуществующие задания: {string.Join("; ", dropped)}.";
        return new(null, null, notice, rewritten, dropped);
    }

    private enum Remapped { Same, Changed, Empty }

    // Новый край диапазона: «сколько заданий было до него» + 1 для начала, «сколько было до конца включительно» для конца.
    private static Remapped Remap(JsonObject reference, IReadOnlyList<int> old)
    {
        var from = Number(reference["from"]);
        var to = Number(reference["to"]);
        if (reference["from"] is not null && from is null || reference["to"] is not null && to is null) return Remapped.Same;
        if (from is null && to is null) return Remapped.Same;

        var inside = old.Count(n => n >= (from ?? int.MinValue) && n <= (to ?? int.MaxValue));
        if (inside == 0) return Remapped.Empty;

        var changed = false;
        if (from is { } f)
        {
            var value = old.Count(n => n < f) + 1;
            changed |= value != f;
            reference["from"] = value;
        }
        if (to is { } t)
        {
            var value = old.Count(n => n <= t);
            changed |= value != t;
            reference["to"] = value;
        }
        return changed ? Remapped.Changed : Remapped.Same;
    }

    // Списки ссылок паспорта: на задания разделов и на задания индикаторов.
    private static IEnumerable<JsonArray> References(JsonObject payload)
    {
        if (payload[PassportSection] is not JsonObject passport) yield break;
        foreach (var list in new[] { "sections", "indicators" })
            foreach (var row in (passport[list] as JsonArray ?? []).OfType<JsonObject>())
                if (row["tasks"] is JsonArray tasks) yield return tasks;
    }

    private static string Describe(JsonObject reference)
    {
        var (from, to) = (Number(reference["from"]), Number(reference["to"]));
        return from is null && to is null ? Text(reference["group"]) ?? "" : from == to ? $"№ {from}"
            : $"{(from is null ? "" : from.ToString())}–{(to is null ? "" : to.ToString())}";
    }

    // Число из содержимого: пришедшее с сервера и созданное в клиенте хранятся по-разному — читаем через текст.
    private static int? Number(JsonNode? node) =>
        node is JsonValue && int.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}

/// <summary>
/// «Перенумеровать» группу банка (этап 7, B5): номера заданий и ссылки паспорта меняются одним сохранением — снимок версии,
/// история статусов (сохранённый черновик снова черновик), запись в журнале аудита (NFR-SEC-07). Кто перенумеровывает, решает
/// вызывающий — автор в статусах правки и методист на проверке, как при любой правке содержимого. Всё добавляется в контекст,
/// а сохраняет вызывающий — с версией клиента.
/// </summary>
public sealed class FosBankRenumbering(AppDbContext db, DocumentHistory history)
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public async Task<BankRenumberResult> RenumberAsync(Document fos, string group, Guid by, CancellationToken ct = default)
    {
        var payload = JsonNode.Parse(fos.Payload)!.AsObject();
        var result = FosBank.Renumber(payload, group);
        if (result.Problem is not null) return result;

        var at = history.NextMoment();
        if (fos.Edit(payload.ToJsonString(Json)) is { } previous) history.StatusChanged(fos, previous, by);
        await history.SnapshotAsync(fos, by, VersionTriggers.AutoSave, ct);
        db.AuditLog.Add(new AuditLogEntry
        {
            UserId = by, EventType = AuditEvents.BankRenumbered, EntityType = AuditEntities.Document, EntityId = fos.Id,
            OccurredAt = at,
            Payload = new JsonObject
            {
                ["group"] = group, ["rewritten_references"] = result.Rewritten,
                ["dropped_references"] = new JsonArray((result.Dropped ?? []).Select(d => (JsonNode?)JsonValue.Create(d)).ToArray())
            }.ToJsonString(Json)
        });
        return result;
    }
}
