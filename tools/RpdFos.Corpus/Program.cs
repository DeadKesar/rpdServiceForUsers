using System.Diagnostics;
using RpdFos.Corpus;

// Прогон системы по корпусу настоящих данных вуза (этап 5.3). Запуск:
//   dotnet run --project tools/RpdFos.Corpus -- <папка корпуса> [--only <часть имени плана>] [--no-rpd]
// Папка корпуса — аргументом или переменной RPDFOS_CORPUS; конвертер PDF — RPDFOS_CONVERTER (по умолчанию
// http://localhost:3000, нужен только для наглядного сравнения). Нужен Docker: база — одноразовая, в контейнере.
var corpus = args.FirstOrDefault(a => !a.StartsWith("--")) ?? Environment.GetEnvironmentVariable("RPDFOS_CORPUS");
if (corpus is null || !Directory.Exists(corpus))
{
    Console.Error.WriteLine("Укажите папку корпуса аргументом или в переменной RPDFOS_CORPUS.");
    return 1;
}
var only = args.SkipWhile(a => a != "--only").Skip(1).FirstOrDefault();
var withRpd = !args.Contains("--no-rpd");

var started = DateTimeOffset.Now;
var watch = Stopwatch.StartNew();
var output = Path.Combine(corpus, "_отчёты");
// Повторный прогон перезаписывает отчёты целиком: устаревших файлов от прошлого прогона не остаётся.
// «Главные находки.md» пишет автор по итогам прогона — инструмент его не трогает.
Directory.CreateDirectory(output);
foreach (var dir in Directory.GetDirectories(output)) Directory.Delete(dir, recursive: true);
foreach (var file in Directory.GetFiles(output).Where(f => Path.GetFileName(f) != Reports.MainFindings)) File.Delete(file);

await using var bench = await Workbench.StartAsync(Environment.GetEnvironmentVariable("RPDFOS_CONVERTER"));
var sets = CorpusSet.Read(corpus).Where(s => only is null || s.Plan.Contains(only)).ToList();
var runner = new PlanRunner(bench, corpus);
var plans = new List<PlanReport>();
foreach (var set in sets)
{
    Console.Write($"[{plans.Count + 1}/{sets.Count}] {set.Name} … ");
    var report = await runner.RunAsync(set);
    plans.Add(report);
    Console.WriteLine($"{report.Status}, дисциплин {report.Disciplines.Count}, находок {report.Findings.Count} ({report.Elapsed.TotalSeconds:0} с)");
}

var rpds = new List<RpdReport>();
var visual = new List<(string Title, List<string> Files)>();
if (withRpd)
{
    var comparer = new RpdComparer(bench, corpus, plans);
    rpds.AddRange(await comparer.RunAsync());
    Console.WriteLine($"РПД: {rpds.Count}, сравнено {rpds.Count(r => r.Status.StartsWith("сравнено"))}");

    // Наглядно: ПФК (несколько семестров) и 20.03.01 «Производственная безопасность» с «Проектом».
    var pfk = rpds.FirstOrDefault(r => r.File.Contains("ПФК") && !r.File.Contains("ОВЗ") && r.DocumentId is not null &&
                                       r.Rows.Any(x => x.Field == "Семестры" && x.Ours.Contains(',')));
    var safety = rpds.Where(r => r.File.Contains("Производ") && r.DocumentId is not null).ToList();
    foreach (var r in new[] { pfk }.Concat(safety).OfType<RpdReport>())
        visual.Add((r.File, await comparer.VisualAsync(r, output)));
}

Reports.Write(output, plans, rpds, visual, watch.Elapsed, started);
Console.WriteLine($"Готово за {watch.Elapsed:hh\\:mm\\:ss}. Отчёты: {output}");
return 0;
