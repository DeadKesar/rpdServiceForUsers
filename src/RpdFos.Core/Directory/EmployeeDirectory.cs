using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.Directory;

/// <summary>Поля записи справочника, которые ведёт администратор.</summary>
public sealed record EmployeeData(
    string ShortName, string? NameForDocument, string? FullName, string? Email, string? Position, string? AcademicDegree,
    string? OfficialDesignation, string? SignatoryRole, Guid? DepartmentId, Guid? InstituteId);

public enum DirectoryProblemKind { NotFound, Invalid, Conflict }

/// <summary>Отказ: что не так (422 — неверные данные, 409 — конфликт с другой записью) и понятный текст.</summary>
public sealed record DirectoryProblem(DirectoryProblemKind Kind, string Title, string Detail);

/// <summary>
/// Справочник сотрудников на странице администратора (этап 5.2, S-51): правила — общие со загрузкой файла
/// должностей. Официальное название уникально среди активных записей; место подписанта — одно на кафедру и на
/// институт; категория согласована с оргструктурой (<c>ck_users_signatory_scope</c>); смена человека на должности
/// освобождает прежнюю запись (<see cref="Vacate"/>). Каждое изменение — запись в журнале аудита: кто, когда, было,
/// стало. Сохраняет вызывающий.
/// </summary>
public sealed class EmployeeDirectory(AppDbContext db, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Место подписанта: заведующий (и исполняющий обязанности) — одно на кафедру, директор — одно на институт;
    /// у остальных категорий единственность не проверяется. null — места нет.
    /// </summary>
    public static string? SeatOf(string? role, Guid? departmentId, Guid? instituteId) => role switch
    {
        SignatoryRoles.DepartmentHead or SignatoryRoles.DepartmentHeadActing when departmentId is not null => $"{role}:{departmentId}",
        SignatoryRoles.InstituteDirector => $"{role}:{instituteId}",
        _ => null
    };

    /// <summary>
    /// Прежний сотрудник на должности: место (официальное название и категория подписанта) снимается, а не
    /// переписывается человек — на запись ссылаются прошлые согласования. Справочная запись без учётной записи больше
    /// не нужна — неактивна; с учётной записью остаётся активной: бывший заведующий, оставшийся преподавателем, не
    /// теряет доступ.
    /// </summary>
    public static void Vacate(Employee previous, DateTimeOffset now)
    {
        previous.OfficialDesignation = null;
        previous.SignatoryRole = null;
        if (previous.IdentitySubject is null) previous.IsActive = false;
        previous.UpdatedAt = now;
    }

    public async Task<(Employee? Employee, DirectoryProblem? Problem)> CreateAsync(EmployeeData data, Guid by, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var employee = new Employee { CanLogin = false, CreatedAt = now };
        if (await ApplyAsync(employee, data, ct) is { } problem) return (null, problem);
        employee.UpdatedAt = now;
        db.Employees.Add(employee);
        Audit(AuditEvents.EmployeeCreated, employee.Id, by, null, Snapshot(employee));
        return (employee, null);
    }

    public async Task<(Employee? Employee, DirectoryProblem? Problem)> UpdateAsync(Guid id, EmployeeData data, Guid by, CancellationToken ct)
    {
        var employee = await db.Employees.SingleOrDefaultAsync(e => e.Id == id, ct);
        if (employee is null) return (null, NotFound());
        var before = Snapshot(employee);
        if (await ApplyAsync(employee, data, ct) is { } problem) return (null, problem);
        employee.UpdatedAt = clock.GetUtcNow();
        Audit(AuditEvents.EmployeeUpdated, employee.Id, by, before, Snapshot(employee));
        return (employee, null);
    }

    /// <summary>Деактивировать (<paramref name="active"/> = false) или вернуть запись: возвращённая — по тем же правилам места.</summary>
    public async Task<(Employee? Employee, DirectoryProblem? Problem)> SetActiveAsync(Guid id, bool active, Guid by, CancellationToken ct)
    {
        var employee = await db.Employees.SingleOrDefaultAsync(e => e.Id == id, ct);
        if (employee is null) return (null, NotFound());
        if (employee.IsActive == active) return (employee, null);
        var before = Snapshot(employee);
        if (active && await ConflictAsync(employee.Id, employee.OfficialDesignation, employee.SignatoryRole, employee.DepartmentId,
                employee.InstituteId, ct) is { } conflict)
            return (null, conflict);
        employee.IsActive = active;
        employee.UpdatedAt = clock.GetUtcNow();
        Audit(active ? AuditEvents.EmployeeReactivated : AuditEvents.EmployeeDeactivated, employee.Id, by, before, Snapshot(employee));
        return (employee, null);
    }

    /// <summary>
    /// «Сменить сотрудника на должности»: место (официальное название, должность, категория подписанта и его
    /// подразделение) переходит к выбранной активной записи или к новой (<paramref name="newcomer"/>); прежняя
    /// запись освобождается по правилам импорта (<see cref="Vacate"/>).
    /// </summary>
    public async Task<(Employee? Employee, DirectoryProblem? Problem)> ReplaceAsync(
        Guid holderId, Guid? successorId, EmployeeData? newcomer, Guid by, CancellationToken ct)
    {
        var holder = await db.Employees.SingleOrDefaultAsync(e => e.Id == holderId && e.IsActive, ct);
        if (holder is null) return (null, NotFound());
        if (holder.OfficialDesignation is null && holder.SignatoryRole is null)
            return (null, new(DirectoryProblemKind.Invalid, "У записи нет должности",
                "Сменить сотрудника можно только на должности с официальным названием или категорией подписанта."));

        Employee successor;
        if (successorId is Guid sid)
        {
            if (sid == holderId) return (null, new(DirectoryProblemKind.Invalid, "Тот же сотрудник", "Выберите другого сотрудника."));
            var found = await db.Employees.SingleOrDefaultAsync(e => e.Id == sid && e.IsActive, ct);
            if (found is null) return (null, NotFound());
            if (found.OfficialDesignation is not null || found.SignatoryRole is not null)
                return (null, new(DirectoryProblemKind.Conflict, "Сотрудник уже на должности",
                    $"«{found.ShortName}» занимает место «{found.OfficialDesignation ?? SignatoryRoles.Title(found.SignatoryRole!)}»."));
            successor = found;
        }
        else if (newcomer is not null)
        {
            if (Validate(newcomer with { OfficialDesignation = null, SignatoryRole = null }) is { } invalid) return (null, invalid);
            successor = new Employee { CanLogin = false, CreatedAt = clock.GetUtcNow() };
            Fill(successor, newcomer with { OfficialDesignation = null, SignatoryRole = null });
            db.Employees.Add(successor);
        }
        else return (null, new(DirectoryProblemKind.Invalid, "Не выбран сотрудник", "Выберите запись справочника или введите нового сотрудника."));

        var now = clock.GetUtcNow();
        var holderBefore = Snapshot(holder);
        var successorBefore = db.Entry(successor).State == EntityState.Added ? null : Snapshot(successor);
        (string? designation, string? position, string? role, Guid? department, Guid? institute) seat =
            (holder.OfficialDesignation, holder.Position, holder.SignatoryRole, holder.DepartmentId, holder.InstituteId);
        Vacate(holder, now);
        // Уникальные индексы должности и места подписанта: прежнюю запись освободить до того, как место займёт новая.
        await db.SaveChangesAsync(ct);

        successor.OfficialDesignation = seat.designation;
        successor.Position = seat.position;
        successor.SignatoryRole = seat.role;
        if (seat.role is not null) (successor.DepartmentId, successor.InstituteId) = (seat.department, seat.institute);
        successor.IsActive = true;
        successor.UpdatedAt = now;
        Audit(AuditEvents.EmployeeReplaced, holder.Id, by, holderBefore, Snapshot(holder));
        Audit(successorBefore is null ? AuditEvents.EmployeeCreated : AuditEvents.EmployeeReplaced, successor.Id, by, successorBefore,
            Snapshot(successor));
        return (successor, null);
    }

    // Проверка и перенос полей: неверные данные — 422, конфликт с другой активной записью — 409.
    private async Task<DirectoryProblem?> ApplyAsync(Employee employee, EmployeeData data, CancellationToken ct)
    {
        if (Validate(data) is { } invalid) return invalid;
        var email = Trim(data.Email);
        if (email is not null && await db.Employees.AnyAsync(e => e.Email == email && e.Id != employee.Id, ct))
            return new(DirectoryProblemKind.Conflict, "Email уже в справочнике", $"Email {email} уже у другой записи справочника.");
        if (employee.IdentitySubject is not null && email != employee.Email)
            return new(DirectoryProblemKind.Invalid, "Email учётной записи не меняется здесь",
                "Запись связана с учётной записью: её email — адрес входа.");
        if (employee.IsActive && await ConflictAsync(employee.Id, Trim(data.OfficialDesignation), data.SignatoryRole,
                data.DepartmentId, data.InstituteId, ct) is { } conflict)
            return conflict;
        if (data.DepartmentId is Guid d && !await db.Departments.AnyAsync(x => x.Id == d, ct) ||
            data.InstituteId is Guid i && !await db.Institutes.AnyAsync(x => x.Id == i, ct))
            return new(DirectoryProblemKind.Invalid, "Подразделение не найдено", "Кафедра или институт отсутствуют в справочнике.");
        Fill(employee, data);
        return null;
    }

    /// <summary>
    /// Данные записи сами по себе: «Фамилия И. О.» обязательна, категория — из <see cref="SignatoryRoles"/> и согласована
    /// с оргструктурой (как <c>ck_users_signatory_scope</c>): заведующий — с кафедрой, директор — с институтом и без
    /// кафедры, остальные категории — без подразделения.
    /// </summary>
    public static DirectoryProblem? Validate(EmployeeData data)
    {
        if (string.IsNullOrWhiteSpace(data.ShortName))
            return new(DirectoryProblemKind.Invalid, "Не указаны фамилия и инициалы", "Поле «Фамилия И. О.» обязательно.");
        if (data.SignatoryRole is not { } role) return null;
        if (!SignatoryRoles.All.Contains(role))
            return new(DirectoryProblemKind.Invalid, "Неизвестная категория подписанта", $"Категории «{role}» нет.");
        return role switch
        {
            SignatoryRoles.DepartmentHead or SignatoryRoles.DepartmentHeadActing when data.DepartmentId is null =>
                new(DirectoryProblemKind.Invalid, "Не выбрана кафедра", $"{SignatoryRoles.Title(role)} — с кафедрой."),
            SignatoryRoles.InstituteDirector when data.InstituteId is null || data.DepartmentId is not null =>
                new(DirectoryProblemKind.Invalid, "Не выбран институт", "Директор института — с институтом и без кафедры."),
            _ when SignatoryRoles.WithoutUnit.Contains(role) && (data.DepartmentId is not null || data.InstituteId is not null) =>
                new(DirectoryProblemKind.Invalid, "Лишнее подразделение",
                    $"{SignatoryRoles.Title(role)} — подписант уровня университета, без кафедры и института."),
            _ => null
        };
    }

    // Официальное название и место подписанта — свободны среди других активных записей.
    private async Task<DirectoryProblem?> ConflictAsync(
        Guid id, string? designation, string? role, Guid? departmentId, Guid? instituteId, CancellationToken ct)
    {
        if (designation is not null &&
            await db.Employees.Where(e => e.Id != id && e.IsActive && e.OfficialDesignation == designation)
                .Select(e => e.ShortName).FirstOrDefaultAsync(ct) is { } holder)
            return new(DirectoryProblemKind.Conflict, "Официальное название занято",
                $"«{designation}» уже у активной записи «{holder}». Чтобы передать должность, используйте «Сменить сотрудника на должности».");
        if (SeatOf(role, departmentId, instituteId) is not null)
        {
            var other = await db.Employees.Where(e => e.Id != id && e.IsActive && e.SignatoryRole == role &&
                    (role == SignatoryRoles.InstituteDirector ? e.InstituteId == instituteId : e.DepartmentId == departmentId))
                .Select(e => e.ShortName).FirstOrDefaultAsync(ct);
            if (other is not null)
                return new(DirectoryProblemKind.Conflict, "Место подписанта занято",
                    $"{SignatoryRoles.Title(role!)} этого подразделения — «{other}». Одно место подписанта на кафедру и на институт.");
        }
        return null;
    }

    private static void Fill(Employee employee, EmployeeData data)
    {
        employee.ShortName = data.ShortName.Trim();
        // Подпись в документах по умолчанию — из «Фамилия И. О.»; администратор может её поправить.
        employee.NameForDocument = Trim(data.NameForDocument) ?? PersonNames.NameForDocument(employee.ShortName) ?? employee.ShortName;
        employee.FullName = Trim(data.FullName);
        if (employee.IdentitySubject is null) employee.Email = Trim(data.Email);
        employee.Position = Trim(data.Position);
        employee.AcademicDegree = Trim(data.AcademicDegree);
        employee.OfficialDesignation = Trim(data.OfficialDesignation);
        employee.SignatoryRole = data.SignatoryRole;
        (employee.DepartmentId, employee.InstituteId) = data.SignatoryRole is { } r && SignatoryRoles.WithoutUnit.Contains(r)
            ? (null, null)
            : (data.DepartmentId, data.SignatoryRole == SignatoryRoles.InstituteDirector ? data.InstituteId : data.DepartmentId is null ? data.InstituteId : null);
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DirectoryProblem NotFound() =>
        new(DirectoryProblemKind.NotFound, "Запись справочника не найдена", "Возможно, она изменена в другой вкладке — обновите страницу.");

    private static object Snapshot(Employee e) => new
    {
        e.ShortName, e.NameForDocument, e.FullName, e.Email, e.Position, e.AcademicDegree, e.OfficialDesignation, e.SignatoryRole,
        e.DepartmentId, e.InstituteId, e.IsActive
    };

    private void Audit(string type, Guid employeeId, Guid by, object? before, object after) =>
        db.AuditLog.Add(new AuditLogEntry
        {
            UserId = by, EventType = type, EntityType = AuditEntities.Employee, EntityId = employeeId, OccurredAt = clock.GetUtcNow(),
            Payload = JsonSerializer.Serialize(new { before, after }, Json)
        });
}
