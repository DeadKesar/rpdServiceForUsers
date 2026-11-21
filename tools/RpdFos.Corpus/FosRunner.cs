using System.Diagnostics;

namespace RpdFos.Corpus;

/// <summary>
/// Режим <c>--fos</c> (этап 7, шаг B1): разбор настоящих ФОС из <c>Корпус\ФОС</c>. База и Docker не нужны —
/// файлы только читаются. Отчёты — в <c>Корпус\_отчёты\ФОС\</c>; остальные отчёты прогона режим не трогает.
/// Номера файлов F01… закрепляет реестр <see cref="FosRegistry"/> в корне корпуса.
/// </summary>
public static class FosRunner
{
    public static void Run(string corpus, string output, string? templatePath)
    {
        var watch = Stopwatch.StartNew();
        var started = DateTimeOffset.Now;
        var folder = Path.Combine(corpus, FosReports.Folder);
        if (!Directory.Exists(folder))
        {
            Console.Error.WriteLine($"Нет папки {folder}.");
            return;
        }

        var current = Directory.GetFiles(folder).Where(f => !Path.GetFileName(f).StartsWith("~$"))
            .Select(f => (Name: Path.GetFileName(f), Hash: FosRegistry.HashOf(f))).ToList();

        // Номера не пересчитываются при появлении файлов: берутся из реестра, а при его отсутствии — из отчётов прошлого
        // прогона (имена там скрыты, файлы сопоставляются по скрытому имени).
        var registryPath = Path.Combine(corpus, FosRegistry.FileName);
        var lastReport = Path.Combine(output, FosReports.Folder, "Файлы.csv");
        var previous = File.Exists(registryPath) ? FosRegistry.Read(registryPath)
            : File.Exists(lastReport) ? FosRegistry.FromReport(current.Select(c => c.Name).ToList(), FosRegistry.ReadReport(lastReport))
            : [];
        var entries = FosRegistry.Assign(current, previous);
        FosRegistry.Write(registryPath, entries);
        var knownIds = previous.Select(e => e.Id).ToHashSet();
        Console.WriteLine($"Реестр {FosRegistry.FileName}: файлов {entries.Count(e => e.Present)}, новых {entries.Count(e => !knownIds.Contains(e.Id))}, " +
                          $"нет в папке {entries.Count(e => !e.Present)}");
        var copies = FosRegistry.Copies(entries.Where(e => e.Present));

        var items = new List<FosAnalysis>();
        foreach (var entry in entries)
        {
            var path = Path.Combine(folder, entry.Name);
            var converted = Path.Combine(corpus, "_преобразовано", FosReports.Folder, Path.GetFileNameWithoutExtension(entry.Name) + ".docx");
            var doc = entry.Present ? FosReader.Read(entry.Id, path, converted) : FosReader.Missing(entry.Id, entry.Name);
            var a = FosAnalyzer.Analyze(doc);
            a.DuplicateOf = copies.GetValueOrDefault(entry.Id);
            items.Add(a);
            Console.WriteLine($"{entry.Id} {doc.Format,-8} {a.DocKind,-3} заголовков {a.Outline.Count,3}, таблиц {doc.Tables.Count(),3}, билетов {a.Tickets,3}" +
                              (doc.Error is not null ? $"  {doc.Error}" : doc.TextChars < 300 ? "  текста нет" : "") +
                              (a.DuplicateOf is not null ? $"  копия {a.DuplicateOf}" : ""));
        }

        FosAnalysis? template = null;
        templatePath ??= FindTemplate();
        if (templatePath is not null && File.Exists(templatePath))
            template = FosAnalyzer.Analyze(FosReader.Read("T", templatePath, null));
        else Console.Error.WriteLine("Файл оформления ФОС не найден: карта закладок не строится (--template <путь>).");

        FosReports.Write(output, items, template, watch.Elapsed, started);
        Console.WriteLine($"Готово за {watch.Elapsed:mm\\:ss}. Отчёты: {Path.Combine(output, FosReports.Folder)}; номера и имена файлов: {registryPath}");
    }

    private static string? FindTemplate()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var path = Path.Combine(dir.FullName, "tests", "fixtures", "FOS_TemplateBookmarks.docx");
                if (File.Exists(path)) return path;
            }
        return null;
    }
}
