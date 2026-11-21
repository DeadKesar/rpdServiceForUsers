using System.Diagnostics;

namespace RpdFos.Corpus;

/// <summary>
/// Режим <c>--fos</c> (этап 7, шаг B1): разбор настоящих ФОС из <c>Корпус\ФОС</c>. База и Docker не нужны —
/// файлы только читаются. Отчёты — в <c>Корпус\_отчёты\ФОС\</c>; остальные отчёты прогона режим не трогает.
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

        // Номера F01… идут по имени файла: пока набор файлов не меняется, номера в отчётах и журнале совпадают.
        var files = Directory.GetFiles(folder).Where(f => !Path.GetFileName(f).StartsWith("~$"))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList();
        var items = new List<FosAnalysis>();
        foreach (var (file, i) in files.Select((f, i) => (f, i)))
        {
            var id = $"F{i + 1:00}";
            var converted = Path.Combine(corpus, "_преобразовано", FosReports.Folder, Path.GetFileNameWithoutExtension(file) + ".docx");
            var doc = FosReader.Read(id, file, converted);
            var a = FosAnalyzer.Analyze(doc);
            items.Add(a);
            Console.WriteLine($"{id} {doc.Format,-8} заголовков {a.Outline.Count,3}, таблиц {doc.Tables.Count(),3}, билетов {a.Tickets,3}" +
                              (doc.Error is not null ? $"  ОШИБКА: {doc.Error}" : doc.TextChars < 300 ? "  текста нет" : ""));
        }

        FosAnalysis? template = null;
        templatePath ??= FindTemplate();
        if (templatePath is not null && File.Exists(templatePath))
            template = FosAnalyzer.Analyze(FosReader.Read("T", templatePath, null));
        else Console.Error.WriteLine("Файл оформления ФОС не найден: карта закладок не строится (--template <путь>).");

        FosReports.Write(output, items, template, watch.Elapsed, started);
        Console.WriteLine($"Готово за {watch.Elapsed:mm\\:ss}. Отчёты: {Path.Combine(output, FosReports.Folder)}");
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
