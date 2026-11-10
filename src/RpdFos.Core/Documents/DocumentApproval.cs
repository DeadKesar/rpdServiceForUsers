using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.DocumentTypes;

namespace RpdFos.Core.Documents;

public enum ApprovalProblem
{
    /// <summary>Документ не в том статусе.</summary>
    WrongStatus,

    /// <summary>Подписанты не всех электронных мест назначены.</summary>
    Unassigned,

    /// <summary>Такого места в согласовании документа нет.</summary>
    UnknownSeat,

    /// <summary>Строка уже отмечена: подписанта не заменить, отметку не повторить.</summary>
    AlreadyMarked,

    /// <summary>Записи справочника сотрудников нет или она неактивна.</summary>
    UnknownEmployee,

    /// <summary>У пользователя нет строки согласования в документе.</summary>
    NoSeat,

    /// <summary>Возврат без комментария.</summary>
    EmptyComment
}

/// <summary>Итог старта: согласование запущено или подписанты этих мест не найдены — их назначают вручную.</summary>
public sealed record ApprovalStart(bool Started, IReadOnlyList<string> Unassigned);

/// <summary>
/// Место электронного согласования документа: кто назначен и что отметил. <see cref="CanSign"/> — у назначенного
/// есть действующая учётная запись с ролью «Утверждающий»: без неё отметку поставить некому.
/// </summary>
public sealed record ApprovalSeatState(
    string Role, string Title, Guid? SignatoryId, string? SignatoryName, string? Position, string Status, DateTimeOffset? MarkedAt,
    string? Comment, bool CanSign);

/// <summary>Возврат с согласования: кто, когда, с каким комментарием.</summary>
public sealed record ApprovalReturn(string? By, DateTimeOffset At, string? Comment);

/// <summary>Круг согласования документа; <see cref="Returned"/> — круг закончился возвратом.</summary>
public sealed record ApprovalState(IReadOnlyList<ApprovalSeatState> Seats, ApprovalReturn? Returned);

/// <summary>
/// Параллельное электронное согласование (UC-07 – UC-09, FR-REV-04…06, FR-REV-13). Приём запускает круг: подписанты
/// электронных мест листа согласования шаблона находятся теми же правилами, что при печати. Согласующие отмечают
/// согласие в любом порядке; отметили все — документ согласован; любой возврат отправляет документ методисту.
/// <para>
/// Круги хранятся сбросом строк: в document_signatures — текущий круг с ключом (документ, место), как в проекте.
/// Строки возвращённого круга остаются до следующего приёма — методист видит, кто и почему вернул; новый круг
/// начинает их заново. Отметки и состав прежних кругов — в журнале аудита.
/// </para>
/// Права пользователя проверяет вызывающий; сохраняет тоже он.
/// </summary>
public sealed class DocumentApproval(AppDbContext db, SignatoryResolution signatories, DocumentHistory history, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Новый круг для только что принятого документа (UC-07, шаг 3): все подписанты найдены — документ сразу
    /// «На согласовании», иначе остаётся «Принят» до ручного назначения.
    /// </summary>
    public async Task<ApprovalStart> BeginAsync(Document document, Guid by, CancellationToken ct = default)
    {
        var seats = await SeatsAsync(document, ct);
        var rows = await ResetRoundAsync(document, seats, ct);
        var unassigned = Unassigned(rows, seats);
        if (unassigned.Count > 0) return new(false, unassigned);
        Start(document, rows, by);
        return new(true, []);
    }

    /// <summary>
    /// «Запустить согласование» принятого документа после ручного назначения. У документа, принятого до появления
    /// согласования, строк нет — круг начинается как при приёме.
    /// </summary>
    public async Task<(ApprovalProblem? Problem, ApprovalStart? Start)> StartAsync(Document document, Guid by, CancellationToken ct = default)
    {
        if (!DocumentTransitions.CanStartApproval(document.Status)) return (ApprovalProblem.WrongStatus, null);

        var rows = await RowsAsync(document, ct);
        ApprovalStart start;
        if (rows.Count == 0) start = await BeginAsync(document, by, ct);
        else
        {
            var unassigned = Unassigned(rows, await SeatsAsync(document, ct));
            if (unassigned.Count > 0) return (ApprovalProblem.Unassigned, new(false, unassigned));
            Start(document, rows, by);
            start = new(true, []);
        }
        if (start.Started) await history.SnapshotAsync(document, by, VersionTriggers.StatusChange, ct);
        return (null, start);
    }

    /// <summary>Назначить или заменить подписанта места — пока его отметки ждут (FR-REV-13). Замена — в журнал аудита.</summary>
    public async Task<ApprovalProblem?> AssignAsync(Document document, string role, Guid employeeId, Guid by, CancellationToken ct = default)
    {
        if (!DocumentTransitions.CanAssignSignatory(document.Status)) return ApprovalProblem.WrongStatus;
        var row = (await RowsAsync(document, ct)).SingleOrDefault(r => r.Role == role);
        if (row is null) return ApprovalProblem.UnknownSeat;
        if (row.Status != SignatureStatuses.Pending) return ApprovalProblem.AlreadyMarked;
        if (!await db.Employees.AnyAsync(e => e.Id == employeeId && e.IsActive, ct)) return ApprovalProblem.UnknownEmployee;
        if (row.ResolvedUserId == employeeId) return null;

        db.AuditLog.Add(Audit(document, by, AuditEvents.SignatoryAssigned,
            new JsonObject { ["role"] = row.Role, ["before"] = row.ResolvedUserId, ["after"] = employeeId }));
        row.Assign(employeeId);
        Touch(document);
        return null;
    }

    /// <summary>
    /// «Согласовать» (UC-08): отметка в своей строке; отметили все — документ «Согласован», снимок версии
    /// <c>approved</c>.
    /// </summary>
    public async Task<ApprovalProblem?> ApproveAsync(Document document, Guid signatoryId, CancellationToken ct = default)
    {
        var (mine, rows, problem) = await PendingOfAsync(document, signatoryId, ct);
        if (problem is not null) return problem;

        var now = clock.GetUtcNow();
        foreach (var row in mine)
        {
            row.Approve(now);
            db.AuditLog.Add(Audit(document, signatoryId, AuditEvents.SignatureApproved, new JsonObject { ["role"] = row.Role }));
        }
        Touch(document);
        if (rows.All(r => r.Status == SignatureStatuses.Approved))
        {
            history.StatusChanged(document, document.Approve(now), signatoryId);
            await history.SnapshotAsync(document, signatoryId, VersionTriggers.Approved, ct);
        }
        return null;
    }

    /// <summary>
    /// «Вернуть на проверку» (UC-09): комментарий обязателен — он остаётся в строке согласования и в истории
    /// статусов. Документ уходит методисту, круг окончен: следующий приём начнёт новый.
    /// </summary>
    public async Task<ApprovalProblem?> RejectAsync(Document document, Guid signatoryId, string? comment, CancellationToken ct = default)
    {
        var (mine, _, problem) = await PendingOfAsync(document, signatoryId, ct);
        if (problem is not null) return problem;
        if (string.IsNullOrWhiteSpace(comment)) return ApprovalProblem.EmptyComment;

        var text = comment.Trim();
        foreach (var row in mine)
        {
            row.Reject(text);
            db.AuditLog.Add(Audit(document, signatoryId, AuditEvents.SignatureRejected,
                new JsonObject { ["role"] = row.Role, ["comment"] = text }));
        }
        history.StatusChanged(document, document.ReturnFromApproval(), signatoryId, text);
        await history.SnapshotAsync(document, signatoryId, VersionTriggers.StatusChange, ct);
        return null;
    }

    /// <summary>У сотрудника есть строка согласования в документе — он его участник.</summary>
    public Task<bool> HasSeatAsync(Guid documentId, Guid employeeId, CancellationToken ct = default) =>
        db.DocumentSignatures.AnyAsync(s => s.DocumentId == documentId && s.ResolvedUserId == employeeId, ct);

    /// <summary>Круг согласования документа для панели «Согласование»: места — в порядке листа согласования шаблона.</summary>
    public async Task<ApprovalState> StateAsync(Document document, CancellationToken ct = default)
    {
        var rows = await db.DocumentSignatures.AsNoTracking().Where(s => s.DocumentId == document.Id).ToListAsync(ct);
        if (rows.Count == 0) return new([], null);

        var seats = await SeatsAsync(document, ct);
        var ids = rows.Select(r => r.ResolvedUserId).OfType<Guid>().Distinct().ToList();
        var people = await db.Employees.AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        var able = await ApproversAsync(people.Values, ct);
        var returned = rows.Any(r => r.Status == SignatureStatuses.Rejected) ? await LastReturnAsync(document.Id, ct) : null;

        var order = new Dictionary<string, int>();
        foreach (var seat in seats) order.TryAdd(seat.Role, order.Count);
        return new(rows.OrderBy(r => order.GetValueOrDefault(r.Role, int.MaxValue)).Select(r =>
        {
            var person = r.ResolvedUserId is Guid id ? people.GetValueOrDefault(id) : null;
            return new ApprovalSeatState(r.Role, seats.FirstOrDefault(s => s.Role == r.Role)?.Title ?? SignatoryRoles.Title(r.Role),
                person?.Id, person?.ShortName, person?.Position, r.Status,
                r.Status == SignatureStatuses.Rejected ? returned?.At : r.ApprovedAt, r.Comment,
                person is not null && able.Contains(person.Id));
        }).ToList(), returned);
    }

    // Электронные места листа согласования — из снимка в шаблоне документа.
    private async Task<IReadOnlyList<SignatureSeat>> SeatsAsync(Document document, CancellationToken ct) =>
        PrintMappingJson.ReadSignatures(await db.DocumentTemplates.IgnoreQueryFilters().Where(t => t.Id == document.TemplateId)
            .Select(t => t.SignaturesConfig).SingleAsync(ct)).Where(s => s.Electronic).ToList();

    private Task<List<DocumentSignature>> RowsAsync(Document document, CancellationToken ct) =>
        db.DocumentSignatures.Where(s => s.DocumentId == document.Id).ToListAsync(ct);

    // Новый круг: строка каждого электронного места ждёт отметки, подписант — заново по правилу шаблона. Ручное
    // назначение действует в пределах круга.
    private async Task<List<DocumentSignature>> ResetRoundAsync(Document document, IReadOnlyList<SignatureSeat> seats, CancellationToken ct)
    {
        var report = await signatories.ResolveAsync(seats, document.DisciplineId, ct);
        var rows = await RowsAsync(document, ct);
        var now = clock.GetUtcNow();
        foreach (var seat in seats)
        {
            var row = rows.FirstOrDefault(r => r.Role == seat.Role);
            if (row is null)
            {
                row = db.DocumentSignatures.Add(new DocumentSignature { DocumentId = document.Id, Role = seat.Role }).Entity;
                rows.Add(row);
            }
            row.Reset(seat.Resolver, report.Seat(seat.Id)?.EmployeeId, now);
        }
        foreach (var gone in rows.Where(r => seats.All(s => s.Role != r.Role)).ToList())
        {
            db.DocumentSignatures.Remove(gone);
            rows.Remove(gone);
        }
        return rows;
    }

    private static List<string> Unassigned(IEnumerable<DocumentSignature> rows, IReadOnlyList<SignatureSeat> seats) =>
        rows.Where(r => r.ResolvedUserId is null)
            .Select(r => seats.FirstOrDefault(s => s.Role == r.Role)?.Title ?? SignatoryRoles.Title(r.Role)).ToList();

    private void Start(Document document, IReadOnlyList<DocumentSignature> rows, Guid by)
    {
        history.StatusChanged(document, document.StartApproval(), by);
        db.AuditLog.Add(Audit(document, by, AuditEvents.ApprovalStarted, new JsonObject
        {
            ["seats"] = new JsonArray(rows.Select(r => (JsonNode)new JsonObject
            {
                ["role"] = r.Role, ["resolver"] = r.Resolver, ["signatory"] = r.ResolvedUserId
            }).ToArray())
        }));
        // В листе согласования шаблона нет электронных мест — отмечать некому: согласие «всех» уже есть (UC-08).
        if (rows.Count == 0) history.StatusChanged(document, document.Approve(clock.GetUtcNow()), by);
    }

    // Строки пользователя, ждущие отметки. Порядок отказов: чужой документ, не тот статус, отметка уже стоит.
    private async Task<(List<DocumentSignature> Mine, List<DocumentSignature> Rows, ApprovalProblem? Problem)> PendingOfAsync(
        Document document, Guid signatoryId, CancellationToken ct)
    {
        var rows = await RowsAsync(document, ct);
        var mine = rows.Where(r => r.ResolvedUserId == signatoryId).ToList();
        if (mine.Count == 0) return ([], rows, ApprovalProblem.NoSeat);
        if (!DocumentTransitions.CanSign(document.Status)) return ([], rows, ApprovalProblem.WrongStatus);
        var pending = mine.Where(r => r.Status == SignatureStatuses.Pending).ToList();
        return pending.Count == 0 ? ([], rows, ApprovalProblem.AlreadyMarked) : (pending, rows, null);
    }

    // Кто из назначенных может поставить отметку: действующая учётная запись с ролью «Утверждающий».
    private async Task<HashSet<Guid>> ApproversAsync(IEnumerable<Employee> people, CancellationToken ct)
    {
        var accounts = people.Where(e => e.IsActive && e.CanLogin && Guid.TryParse(e.IdentitySubject, out _))
            .ToDictionary(e => Guid.Parse(e.IdentitySubject!), e => e.Id);
        if (accounts.Count == 0) return [];
        var accountIds = accounts.Keys.ToList();
        var found = await (
                from ur in db.UserRoles
                join r in db.Roles on ur.RoleId equals r.Id
                join u in db.Users on ur.UserId equals u.Id
                where accountIds.Contains(ur.UserId) && r.Name == AppRoles.Approver
                select new { ur.UserId, u.LockoutEnd })
            .ToListAsync(ct);
        // Бессрочная блокировка — учётная запись отключена администратором (этап 5.2).
        return found.Where(a => a.LockoutEnd != DateTimeOffset.MaxValue).Select(a => accounts[a.UserId]).ToHashSet();
    }

    // Время и автор возврата — в истории статусов: последний переход «На согласовании → На проверке».
    private async Task<ApprovalReturn?> LastReturnAsync(Guid documentId, CancellationToken ct) =>
        await (
                from h in db.DocumentStatusHistory
                where h.DocumentId == documentId && h.FromStatus == DocumentStatuses.Approval && h.ToStatus == DocumentStatuses.UnderReview
                join e in db.Employees on h.ChangedBy equals e.Id
                orderby h.ChangedAt descending
                select new ApprovalReturn(e.ShortName, h.ChangedAt, h.Comment))
            .FirstOrDefaultAsync(ct);

    // Каждое действие согласования меняет документ: его версия упорядочивает одновременные отметки и назначения —
    // вторая запись получит конфликт версий (409), а не затрёт первую и не оставит документ без перехода в «Согласован».
    private void Touch(Document document) => db.Entry(document).Property(d => d.UpdatedAt).IsModified = true;

    private AuditLogEntry Audit(Document document, Guid by, string eventType, JsonObject payload) => new()
    {
        UserId = by, EventType = eventType, EntityType = AuditEntities.Document, EntityId = document.Id,
        OccurredAt = clock.GetUtcNow(), Payload = payload.ToJsonString(Json)
    };
}
