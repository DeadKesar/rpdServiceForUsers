using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Directory;

namespace RpdFos.Core.Documents;

/// <summary>Дисциплина версии плана в закреплении — для текста уведомления: индекс, название и план.</summary>
/// <param name="PlanTitle">«09.04.04, набор 2023, версия 1».</param>
public sealed record AssignedDiscipline(Guid Id, string Code, string Name, Guid PlanId, string PlanTitle);

/// <summary>Закреплённый за дисциплиной: запись справочника и «Фамилия И. О.».</summary>
public sealed record AssignmentHolder(Guid EmployeeId, string Name);

public enum AssignmentProblem
{
    /// <summary>Не выбрано ни одной дисциплины.</summary>
    NoDisciplines,

    /// <summary>Дисциплины с таким идентификатором нет.</summary>
    DisciplineNotFound,

    /// <summary>Строка плана — не дисциплина, для которой создаётся документ (модуль, практика, ГИА…, FR-DOC-18).</summary>
    NotADiscipline,

    /// <summary>Сотрудника нет в справочнике или его запись неактивна.</summary>
    EmployeeNotFound,

    /// <summary>У сотрудника нет действующей учётной записи преподавателя: войти и создать документ он не сможет.</summary>
    NoTeacherAccount
}

/// <param name="Added">Закреплений добавлено.</param>
/// <param name="AlreadyAssigned">Дисциплин, которые за сотрудником уже были закреплены.</param>
public sealed record AssignmentResult(AssignmentProblem? Problem, string? Detail = null, int Added = 0, int AlreadyAssigned = 0);

/// <summary>
/// Закрепление дисциплин за сотрудниками (этап 7, A10; решение 5): методист закрепляет сотрудников за дисциплинами версии
/// плана. Закреплённый видит документы дисциплины (только для чтения), создаёт по ней РПД и ФОС и берёт её документы основой
/// нового (A11); правит документ по-прежнему только автор (FR-DOC-14) — другой человек продолжает его после передачи (A9).
/// У дисциплины с закреплениями документы создают только закреплённые преподаватели, без закреплений — любой автор, как раньше.
/// <para>
/// Закрепить можно запись справочника с действующей учётной записью преподавателя — тот же отбор, что у нового автора при
/// передаче документа: закреплённый должен уметь войти и создать документ. Закрепление и снятие — записи в журнале аудита
/// (NFR-SEC-07) и уведомление сотруднику. Всё добавляется в контекст, а сохраняет вызывающий — одним сохранением.
/// </para>
/// </summary>
public sealed class DisciplineAssignments(
    AppDbContext db, DocumentHistory history, DocumentNotifications notifications, DirectoryAccounts directory)
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // Сколько фамилий называет отказ: у дисциплины их может быть много, а отказ должен читаться.
    private const int NamedHolders = 3;

    /// <summary>
    /// Закрепляет дисциплины за сотрудником. Порядок отказов: данные запроса — дисциплины и сотрудник (все 422). Уже
    /// закреплённые дисциплины не повторяются и отказом не считаются — их число в ответе. Сотруднику приходит одно
    /// уведомление на учебный план — о новых закреплениях; если новых нет, уведомления нет.
    /// </summary>
    /// <param name="byRole">Роль закрепившего (<see cref="AppRoles"/>): в уведомлении она названа.</param>
    public async Task<AssignmentResult> AssignAsync(
        IReadOnlyCollection<Guid> disciplineIds, Guid employeeId, Guid by, string byRole, CancellationToken ct = default)
    {
        var ids = disciplineIds.Distinct().ToList();
        if (ids.Count == 0) return new(AssignmentProblem.NoDisciplines);

        var rows = await LoadAsync(ids, ct);
        if (rows.Count != ids.Count) return new(AssignmentProblem.DisciplineNotFound);
        if (rows.Select(r => DocumentRules.WhyNoDocument(r.Row)).FirstOrDefault(reason => reason is not null) is { } why)
            return new(AssignmentProblem.NotADiscipline, why);

        if (!await db.Employees.AnyAsync(e => e.Id == employeeId && e.IsActive, ct)) return new(AssignmentProblem.EmployeeNotFound);
        if (await directory.RolesOfActiveAsync(employeeId, ct) is not { } roles || !roles.Contains(AppRoles.Teacher))
            return new(AssignmentProblem.NoTeacherAccount);

        var held = (await db.DisciplineAssignments.Where(a => a.EmployeeId == employeeId && ids.Contains(a.DisciplineId))
            .Select(a => a.DisciplineId).ToListAsync(ct)).ToHashSet();
        var added = new List<AssignedDiscipline>();
        foreach (var (_, discipline) in rows.Where(r => !held.Contains(r.Discipline.Id)))
        {
            var at = history.NextMoment();
            db.DisciplineAssignments.Add(new DisciplineAssignment
            {
                DisciplineId = discipline.Id, EmployeeId = employeeId, AssignedBy = by, AssignedAt = at
            });
            Audit(AuditEvents.DisciplineAssigned, discipline.Id, by, employeeId, at);
            added.Add(discipline);
        }
        await notifications.DisciplinesAssignedAsync(added, employeeId, by, byRole, history.NextMoment(), ct);
        return new(null, null, added.Count, held.Count);
    }

    /// <summary>
    /// Снимает закрепление. Сотруднику, у которого нет действующей учётной записи (он ушёл — главный случай), уведомление
    /// не создаётся: его некому прочесть (как при передаче документа, A9). <c>false</c> — такого закрепления нет.
    /// </summary>
    public async Task<bool> UnassignAsync(Guid assignmentId, Guid by, string byRole, CancellationToken ct = default)
    {
        var assignment = await db.DisciplineAssignments.SingleOrDefaultAsync(a => a.Id == assignmentId, ct);
        if (assignment is null) return false;

        var discipline = (await LoadAsync([assignment.DisciplineId], ct)).Single().Discipline;
        db.DisciplineAssignments.Remove(assignment);
        var at = history.NextMoment();
        Audit(AuditEvents.DisciplineUnassigned, discipline.Id, by, assignment.EmployeeId, at);
        if (await directory.RolesOfActiveAsync(assignment.EmployeeId, ct) is not null)
            await notifications.DisciplineUnassignedAsync(discipline, assignment.EmployeeId, by, byRole, at, ct);
        return true;
    }

    /// <summary>
    /// Передача документа (A9) закрепляет нового автора, если у дисциплины есть закрепления и среди них его нет: иначе
    /// дисциплина с закреплениями осталась бы с документом человека, который по ней больше ничего создать не может. Без
    /// закреплений ничего не меняется. Уведомления нет — новый автор получает «Вам передан документ». В аудите закрепление
    /// назвало документ, из-за которого появилось.
    /// </summary>
    public async Task EnsureAssignedAsync(Document document, Guid employeeId, Guid by, CancellationToken ct = default)
    {
        var holders = await db.DisciplineAssignments.Where(a => a.DisciplineId == document.DisciplineId)
            .Select(a => a.EmployeeId).ToListAsync(ct);
        if (holders.Count == 0 || holders.Contains(employeeId)) return;

        var at = history.NextMoment();
        db.DisciplineAssignments.Add(new DisciplineAssignment
        {
            DisciplineId = document.DisciplineId, EmployeeId = employeeId, AssignedBy = by, AssignedAt = at
        });
        Audit(AuditEvents.DisciplineAssigned, document.DisciplineId, by, employeeId, at, document.Id);
    }

    /// <summary>Закреплённые за дисциплиной — по алфавиту; пусто, если закреплений нет.</summary>
    public async Task<List<AssignmentHolder>> HoldersAsync(Guid disciplineId, CancellationToken ct = default) =>
        await (
                from a in db.DisciplineAssignments.AsNoTracking()
                join e in db.Employees on a.EmployeeId equals e.Id
                where a.DisciplineId == disciplineId
                orderby e.ShortName
                select new AssignmentHolder(e.Id, e.ShortName))
            .ToListAsync(ct);

    /// <summary>Закреплена ли дисциплина за сотрудником.</summary>
    public async Task<bool> IsAssignedAsync(Guid disciplineId, Guid employeeId, CancellationToken ct = default) =>
        await db.DisciplineAssignments.AnyAsync(a => a.DisciplineId == disciplineId && a.EmployeeId == employeeId, ct);

    /// <summary>Отказ преподавателю создать документ по закреплённой за другими дисциплине.</summary>
    public static string Refusal(IReadOnlyList<AssignmentHolder> holders)
    {
        // «Иванов И. И.» уже кончается точкой; «и ещё 2» — нет.
        var names = Names(holders);
        return $"Дисциплина закреплена за {(holders.Count == 1 ? "сотрудником" : "сотрудниками")}: {(names.EndsWith('.') ? names : names + ".")} " +
               "Документы по ней создают закреплённые преподаватели — обратитесь к методисту.";
    }

    /// <summary>«Иванов И. И., Петров П. П.»: не больше трёх фамилий, остальные — числом.</summary>
    public static string Names(IReadOnlyList<AssignmentHolder> holders) =>
        string.Join(", ", holders.Take(NamedHolders).Select(h => h.Name)) +
        (holders.Count > NamedHolders ? $" и ещё {holders.Count - NamedHolders}" : "");

    // Строки плана с планом и направлением: строка нужна правилу «для какой строки создаётся документ», остальное — уведомлению.
    private async Task<List<(Discipline Row, AssignedDiscipline Discipline)>> LoadAsync(List<Guid> ids, CancellationToken ct)
    {
        var found = await (
                from d in db.Disciplines.AsNoTracking()
                join p in db.CurriculumPlans on d.CurriculumPlanId equals p.Id
                join s in db.StudyDirections on p.DirectionId equals s.Id
                where ids.Contains(d.Id)
                orderby d.Code
                select new { Row = d, p.Year, p.Version, DirectionCode = s.Code })
            .ToListAsync(ct);
        return found.Select(f => (f.Row, new AssignedDiscipline(f.Row.Id, f.Row.Code, f.Row.Name, f.Row.CurriculumPlanId,
            $"{f.DirectionCode}, набор {f.Year}, версия {f.Version}"))).ToList();
    }

    private void Audit(string eventType, Guid disciplineId, Guid by, Guid employeeId, DateTimeOffset at, Guid? documentId = null)
    {
        var payload = new JsonObject { ["employee"] = employeeId };
        if (documentId is Guid id) payload["document"] = id;
        db.AuditLog.Add(new AuditLogEntry
        {
            UserId = by, EventType = eventType, EntityType = AuditEntities.Discipline, EntityId = disciplineId, OccurredAt = at,
            Payload = payload.ToJsonString(Json)
        });
    }
}
