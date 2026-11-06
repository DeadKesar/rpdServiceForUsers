using System.Text.RegularExpressions;

namespace RpdFos.Corpus;

/// <summary>
/// Комплект «план — список компетенций — матрица» из <c>_комплекты.csv</c>. Матрица не выбрана — кандидаты из
/// «Замечания МК» (через « / »): прогон берёт ту, у которой меньше несопоставленных строк.
/// </summary>
public sealed record CorpusSet(
    string Plan, string Code, string Profile, string Form, int? Year, string CompetenceList, string? Matrix,
    IReadOnlyList<string> MatrixCandidates, string MatrixNote)
{
    /// <summary>Короткое имя для отчётов: имя файла плана без расширений.</summary>
    public string Name => Path.GetFileName(Plan).Replace(".plx.xlsx", "").Replace(".xlsx", "").Trim();

    /// <summary>Матрица другого (более раннего) года — расхождения с планом ожидаемы.</summary>
    public bool OlderMatrix => MatrixNote.Contains("2021") || (Matrix ?? "").Contains("2021-2022");

    public static IReadOnlyList<CorpusSet> Read(string corpus)
    {
        var lines = File.ReadAllLines(Path.Combine(corpus, "_комплекты.csv"));
        var result = new List<CorpusSet>();
        foreach (var line in lines.Skip(1).Where(l => l.Trim().Length > 0))
        {
            var c = line.Split(';');
            string Col(int i) => i < c.Length ? c[i].Trim() : "";
            var note = Col(8);
            var candidates = new List<string>();
            var choose = Regex.Match(note, @"выбрать:\s*(.+)$");
            if (choose.Success)
                candidates.AddRange(choose.Groups[1].Value.Split(" / ").Select(x => x.Trim()).Where(x => x.Length > 0));
            result.Add(new CorpusSet(Col(0), Col(1), Col(2), Col(3), int.TryParse(Col(4), out var y) ? y : null, Col(5),
                Col(7).Length > 0 ? Col(7) : null, candidates, note));
        }
        return result;
    }

    /// <summary>Путь к матрице-кандидату по имени файла: в МК или в _преобразовано/МК.</summary>
    public static string? FindMatrix(string corpus, string name)
    {
        foreach (var folder in new[] { "МК", Path.Combine("_преобразовано", "МК") })
        {
            var path = Path.Combine(corpus, folder, name);
            if (File.Exists(path)) return Path.Combine(folder, name);
        }
        return null;
    }
}
