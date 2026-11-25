using RpdFos.Manuals;

// dotnet run --project tools/RpdFos.Manuals             — проверить руководства и собрать PDF в out/manuals
// dotnet run --project tools/RpdFos.Manuals -- --check  — только проверить (конвертер не нужен)
var root = AppContext.BaseDirectory;
while (root is not null && !File.Exists(Path.Combine(root, "RpdFos.slnx"))) root = Path.GetDirectoryName(root);
if (root is null)
{
    Console.Error.WriteLine("Корень репозитория не найден.");
    return 1;
}

var folder = Path.Combine(root, "docs", "manuals");
var problems = Manual.Check(folder);
foreach (var problem in problems) Console.Error.WriteLine(problem);
if (problems.Count > 0) return 1;

var manuals = Manual.LoadAll(folder);
foreach (var manual in manuals)
    Console.WriteLine($"{manual.Name}: заголовков {manual.Headings.Count}, снимков {manual.Images.Count}; ссылки и якоря в порядке.");
if (args.Contains("--check")) return 0;

using var converter = new HttpClient
{
    BaseAddress = new Uri(Environment.GetEnvironmentVariable("CONVERTER_URL") ?? "http://localhost:3000"),
    Timeout = TimeSpan.FromMinutes(2)
};
var output = Directory.CreateDirectory(Path.Combine(root, "out", "manuals")).FullName;
try
{
    foreach (var manual in manuals)
    {
        var pdf = await new ManualPdf(converter).BuildAsync(manual);
        var path = Path.Combine(output, Path.ChangeExtension(manual.Name, ".pdf"));
        await File.WriteAllBytesAsync(path, pdf);
        Console.WriteLine($"{path}: {pdf.Length / 1024} КБ");
    }
    return 0;
}
catch (HttpRequestException e)
{
    Console.Error.WriteLine($"Конвертер {converter.BaseAddress} недоступен ({e.Message}). Запустите: docker compose up -d converter");
    return 1;
}
