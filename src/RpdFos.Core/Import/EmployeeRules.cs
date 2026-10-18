using System.Text.RegularExpressions;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.Import;

/// <summary>Категория подписанта для записей справочника сотрудников из файла должностей.</summary>
internal static partial class EmployeeRules
{
    // Ключевые слова должностей — по границам слов: «директор» не должен совпадать с «ректор».
    [GeneratedRegex(@"\bзаведующ")] private static partial Regex Head();
    [GeneratedRegex(@"\bи\.\s?о\.|\bисполняющ|\bврио\b")] private static partial Regex Acting();
    [GeneratedRegex(@"\bпроректор")] private static partial Regex ViceRector();
    [GeneratedRegex(@"\bректор\b")] private static partial Regex Rector();
    [GeneratedRegex(@"\bбиблиотек")] private static partial Regex Library();
    [GeneratedRegex(@"\bуму\b|\bучебно-методическ")] private static partial Regex Umu();
    [GeneratedRegex(@"\bуитп\b|\bинформационно-техническ")] private static partial Regex ItDept();
    [GeneratedRegex(@"\bдиректор\b")] private static partial Regex Director();

    /// <summary>
    /// Категория подписанта по должности и официальному названию строки. В файле должностей кодов
    /// категорий нет, поэтому — по ключевым словам; нет совпадения — null (не подписант).
    /// </summary>
    public static string? SignatoryRoleOf(string? position, string officialName, bool isInstituteRow)
    {
        var text = $"{position} {officialName}".ToLowerInvariant();
        if (Head().IsMatch(text))
            return Acting().IsMatch(text) ? SignatoryRoles.DepartmentHeadActing : SignatoryRoles.DepartmentHead;
        if (ViceRector().IsMatch(text)) return SignatoryRoles.ViceRector;
        if (Rector().IsMatch(text)) return SignatoryRoles.Rector;
        if (Library().IsMatch(text)) return SignatoryRoles.LibraryDirector;
        if (Umu().IsMatch(text)) return SignatoryRoles.UmuHead;
        if (ItDept().IsMatch(text)) return SignatoryRoles.ItDeptHead;
        if (Director().IsMatch(text) && isInstituteRow) return SignatoryRoles.InstituteDirector;
        return null;
    }
}
