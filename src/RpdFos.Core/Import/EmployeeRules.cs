using System.Text.RegularExpressions;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.Import;

/// <summary>Правила для записей справочника сотрудников из файла должностей.</summary>
internal static partial class EmployeeRules
{
    [GeneratedRegex(@"^(?<surname>[А-ЯЁA-Z][^\s.]*)\s+(?<initials>(?:[А-ЯЁA-Z]\.\s*){1,3})$")]
    private static partial Regex SurnameWithInitials();

    [GeneratedRegex(@"[А-ЯЁA-Z]\.")]
    private static partial Regex Initial();

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
    /// Подпись для документов в формате проекта — «И. О. Фамилия» из «Фамилия И. О.».
    /// null — строка не похожа на фамилию с инициалами.
    /// </summary>
    public static string? NameForDocument(string shortName)
    {
        var m = SurnameWithInitials().Match(shortName.Trim());
        if (!m.Success) return null;
        var initials = string.Join(' ', Initial().Matches(m.Groups["initials"].Value).Select(x => x.Value));
        return $"{initials} {m.Groups["surname"].Value}";
    }

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
