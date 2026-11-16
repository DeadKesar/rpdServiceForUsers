using RpdFos.Api.Tests.Integration;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Directory;
using Xunit;

namespace RpdFos.Api.Tests.Documents;

/// <summary>
/// Правила справочника сотрудников, которые не требуют базы (этап 6.1, C6): оргструктура председателя УМК
/// (направление подготовки вместо подразделения), место подписанта и освобождение места при смене человека.
/// </summary>
public class SignatoryRulesTests
{
    private static readonly Guid Department = Guid.NewGuid(), Institute = Guid.NewGuid(), Direction = Guid.NewGuid();

    private static EmployeeData Data(string? role, Guid? department = null, Guid? institute = null, Guid? direction = null) =>
        new("Умков У. М.", null, null, null, null, null, null, role, department, institute, direction);

    [Fact]
    public void Chair_is_with_a_direction_and_without_department_and_institute()
    {
        Assert.Null(EmployeeDirectory.Validate(Data(SignatoryRoles.UmkChair, direction: Direction)));

        Assert.Equal("Не выбрано направление", EmployeeDirectory.Validate(Data(SignatoryRoles.UmkChair))!.Title);
        Assert.Equal("Лишнее подразделение",
            EmployeeDirectory.Validate(Data(SignatoryRoles.UmkChair, department: Department, direction: Direction))!.Title);
        Assert.Equal("Лишнее подразделение",
            EmployeeDirectory.Validate(Data(SignatoryRoles.UmkChair, institute: Institute, direction: Direction))!.Title);
        // Кафедра без направления — прежде всего не выбрано направление.
        Assert.Equal("Не выбрано направление", EmployeeDirectory.Validate(Data(SignatoryRoles.UmkChair, department: Department))!.Title);
    }

    [Fact]
    public void Other_categories_and_non_signatories_have_no_direction()
    {
        // Каждой категории — её подразделение по оргструктуре, и с ним направление всё равно лишнее.
        foreach (var role in SignatoryRoles.All.Where(r => r != SignatoryRoles.UmkChair))
        {
            var withUnit = role switch
            {
                SignatoryRoles.DepartmentHead or SignatoryRoles.DepartmentHeadActing => Data(role, department: Department, direction: Direction),
                SignatoryRoles.InstituteDirector => Data(role, institute: Institute, direction: Direction),
                _ => Data(role, direction: Direction)
            };

            Assert.Equal("Лишнее направление", EmployeeDirectory.Validate(withUnit)!.Title);
            // Без направления та же запись допустима.
            Assert.Null(EmployeeDirectory.Validate(withUnit with { DirectionId = null }));
        }

        Assert.Equal("Лишнее направление", EmployeeDirectory.Validate(Data(null, direction: Direction))!.Title);
        Assert.Null(EmployeeDirectory.Validate(Data(null)));
    }

    [Fact]
    public void Seat_of_chair_is_the_direction()
    {
        var other = Guid.NewGuid();

        Assert.Equal($"umk_chair:{Direction}", EmployeeDirectory.SeatOf(SignatoryRoles.UmkChair, null, null, Direction));
        Assert.NotEqual(EmployeeDirectory.SeatOf(SignatoryRoles.UmkChair, null, null, Direction),
            EmployeeDirectory.SeatOf(SignatoryRoles.UmkChair, null, null, other));
        // Председатель без направления места не занимает: таких записей может быть несколько.
        Assert.Null(EmployeeDirectory.SeatOf(SignatoryRoles.UmkChair, null, null));
        // Места заведующего и директора — как были.
        Assert.Equal($"department_head:{Department}", EmployeeDirectory.SeatOf(SignatoryRoles.DepartmentHead, Department, null));
        Assert.Equal($"institute_director:{Institute}", EmployeeDirectory.SeatOf(SignatoryRoles.InstituteDirector, null, Institute));
        Assert.Null(EmployeeDirectory.SeatOf(SignatoryRoles.Rector, null, null));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("subject", true)]
    public void Vacated_chair_loses_the_direction_with_the_seat(string? identitySubject, bool staysActive)
    {
        var chair = ChairData.NewEmployee(SignatoryRoles.UmkChair, Direction);
        chair.OfficialDesignation = "Председатель УМК направления";
        chair.IdentitySubject = identitySubject;

        EmployeeDirectory.Vacate(chair, DateTimeOffset.UtcNow);

        // Место (название, категория, направление) снято: без категории направление нарушило бы ck_users_direction_scope.
        Assert.Equal((null, null, null, staysActive), (chair.OfficialDesignation, chair.SignatoryRole, chair.DirectionId, chair.IsActive));
    }
}
