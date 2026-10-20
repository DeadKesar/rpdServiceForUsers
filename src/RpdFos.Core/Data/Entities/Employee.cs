namespace RpdFos.Core.Data.Entities;

/// <summary>
/// Справочник сотрудников (таблица users, раздел 2.4 ВКР): и пользователи системы, и справочные
/// записи без входа — подписанты. Это не учётная запись: вход — через <see cref="AppUser"/>,
/// связь с ней — <see cref="IdentitySubject"/> (утверждение sub токена).
/// </summary>
public class Employee
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? Email { get; set; }

    /// <summary>ФИО полностью — необязательное: в файле должностей только фамилия с инициалами.</summary>
    public string? FullName { get; set; }

    /// <summary>«Фамилия И. О.» — из файла должностей (отступление от проекта, согласовано на этапе 2).</summary>
    public string ShortName { get; set; } = "";

    /// <summary>Подпись в документах — «И. О. Фамилия».</summary>
    public string NameForDocument { get; set; } = "";

    /// <summary>Канонический код для именованных лукапов: «Офиц. название» строки файла должностей.</summary>
    public string? OfficialDesignation { get; set; }

    public string? Position { get; set; }
    public string? AcademicDegree { get; set; }
    public string? SignatoryRole { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? InstituteId { get; set; }
    public bool CanLogin { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public string? IdentitySubject { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Категории подписантов (раздел 2.4 ВКР): по ним резолверы шаблона находят подписанта. Один набор кодов —
/// и в листах согласования шаблонов (<c>role</c>, <c>lookup.role:&lt;код&gt;</c>), и в users.signatory_role.
/// </summary>
public static class SignatoryRoles
{
    public const string DepartmentHead = "department_head", DepartmentHeadActing = "department_head_acting",
        InstituteDirector = "institute_director", Rector = "rector", ViceRectorEdu = "vice_rector_edu",
        UmuHead = "umu_head", LibraryDirector = "library_director", ItDirectorateHead = "it_directorate_head",
        UmkChair = "umk_chair";

    public static readonly string[] All =
    [
        DepartmentHead, DepartmentHeadActing, InstituteDirector, Rector, ViceRectorEdu, UmuHead, LibraryDirector,
        ItDirectorateHead, UmkChair
    ];

    /// <summary>
    /// Подписанты без кафедры и без института: уровня университета и председатель УМК — его связь
    /// с направлением подготовки появится на этапе 6.
    /// </summary>
    public static readonly string[] WithoutUnit = [Rector, ViceRectorEdu, UmuHead, LibraryDirector, ItDirectorateHead, UmkChair];
}
