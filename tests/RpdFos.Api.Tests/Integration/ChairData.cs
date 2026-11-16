using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Api.Tests.Integration;

/// <summary>Направление подготовки, созданное для проверки: код, дисциплина его плана.</summary>
public sealed record ChairDirection(Guid Id, string Code, Guid DisciplineId);

/// <summary>
/// Данные для проверок председателя УМК по направлению (этап 6.1, C6). У каждого вызова — своё направление: в общей
/// базе тестов председателя реального направления плана 09.04.04 нет, и другие тесты на это опираются (документ
/// остаётся «Принят» из-за ненайденного подписанта), поэтому записи председателей создаются только для своих направлений.
/// </summary>
public static class ChairData
{
    /// <summary>Своё направление с учебным планом и дисциплиной без кафедры.</summary>
    public static async Task<ChairDirection> AddDirectionAsync(AppDbContext db)
    {
        var plan = await TemplateData.AddPlanAsync(db);
        var discipline = new Discipline { CurriculumPlanId = plan.Id, Code = "Б1.О.01", Name = "Дисциплина проверки председателя УМК" };
        db.Disciplines.Add(discipline);
        await db.SaveChangesAsync();
        var code = await db.StudyDirections.Where(d => d.Id == plan.DirectionId).Select(d => d.Code).SingleAsync();
        return new ChairDirection(plan.DirectionId, code, discipline.Id);
    }

    /// <summary>Запись справочника без входа — подписанта; ФИО обезличены, с уникальным суффиксом для поиска в тексте документа.</summary>
    public static Employee NewEmployee(string? role, Guid? directionId = null, Guid? departmentId = null, bool active = true)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var now = DateTimeOffset.UtcNow;
        return new Employee
        {
            ShortName = $"Умков{suffix} У. М.", NameForDocument = $"У. М. Умков{suffix}", Position = "Председатель УМК",
            SignatoryRole = role, DirectionId = directionId, DepartmentId = departmentId, IsActive = active, CanLogin = false,
            CreatedAt = now, UpdatedAt = now
        };
    }

    /// <summary>Председатель УМК направления; без направления — запись, какую допускает база, но не принимает страница справочника.</summary>
    public static async Task<Employee> AddChairAsync(AppDbContext db, Guid? directionId, bool active = true)
    {
        var chair = NewEmployee(SignatoryRoles.UmkChair, directionId, active: active);
        db.Employees.Add(chair);
        await db.SaveChangesAsync();
        return chair;
    }
}
