using System.Text.RegularExpressions;

namespace RpdFos.Core.Directory;

/// <summary>Формы имени сотрудника: «Фамилия И. О.» (short_name) и «И. О. Фамилия» (подпись в документах).</summary>
internal static partial class PersonNames
{
    [GeneratedRegex(@"^(?<surname>[А-ЯЁA-Z][^\s.]*)\s+(?<initials>(?:[А-ЯЁA-Z]\.\s*){1,3})$")]
    private static partial Regex SurnameWithInitials();

    [GeneratedRegex(@"[А-ЯЁA-Z]\.")]
    private static partial Regex Initial();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>
    /// «Фамилия И. О.» из ФИО полностью («Иванов Александр Борисович» → «Иванов А. Б.»).
    /// Уже сокращённое ФИО и одно слово («Администратор») — как есть.
    /// </summary>
    public static string ShortNameOf(string fullName)
    {
        var name = Spaces().Replace(fullName.Trim(), " ");
        if (SurnameWithInitials().IsMatch(name)) return name;
        var parts = name.Split(' ');
        if (parts.Length < 2) return name;
        return parts[0] + " " + string.Join(' ', parts.Skip(1).Take(2).Select(p => char.ToUpperInvariant(p[0]) + "."));
    }

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
}
