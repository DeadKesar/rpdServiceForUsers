namespace RpdFos.Core.Data.Entities;

/// <summary>
/// Четыре роли системы (раздел 2.2 ВКР). Ролей у учётной записи может быть несколько: заведующий кафедрой — и
/// преподаватель, и согласующий (этап 6, B1).
/// </summary>
public static class AppRoles
{
    public const string Teacher = "teacher", Methodist = "methodist", Approver = "approver", Admin = "admin";

    public static readonly string[] All = [Teacher, Methodist, Approver, Admin];
}
