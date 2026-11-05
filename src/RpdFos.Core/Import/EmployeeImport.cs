using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Directory;
using Parsed = DisciplineDataExtractor;

namespace RpdFos.Core.Import;

/// <summary>Итог загрузки сотрудников: добавлено, обновлено, снято с должности при смене человека.</summary>
public sealed record EmployeeImportCounts(int Added, int Updated, int Replaced);

/// <summary>
/// Загрузка файла должностей в справочник сотрудников по правилам этапа 2 — общая для полного импорта и загрузки
/// файла на странице «Справочник сотрудников» (этап 5.2): институты из файла (сокращение — из строки директора),
/// кафедрам — их институт, записи — добавить и обновить по официальному названию, смена человека на должности —
/// <see cref="EmployeeDirectory.Vacate"/>; записи, которых нет в файле, не трогаются. Кафедры — только известные:
/// из справочника кафедр плана (<see cref="DepartmentsByName"/>).
/// </summary>
public sealed class EmployeeImport(AppDbContext db, DateTimeOffset now, Action<string> warning)
{
    public Dictionary<string, Institute> Institutes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Department> DepartmentsByName { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Кафедры справочника — для загрузки файла без учебного плана.</summary>
    public async Task LoadDepartmentsAsync(CancellationToken ct)
    {
        foreach (var d in await db.Departments.ToListAsync(ct)) DepartmentsByName[d.Name] = d;
    }

    /// <summary>
    /// Институты: из титула плана (если есть) и файла должностей. Сокращение («ИИТ») — из строки директора:
    /// её официальное название и есть институт, а «для документа» — сокращение.
    /// </summary>
    public async Task UpsertInstitutesAsync(string? planInstitute, IReadOnlyList<Parsed.Employee> employees, CancellationToken ct)
    {
        var abbreviations = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        void Note(string? name, string? abbreviation)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            abbreviations[name] = abbreviation ?? abbreviations.GetValueOrDefault(name);
        }
        Note(planInstitute, null);
        foreach (var e in employees)
            Note(e.Institute, e.Department is null && e.OfficialName == e.Institute ? e.DocumentTitle : null);
        if (abbreviations.Count == 0) return;

        var names = abbreviations.Keys.ToList();
        var existing = await db.Institutes.Where(i => names.Contains(i.Name)).ToListAsync(ct);
        var usedCodes = (await db.Institutes.Select(i => i.Code).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, abbreviation) in abbreviations)
        {
            var institute = existing.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
            if (institute is null)
            {
                var code = abbreviation is not null && usedCodes.Add(abbreviation) ? abbreviation : name;
                if (code == name) warning($"Институт «{name}»: сокращение неизвестно — код временно равен названию");
                institute = new Institute { Code = code, Name = name, ShortName = abbreviation };
                db.Institutes.Add(institute);
            }
            else if (abbreviation is not null)
            {
                institute.ShortName = abbreviation;
                // Код, равный названию, — временный (сокращения не было); настоящий код не трогаем.
                if (institute.Code == institute.Name && usedCodes.Add(abbreviation)) institute.Code = abbreviation;
            }
            Institutes[name] = institute;
        }
    }

    /// <summary>Институт кафедры — из файла должностей (в плане его нет).</summary>
    public void LinkInstitute(Department department, IReadOnlyList<Parsed.Employee> employees)
    {
        var instituteName = employees.FirstOrDefault(e => e.Institute is not null &&
            string.Equals(e.Department, department.Name, StringComparison.OrdinalIgnoreCase))?.Institute;
        if (instituteName is not null && Institutes.TryGetValue(instituteName, out var institute))
            department.InstituteId = institute.Id;
    }

    public async Task<EmployeeImportCounts> UpsertEmployeesAsync(IReadOnlyList<Parsed.Employee> parsed, CancellationToken ct)
    {
        if (parsed.Count == 0) return new(0, 0, 0);
        var designations = parsed.Select(e => e.OfficialName).ToList();
        // Ключ — официальное название (должность) среди активных записей.
        var existing = await db.Employees
            .Where(e => e.IsActive && e.OfficialDesignation != null && designations.Contains(e.OfficialDesignation))
            .ToDictionaryAsync(e => e.OfficialDesignation!, ct);

        var replaced = 0;
        foreach (var e in parsed)
        {
            if (!existing.TryGetValue(e.OfficialName, out var previous) || previous.ShortName == e.FullName) continue;
            EmployeeDirectory.Vacate(previous, now);
            existing.Remove(e.OfficialName);
            replaced++;
            warning($"«{e.OfficialName}»: на должности новый сотрудник — «{previous.ShortName}» снят с должности"
                + (previous.IsActive ? " (учётная запись сохранена)" : ", запись неактивна"));
        }
        // Уникальные индексы должности и места подписанта: прежнюю запись освободить до вставки новой.
        if (replaced > 0) await db.SaveChangesAsync(ct);

        // Занятые места подписантов: заведующий — один на кафедру, директор — один на институт.
        var taken = (await db.Employees.Where(e => e.IsActive && e.SignatoryRole != null)
                .Select(e => new { e.OfficialDesignation, e.SignatoryRole, e.DepartmentId, e.InstituteId }).ToListAsync(ct))
            .Where(x => x.OfficialDesignation is null || !designations.Contains(x.OfficialDesignation))
            .Select(x => EmployeeDirectory.SeatOf(x.SignatoryRole, x.DepartmentId, x.InstituteId))
            .OfType<string>().ToHashSet();

        var (added, updated) = (0, 0);
        foreach (var e in parsed)
        {
            Department? department = null;
            if (e.Department is not null && !DepartmentsByName.TryGetValue(e.Department, out department))
                warning($"«{e.OfficialName}»: кафедры нет в справочнике кафедр плана — сотрудник загружен без кафедры");
            var isInstituteRow = e.Department is null && e.Institute is not null && e.OfficialName == e.Institute;
            var institute = isInstituteRow ? Institutes.GetValueOrDefault(e.Institute!) : null;

            var role = EmployeeRules.SignatoryRoleOf(e.Position, e.OfficialName, isInstituteRow);
            // Категория подписанта должна быть согласована с оргструктурой (ограничение ck_users_signatory_scope).
            if ((role is SignatoryRoles.DepartmentHead or SignatoryRoles.DepartmentHeadActing && department is null) ||
                (role is SignatoryRoles.InstituteDirector && institute is null))
            {
                warning($"«{e.OfficialName}»: подразделение не найдено — категория подписанта не назначена");
                role = null;
            }
            var departmentId = department?.Id;
            var instituteId = department is null ? institute?.Id : null;
            if (role is not null && SignatoryRoles.WithoutUnit.Contains(role)) (departmentId, instituteId) = (null, null);
            if (EmployeeDirectory.SeatOf(role, departmentId, instituteId) is { } seat && !taken.Add(seat))
            {
                warning($"«{e.OfficialName}»: место подписанта уже занято другим сотрудником — категория не назначена");
                role = null;
            }

            var nameForDocument = PersonNames.NameForDocument(e.FullName);
            if (nameForDocument is null)
                warning($"«{e.OfficialName}»: ФИО «{e.FullName}» не в формате «Фамилия И. О.» — подпись не переставлена");

            if (!existing.TryGetValue(e.OfficialName, out var employee))
            {
                // Справочная запись: входа нет, пока Администратор не свяжет её с учётной записью.
                employee = new Employee { OfficialDesignation = e.OfficialName, CanLogin = false, CreatedAt = now };
                db.Employees.Add(employee);
                existing[e.OfficialName] = employee;
                added++;
            }
            else updated++;
            employee.ShortName = e.FullName;
            employee.NameForDocument = nameForDocument ?? e.FullName;
            employee.Position = e.Position;
            employee.SignatoryRole = role;
            employee.DepartmentId = departmentId;
            employee.InstituteId = instituteId;
            employee.UpdatedAt = now;
        }
        return new(added, updated, replaced);
    }
}
