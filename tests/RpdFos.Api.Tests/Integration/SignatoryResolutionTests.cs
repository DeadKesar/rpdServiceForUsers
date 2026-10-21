using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;
using RpdFos.Core.DocumentTypes;
using Xunit;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// Подписанты по листу согласования шаблона (этап 5, B4) — на обезличенном справочнике должностей из фикстур
/// («СотрудникNN И. О.»), импортированном вместе с реальным планом 09.04.04.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class SignatoryResolutionTests(DatabaseFixture app)
{
    private static readonly IReadOnlyList<SignatureSeat> RpdSeats = new DocumentTypeRegistry().Get(DocumentTypeRegistry.Rpd).Signatures;

    [Fact]
    public async Task Rpd_signatories_of_real_discipline()
    {
        var disciplineId = await DocumentData.DisciplineAsync(app, "Б1.О.10");
        SignatoryReport? report = null;
        Department? department = null;
        await WithResolutionAsync(async (db, resolution) =>
        {
            report = await resolution.ResolveAsync(RpdSeats, disciplineId);
            var departmentId = await db.Disciplines.Where(d => d.Id == disciplineId).Select(d => d.DepartmentId).SingleAsync();
            department = await db.Departments.SingleAsync(d => d.Id == departmentId);
        });

        // Все места, кроме председателя УМК: такой категории в файле должностей нет.
        Assert.Equal(RpdSeats.Select(s => s.Id), report!.Seats.Select(s => s.SeatId));
        Assert.All(report.Seats.Where(s => s.SeatId != SignatoryRoles.UmkChair), s =>
        {
            Assert.True(s.Found, s.SeatId);
            Assert.Contains("Сотрудник", s.Name);
            Assert.False(string.IsNullOrWhiteSpace(s.Position), s.SeatId);
        });

        // Заведующий — кафедры дисциплины, с её сокращением для подписи «наименование кафедры».
        Assert.Equal(department!.ShortName, report.Seat(SignatoryRoles.DepartmentHead)!.Department);

        var chair = report.Seat(SignatoryRoles.UmkChair)!;
        Assert.Equal((false, null, null), (chair.Found, chair.Name, chair.Position));
        Assert.Equal(
            ["Не найден подписант «Председатель учебно-методической комиссии по направлению подготовки» (правило lookup.role:umk_chair) — место напечатано пустой строкой."],
            report.Warnings);
    }

    [Fact]
    public async Task Acting_head_stands_in_and_inactive_records_are_skipped()
    {
        var stem = "Кафедра" + Guid.NewGuid().ToString("N")[..8];
        var department = new Department { Code = stem, Name = stem, ShortName = stem };
        var now = DateTimeOffset.UtcNow;
        Employee Head(string name, string role, bool active) => new()
        {
            ShortName = name, NameForDocument = name, Position = "Заведующий кафедрой", SignatoryRole = role, DepartmentId = department.Id,
            IsActive = active, CanLogin = false, CreatedAt = now, UpdatedAt = now
        };
        var acting = Head("Исполняющий И. О.", SignatoryRoles.DepartmentHeadActing, active: true);

        await app.WithDbAsync(async db =>
        {
            db.Departments.Add(department);
            // Неактивный заведующий (снят с должности) не подписывает — подписывает исполняющий обязанности.
            db.Employees.AddRange(Head("Прежний П. П.", SignatoryRoles.DepartmentHead, active: false), acting);
            await db.SaveChangesAsync();

            var context = new SignatoryContext(db, department);
            var chain = new DepartmentHeadResolver();
            Assert.Equal(acting.Id, (await chain.ResolveAsync(DepartmentHeadResolver.Rule, context, default))!.Id);

            // Правило не этого звена — дальше по цепочке; конец цепочки — не найден.
            Assert.Null(await chain.ResolveAsync("lookup.role:rector", context, default));
            Assert.Null(await new InstituteDirectorResolver().ResolveAsync(InstituteDirectorResolver.Rule, context, default));
        });
    }

    private async Task WithResolutionAsync(Func<AppDbContext, SignatoryResolution, Task> action)
    {
        await app.RealPlanIdAsync();
        await using var scope = app.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<AppDbContext>(), scope.ServiceProvider.GetRequiredService<SignatoryResolution>());
    }
}
