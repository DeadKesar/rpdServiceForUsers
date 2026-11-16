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

        // Все места, кроме председателя УМК: такой категории в файле должностей нет, а председателя направления плана
        // 09.04.04 в справочнике тестов никто не назначает.
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
        Assert.Equal([ChairMissing("discipline.direction.umk_chair")], report.Warnings);
    }

    [Fact]
    public async Task Umk_chair_is_found_by_the_direction_of_the_disciplines_plan()
    {
        await app.WithDbAsync(async db =>
        {
            var first = await ChairData.AddDirectionAsync(db);
            var second = await ChairData.AddDirectionAsync(db);
            var withoutChair = await ChairData.AddDirectionAsync(db);
            var firstChair = await ChairData.AddChairAsync(db, first.Id);
            var secondChair = await ChairData.AddChairAsync(db, second.Id);
            var resolution = new SignatoryResolution(db);

            // Свой председатель у каждого направления; подпись — из его записи справочника.
            var firstSeat = (await resolution.ResolveAsync(RpdSeats, first.DisciplineId)).Seat(SignatoryRoles.UmkChair)!;
            var secondSeat = (await resolution.ResolveAsync(RpdSeats, second.DisciplineId)).Seat(SignatoryRoles.UmkChair)!;
            Assert.Equal((firstChair.Id, firstChair.NameForDocument, firstChair.Position), (firstSeat.EmployeeId, firstSeat.Name, firstSeat.Position));
            Assert.Equal((secondChair.Id, secondChair.NameForDocument), (secondSeat.EmployeeId, secondSeat.Name));

            // У направления без председателя место пустое с предупреждением — председатель соседних направлений его не заменяет.
            var missing = await resolution.ResolveAsync(RpdSeats, withoutChair.DisciplineId);
            var seat = missing.Seat(SignatoryRoles.UmkChair)!;
            Assert.Equal((false, null, null, null), (seat.Found, seat.Name, seat.Position, seat.Department));
            Assert.Contains(ChairMissing("discipline.direction.umk_chair"), missing.Warnings);
        });
    }

    [Fact]
    public async Task Old_rule_name_resolves_the_chair_the_same_way()
    {
        // Лист согласования новой редакции шаблона копируется из прежней, и в действующих шаблонах осталось прежнее
        // имя правила: оно ищет по направлению, а не по категории.
        var legacy = RpdSeats.Select(s => s.Id == SignatoryRoles.UmkChair ? s with { Resolver = UmkChairResolver.LegacyRule } : s).ToList();
        Assert.Equal("lookup.role:umk_chair", UmkChairResolver.LegacyRule);

        await app.WithDbAsync(async db =>
        {
            var served = await ChairData.AddDirectionAsync(db);
            var other = await ChairData.AddDirectionAsync(db);
            var chair = await ChairData.AddChairAsync(db, served.Id);
            await ChairData.AddChairAsync(db, null); // запись без направления не находится ни под одним из имён правила
            var resolution = new SignatoryResolution(db);

            var current = (await resolution.ResolveAsync(RpdSeats, served.DisciplineId)).Seat(SignatoryRoles.UmkChair)!;
            var old = (await resolution.ResolveAsync(legacy, served.DisciplineId)).Seat(SignatoryRoles.UmkChair)!;
            Assert.Equal((chair.Id, chair.NameForDocument), (current.EmployeeId, current.Name));
            Assert.Equal(current, old);

            var missing = await resolution.ResolveAsync(legacy, other.DisciplineId);
            Assert.False(missing.Seat(SignatoryRoles.UmkChair)!.Found);
            Assert.Contains(ChairMissing(UmkChairResolver.LegacyRule), missing.Warnings);
        });
    }

    [Fact]
    public async Task Only_the_active_chair_of_the_direction_signs()
    {
        await app.WithDbAsync(async db =>
        {
            var direction = await ChairData.AddDirectionAsync(db);
            await ChairData.AddChairAsync(db, direction.Id, active: false);
            var resolution = new SignatoryResolution(db);

            // Прежний председатель снят с должности: место пустое, пока не назначен действующий.
            Assert.False((await resolution.ResolveAsync(RpdSeats, direction.DisciplineId)).Seat(SignatoryRoles.UmkChair)!.Found);

            var current = await ChairData.AddChairAsync(db, direction.Id);
            Assert.Equal(current.Id, (await resolution.ResolveAsync(RpdSeats, direction.DisciplineId)).Seat(SignatoryRoles.UmkChair)!.EmployeeId);
        });
    }

    [Fact]
    public void Templates_of_both_types_name_the_chair_rule_by_direction()
    {
        var registry = new DocumentTypeRegistry();

        Assert.All(new[] { DocumentTypeRegistry.Rpd, DocumentTypeRegistry.Fos }, type =>
        {
            var seat = registry.Get(type).Signatures.Single(s => s.Id == SignatoryRoles.UmkChair);
            // Место остаётся бумажным: меняется только то, кто подписывает.
            Assert.Equal((UmkChairResolver.Rule, false, SignaturePlacements.ApprovalSheet), (seat.Resolver, seat.Electronic, seat.Placement));
        });
        // Оба имени правила — в каталоге: с названием для редактора шаблона и для проверки листа согласования.
        Assert.Contains(UmkChairResolver.Rule, SignatureResolvers.All.Keys);
        Assert.Contains(UmkChairResolver.LegacyRule, SignatureResolvers.All.Keys);
    }

    private static string ChairMissing(string rule) =>
        $"Не найден подписант «Председатель учебно-методической комиссии по направлению подготовки» (правило {rule}) — место напечатано пустой строкой.";

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
