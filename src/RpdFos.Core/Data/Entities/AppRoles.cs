namespace RpdFos.Core.Data.Entities;

/// <summary>Четыре роли системы (раздел 2.2 ВКР). В прототипе у пользователя одна роль.</summary>
public static class AppRoles
{
    public const string Teacher = "teacher", Methodist = "methodist", Approver = "approver", Admin = "admin";

    public static readonly string[] All = [Teacher, Methodist, Approver, Admin];
}
