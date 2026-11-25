using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.Directory;

/// <summary>
/// Страница «Кафедры» администратора (этап 8, B2; FR-ADM-06, S-51): правка названия и краткого названия. Код кафедры и
/// институт — из плана и файла должностей, здесь только читаются. Название и краткое название уникальны (как в таблице);
/// правка ставит <see cref="Department.IsEditedManually"/> — импорт плана такую кафедру не переписывает — и пишет в журнал
/// аудита, что было и что стало. Сохраняет вызывающий.
/// </summary>
public sealed class DepartmentDirectory(AppDbContext db, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // Пределы колонок departments.name и departments.short_name.
    public const int MaxName = 255, MaxShortName = 50;

    public async Task<(Department? Department, DirectoryProblem? Problem)> UpdateAsync(
        Guid id, string? name, string? shortName, Guid by, CancellationToken ct)
    {
        var department = await db.Departments.SingleOrDefaultAsync(d => d.Id == id, ct);
        if (department is null)
            return (null, new(DirectoryProblemKind.NotFound, "Кафедра не найдена", "Возможно, справочник изменён — обновите страницу."));

        name = Collapse(name);
        shortName = Collapse(shortName);
        if (name.Length == 0 || shortName.Length == 0)
            return (null, new(DirectoryProblemKind.Invalid, "Название не заполнено", "Укажите название и краткое название кафедры."));
        if (name.Length > MaxName || shortName.Length > MaxShortName)
            return (null, new(DirectoryProblemKind.Invalid, "Название слишком длинное",
                $"Название — не больше {MaxName} знаков, краткое название — не больше {MaxShortName}."));

        // Без изменений правкой не считается: признак и запись аудита ставит только настоящее исправление.
        if (name == department.Name && shortName == department.ShortName) return (department, null);

        if (await db.Departments.AnyAsync(d => d.Id != id && d.Name == name, ct))
            return (null, new(DirectoryProblemKind.Conflict, "Название занято", "Другая кафедра уже называется так."));
        if (await db.Departments.AnyAsync(d => d.Id != id && d.ShortName == shortName, ct))
            return (null, new(DirectoryProblemKind.Conflict, "Краткое название занято", "У другой кафедры такое же краткое название."));

        var before = new { name = department.Name, short_name = department.ShortName };
        department.Name = name;
        department.ShortName = shortName;
        department.IsEditedManually = true;
        db.AuditLog.Add(new AuditLogEntry
        {
            UserId = by, EventType = AuditEvents.DepartmentUpdated, EntityType = AuditEntities.Department, EntityId = id,
            OccurredAt = clock.GetUtcNow(),
            Payload = JsonSerializer.Serialize(new { before, after = new { name, short_name = shortName } }, Json)
        });
        return (department, null);
    }

    // Лишние пробелы внутри и по краям — опечатка набора, а не часть названия.
    private static string Collapse(string? value) =>
        string.Join(' ', (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
