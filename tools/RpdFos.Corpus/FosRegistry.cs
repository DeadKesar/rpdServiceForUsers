using System.Security.Cryptography;
using System.Text;

namespace RpdFos.Corpus;

/// <summary>Закреплённый номер файла ФОС. <paramref name="Present"/> — файл сейчас лежит в папке корпуса.</summary>
public sealed record RegistryEntry(string Id, string Name, string Hash, bool Present);

/// <summary>
/// Реестр номеров F01… файлов ФОС. Зачем: номера по порядку имён сдвигались бы с каждым новым файлом, а журнал и
/// предложения автору ссылаются на них (F34, F35). Реестр лежит в корне корпуса рядом с <c>_комплекты.csv</c> и хранит
/// настоящие имена файлов — он же отвечает на вопрос «какой файл под каким номером»; в отчётах имена скрыты.
/// Номер навсегда за файлом: исчезнувший файл остаётся в реестре, его номер не выдаётся повторно.
/// </summary>
public static class FosRegistry
{
    public const string FileName = "_номера_ФОС.csv";

    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;

    public static string IdOf(int number) => $"F{number:00}";

    public static int NumberOf(string id) => int.Parse(id.AsSpan(1));

    /// <summary>Содержимое файла: по нему находятся переименованные файлы и точные копии. Хватает 16 знаков хеша.</summary>
    public static string HashOf(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream))[..16];
    }

    /// <summary>
    /// Сопоставляет файлы папки с реестром: тот же файл — по имени, переименованный — по содержимому; остальные —
    /// новые, получают следующие номера по порядку имён. Прежние номера не меняются никогда.
    /// </summary>
    public static List<RegistryEntry> Assign(IReadOnlyList<(string Name, string Hash)> current, IReadOnlyList<RegistryEntry> previous)
    {
        var free = current.ToList();
        var result = new List<RegistryEntry>();
        var unmatched = new List<RegistryEntry>();
        foreach (var entry in previous)
        {
            var i = free.FindIndex(c => Names.Equals(c.Name, entry.Name));
            if (i < 0) { unmatched.Add(entry); continue; }
            result.Add(entry with { Hash = free[i].Hash, Present = true });
            free.RemoveAt(i);
        }
        foreach (var entry in unmatched)
        {
            var i = entry.Hash.Length > 0 ? free.FindIndex(c => c.Hash == entry.Hash) : -1;
            if (i < 0) { result.Add(entry with { Present = false }); continue; }
            result.Add(entry with { Name = free[i].Name, Present = true });
            free.RemoveAt(i);
        }
        var next = previous.Select(e => NumberOf(e.Id)).DefaultIfEmpty(0).Max() + 1;
        foreach (var file in free.OrderBy(c => c.Name, Names))
            result.Add(new RegistryEntry(IdOf(next++), file.Name, file.Hash, Present: true));
        return result.OrderBy(e => NumberOf(e.Id)).ToList();
    }

    /// <summary>
    /// Первый запуск с реестром при уже построенных отчётах: номера берутся из прежнего <c>Файлы.csv</c>, где имена скрыты,
    /// поэтому файлы сопоставляются по скрытому имени. Одинаковые скрытые имена раздаются по порядку настоящих.
    /// </summary>
    public static List<RegistryEntry> FromReport(IReadOnlyList<string> currentNames, IReadOnlyList<(string Id, string MaskedName)> rows)
    {
        var byMask = currentNames.GroupBy(FosHeuristics.MaskFileName)
            .ToDictionary(g => g.Key, g => g.OrderBy(n => n, Names).ToList());
        var result = new List<RegistryEntry>();
        foreach (var row in rows.OrderBy(r => NumberOf(r.Id)))
        {
            if (byMask.TryGetValue(row.MaskedName, out var names) && names.Count > 0)
            {
                result.Add(new RegistryEntry(row.Id, names[0], "", Present: true));
                names.RemoveAt(0);
            }
            else result.Add(new RegistryEntry(row.Id, row.MaskedName, "", Present: false));
        }
        return result;
    }

    /// <summary>Точные копии: номер файла → номер первого файла с тем же содержимым. Сам первый в словарь не входит.</summary>
    public static Dictionary<string, string> Copies(IEnumerable<RegistryEntry> present) =>
        present.Where(e => e.Hash.Length > 0).GroupBy(e => e.Hash)
            .SelectMany(g => g.OrderBy(e => NumberOf(e.Id)).Skip(1).Select(e => (e.Id, First: g.OrderBy(x => NumberOf(x.Id)).First().Id)))
            .ToDictionary(x => x.Id, x => x.First);

    // ---------- CSV ----------

    public static List<RegistryEntry> Read(string path) =>
        File.ReadAllLines(path, Encoding.UTF8).Skip(1).Where(l => l.Trim().Length > 0).Select(SplitCsv)
            .Select(c => new RegistryEntry(c[0].Trim('﻿'), c[1], c.Count > 2 ? c[2] : "", c.Count > 3 && c[3] == "да")).ToList();

    public static void Write(string path, IEnumerable<RegistryEntry> entries)
    {
        var s = new StringBuilder("id;файл;хеш;в папке\n");
        foreach (var e in entries) s.Append(string.Join(';', new[] { e.Id, e.Name, e.Hash, e.Present ? "да" : "нет" }.Select(QuoteCsv))).Append('\n');
        File.WriteAllText(path, s.ToString(), new UTF8Encoding(true));
    }

    /// <summary>Строки <c>Файлы.csv</c> прежнего прогона: номер и скрытое имя.</summary>
    public static List<(string Id, string MaskedName)> ReadReport(string path) =>
        File.ReadAllLines(path, Encoding.UTF8).Skip(1).Where(l => l.Trim().Length > 0).Select(SplitCsv)
            .Select(c => (c[0].Trim('﻿'), c[1])).ToList();

    public static string QuoteCsv(string v) => v.Contains(';') || v.Contains('"') || v.Contains('\n') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;

    /// <summary>Делит строку CSV по «;» с кавычками: имя файла может содержать точку с запятой.</summary>
    public static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ';') { fields.Add(field.ToString()); field.Clear(); }
            else field.Append(c);
        }
        fields.Add(field.ToString());
        return fields;
    }
}
