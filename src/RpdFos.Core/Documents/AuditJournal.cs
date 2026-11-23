using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.Documents;

/// <summary>Условия отбора журнала; пустое условие не отбирает. Период — полуоткрытый: <c>From</c> включительно, <c>To</c> — нет.</summary>
public sealed record AuditFilter(Guid? UserId, DateTimeOffset? From, DateTimeOffset? To, string? EventType);

/// <summary>
/// Запись журнала аудита для администратора (этап 8, A11). <paramref name="DocumentId"/> — документ, к которому относится
/// событие, если он ещё есть: по нему интерфейс ставит ссылку.
/// </summary>
public sealed record AuditRecord(
    Guid Id, DateTimeOffset At, Guid? UserId, string? UserName, string EventType, string Title, string? EntityType, Guid? EntityId,
    string? EntityTitle, Guid? DocumentId, string? FromStatus, string? ToStatus, string? Comment, IReadOnlyList<string> Details);

public sealed record AuditPage(IReadOnlyList<AuditRecord> Items, int Total);

public sealed record AuditUserOption(Guid Id, string Name);

public sealed record AuditEventOption(string Code, string Title);

public sealed record AuditFilterOptions(IReadOnlyList<AuditUserOption> Users, IReadOnlyList<AuditEventOption> Events);

/// <summary>
/// Журнал аудита (этап 8, A11; FR-ADM-02, S-53): все события таблицы <c>audit_log</c> — от новых к старым, с отбором по
/// пользователю, периоду и типу события и выдачей частями. Только чтение: таблица пишется на месте событий, а не здесь.
/// <para>
/// В отличие от истории документа (<see cref="DocumentTimeline"/>), лента одна на всю систему и растёт без предела, поэтому отбор
/// и страницы выполняет база, а в памяти описывается только вырезанная часть.
/// </para>
/// </summary>
public sealed class AuditJournal(AppDbContext db)
{
    public const int DefaultPage = 50, MaxPage = 200;

    public async Task<AuditPage> PageAsync(AuditFilter filter, int skip, int take, CancellationToken ct = default)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, MaxPage);

        var query = db.AuditLog.AsNoTracking().AsQueryable();
        if (filter.UserId is { } user) query = query.Where(a => a.UserId == user);
        if (filter.From is { } from) query = query.Where(a => a.OccurredAt >= from);
        if (filter.To is { } to) query = query.Where(a => a.OccurredAt < to);
        if (!string.IsNullOrWhiteSpace(filter.EventType)) query = query.Where(a => a.EventType == filter.EventType);

        var total = await query.CountAsync(ct);
        // Id в порядке — чтобы события одного момента не перескакивали со страницы на страницу.
        var rows = await query.OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id).Skip(skip).Take(take).ToListAsync(ct);
        var payloads = rows.Select(r => Parse(r.Payload)).ToList();

        var personIds = rows.Where(r => r.UserId is not null).Select(r => r.UserId!.Value).ToHashSet();
        foreach (var p in payloads) personIds.UnionWith(AuditDescriber.PeopleIn(p));
        var names = await db.Employees.AsNoTracking().Where(e => personIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.ShortName, ct);
        string? NameOf(Guid? id) => id is { } v && names.TryGetValue(v, out var name) ? name : null;

        var docs = await DocumentsAsync(rows, payloads, ct);
        var titles = await TitlesAsync(rows, payloads, ct);
        var items = new List<AuditRecord>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var described = AuditDescriber.Describe(row, payloads[i], NameOf);
            // Причину отказа согласующего история документа берёт из перехода статуса, а в журнале перехода нет.
            var comment = row.EventType == AuditEvents.SignatureRejected ? payloads[i]?["comment"]?.ToString() : described.Comment;
            var target = row.EntityId is { } id && row.EntityType is { } kind && titles.TryGetValue((kind, id), out var t) ? t : null;
            // Ссылка — на документ события или, у закрепления при передаче, на документ из содержимого; у удалённого её нет.
            var documentId = row.EntityType == AuditEntities.Document ? row.EntityId : DocumentOf(payloads[i]);
            if (documentId is not { } doc || !docs.TryGetValue(doc, out var live) || !live.Alive) documentId = null;
            items.Add(new(row.Id, row.OccurredAt, row.UserId, NameOf(row.UserId), row.EventType, described.Title, row.EntityType,
                row.EntityId, target, documentId, described.FromStatus, described.ToStatus, comment, described.Details));
        }
        return new(items, total);
    }

    /// <summary>
    /// Условия отбора для страницы: те, кто есть в журнале (пустой список «всех сотрудников» ни к чему), и все названные события —
    /// фильтр по типу полезен и там, где событий ещё не было.
    /// </summary>
    public async Task<AuditFilterOptions> OptionsAsync(CancellationToken ct = default)
    {
        var authors = db.AuditLog.Where(a => a.UserId != null).Select(a => a.UserId!.Value).Distinct();
        var users = await db.Employees.AsNoTracking().Where(e => authors.Contains(e.Id)).OrderBy(e => e.ShortName)
            .Select(e => new AuditUserOption(e.Id, e.ShortName)).ToListAsync(ct);
        var events = AuditDescriber.KnownEvents.Select(e => new AuditEventOption(e, AuditDescriber.Title(e)))
            .OrderBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        return new(users, events);
    }

    private static Guid? DocumentOf(JsonNode? p) => Guid.TryParse(p?["document"]?.ToString(), out var id) ? id : null;

    // Документы строк: название и признак «ещё есть». Удалённые скрыты фильтром запросов, но в журнале остаются.
    private async Task<Dictionary<Guid, (string Title, bool Alive)>> DocumentsAsync(
        List<AuditLogEntry> rows, List<JsonNode?> payloads, CancellationToken ct)
    {
        var ids = rows.Where(r => r.EntityType == AuditEntities.Document && r.EntityId is not null).Select(r => r.EntityId!.Value)
            .Concat(payloads.Select(DocumentOf).OfType<Guid>()).Distinct().ToList();
        return (await db.Documents.IgnoreQueryFilters().AsNoTracking().Where(d => ids.Contains(d.Id))
                .Select(d => new { d.Id, d.Title, d.IsDeleted }).ToListAsync(ct))
            .ToDictionary(d => d.Id, d => (d.Title, !d.IsDeleted));
    }

    // Название объекта события: документ, сотрудник, дисциплина или строка матрицы.
    private async Task<Dictionary<(string Kind, Guid Id), string?>> TitlesAsync(
        List<AuditLogEntry> rows, List<JsonNode?> payloads, CancellationToken ct)
    {
        var result = new Dictionary<(string, Guid), string?>();
        IEnumerable<Guid> Ids(string kind) => rows.Where(r => r.EntityType == kind && r.EntityId is not null).Select(r => r.EntityId!.Value);

        var docIds = Ids(AuditEntities.Document).Distinct().ToList();
        foreach (var d in await db.Documents.IgnoreQueryFilters().AsNoTracking().Where(d => docIds.Contains(d.Id))
                     .Select(d => new { d.Id, d.Title }).ToListAsync(ct))
            result[(AuditEntities.Document, d.Id)] = d.Title;
        // Запись об удалении черновика называет документ сама: так название есть, даже если строки документа уже нет.
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].EventType == AuditEvents.DocumentDeleted && rows[i].EntityId is { } deleted && !result.ContainsKey((AuditEntities.Document, deleted)))
                result[(AuditEntities.Document, deleted)] = payloads[i]?["title"]?.ToString();

        var employeeIds = Ids(AuditEntities.Employee).Distinct().ToList();
        foreach (var e in await db.Employees.AsNoTracking().Where(e => employeeIds.Contains(e.Id)).Select(e => new { e.Id, e.ShortName }).ToListAsync(ct))
            result[(AuditEntities.Employee, e.Id)] = e.ShortName;

        var disciplineIds = Ids(AuditEntities.Discipline).Distinct().ToList();
        foreach (var d in await db.Disciplines.AsNoTracking().Where(d => disciplineIds.Contains(d.Id)).Select(d => new { d.Id, d.Code, d.Name }).ToListAsync(ct))
            result[(AuditEntities.Discipline, d.Id)] = $"{d.Code} {d.Name}";

        // Строка матрицы называет себя в содержимом события: сама строка после смены версии плана может исчезнуть.
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].EntityType == AuditEntities.MatrixRow && rows[i].EntityId is { } rowId)
                result[(AuditEntities.MatrixRow, rowId)] = payloads[i]?["row"]?.ToString();
        return result;
    }

    private static JsonNode? Parse(string json)
    {
        try { return JsonNode.Parse(json); }
        catch (JsonException) { return null; }
    }
}
