using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.Directory;

/// <summary>Почему учётную запись не удалось связать со справочником сотрудников.</summary>
public enum DirectoryLinkProblem
{
    /// <summary>Записи справочника с таким id нет.</summary>
    NotFound,

    /// <summary>Запись справочника неактивна (человек сменился на должности).</summary>
    Inactive,

    /// <summary>Запись справочника уже связана с другой учётной записью.</summary>
    AlreadyLinked,

    /// <summary>Email учётной записи уже у другой записи справочника.</summary>
    EmailTaken
}

/// <summary>
/// Связь учётной записи (Identity) со справочником сотрудников users (правка по ревизии архитектуры):
/// у каждой учётной записи — запись справочника с can_login = true и identity_subject = id учётной записи.
/// На запись справочника ссылаются uploaded_by, started_by и дальше — согласования.
/// </summary>
public sealed class DirectoryAccounts(AppDbContext db, TimeProvider clock)
{
    /// <summary>Утверждение токена с идентификатором учётной записи.</summary>
    public const string SubjectClaim = "sub";

    /// <summary>
    /// Создаёт запись справочника для учётной записи или, если задан <paramref name="directoryUserId"/>,
    /// привязывает учётную запись к существующей записи (например, загруженной из файла должностей).
    /// Повторный вызов для уже связанной учётной записи возвращает её запись. Изменения сохраняются.
    /// </summary>
    public async Task<(Employee? Employee, DirectoryLinkProblem? Problem)> LinkAsync(
        AppUser account, Guid? directoryUserId = null, CancellationToken ct = default)
    {
        var subject = account.Id.ToString();
        var linked = await db.Employees.SingleOrDefaultAsync(e => e.IdentitySubject == subject, ct);
        if (linked is not null) return (linked, null);

        var email = account.Email;
        if (email is not null && await db.Employees.AnyAsync(e => e.Email == email && e.Id != directoryUserId, ct))
            return (null, DirectoryLinkProblem.EmailTaken);

        var now = clock.GetUtcNow();
        Employee employee;
        if (directoryUserId is Guid id)
        {
            var existing = await db.Employees.SingleOrDefaultAsync(e => e.Id == id, ct);
            if (existing is null) return (null, DirectoryLinkProblem.NotFound);
            if (!existing.IsActive) return (null, DirectoryLinkProblem.Inactive);
            if (existing.IdentitySubject is not null) return (null, DirectoryLinkProblem.AlreadyLinked);
            employee = existing;
            // ФИО полностью в файле должностей нет — берётся из учётной записи; подпись остаётся из справочника.
            employee.FullName ??= NullIfEmpty(account.FullName);
        }
        else
        {
            var shortName = PersonNames.ShortNameOf(account.FullName.Length > 0 ? account.FullName : email ?? subject);
            employee = new Employee
            {
                FullName = NullIfEmpty(account.FullName),
                ShortName = shortName,
                NameForDocument = PersonNames.NameForDocument(shortName) ?? shortName,
                CreatedAt = now
            };
            db.Employees.Add(employee);
        }

        employee.Email = email;
        employee.IdentitySubject = subject;
        employee.CanLogin = true;
        employee.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return (employee, null);
    }

    /// <summary>
    /// Создаёт записи справочника для учётных записей, у которых их нет, — созданных до появления справочника
    /// (этап 1). Возвращает число связанных и учётные записи, которые связать не удалось.
    /// </summary>
    public async Task<(int Linked, IReadOnlyList<(string? Email, DirectoryLinkProblem Problem)> Problems)> LinkUnlinkedAccountsAsync(
        CancellationToken ct = default)
    {
        var subjects = db.Employees.Where(e => e.IdentitySubject != null).Select(e => e.IdentitySubject);
        var accounts = await db.Users.Where(u => !subjects.Contains(u.Id.ToString())).ToListAsync(ct);

        var linked = 0;
        var problems = new List<(string?, DirectoryLinkProblem)>();
        foreach (var account in accounts)
        {
            var (_, problem) = await LinkAsync(account, ct: ct);
            if (problem is null) linked++;
            else problems.Add((account.Email, problem.Value));
        }
        return (linked, problems);
    }

    /// <summary>Id активной записи справочника текущего пользователя по утверждению sub; null — не связан.</summary>
    public async Task<Guid?> FindIdAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        var subject = user.FindFirst(SubjectClaim)?.Value;
        if (string.IsNullOrEmpty(subject)) return null;
        return await db.Employees
            .Where(e => e.IdentitySubject == subject && e.IsActive)
            .Select(e => (Guid?)e.Id)
            .FirstOrDefaultAsync(ct);
    }

    private static string? NullIfEmpty(string text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
