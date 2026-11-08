namespace RpdFos.Core.Import;

/// <summary>
/// Нестрогое сравнение названий кафедр между учебными планами (этап 5.4, B6). В планах разных лет одна кафедра
/// пишется по-разному: окончания («…информации и управления» / «…и управление»), опечатки («Управвление»,
/// «Гидропневмоавтомактика»), слитные слова («регулированиепромышленного»), «Иностранный язык» / «Иностранные языки».
/// Сравниваются только буквы и цифры без регистра и «ё»; допускается расстояние Левенштейна до 2 или до десятой
/// части длины более короткого названия. Разные кафедры с похожими названиями («Инженерная безопасность» и
/// «Информационная безопасность») так не совпадают.
/// </summary>
public static class DepartmentNames
{
    public static string Key(string name) =>
        new(name.ToLowerInvariant().Replace('ё', 'е').Where(char.IsLetterOrDigit).ToArray());

    public static bool Same(string a, string b)
    {
        var (x, y) = (Key(a), Key(b));
        if (x == y) return true;
        var allowed = Math.Max(2, Math.Min(x.Length, y.Length) / 10);
        return Math.Abs(x.Length - y.Length) <= allowed && Distance(x, y, allowed) <= allowed;
    }

    // Левенштейн с отсечкой: дальше порога считать незачем.
    private static int Distance(string a, string b, int limit)
    {
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        var current = new int[b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowMin = current[0];
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                rowMin = Math.Min(rowMin, current[j]);
            }
            if (rowMin > limit) return rowMin;
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}
