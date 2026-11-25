using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using RpdFos.Api.Features.Printing;
using RpdFos.Api.Tests.Documents;
using RpdFos.Api.Tests.Integration;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Printing;
using Xunit;
using Xunit.Abstractions;

namespace RpdFos.Api.Tests.Bench;

/// <summary>
/// Замер скорости печати (этап 8, B3; требование Т9 и испытание И9 ЛР ПМИ, NFR-PER-03): от запроса формирования до статуса
/// «готово» при 1…5 одновременных пользователях, у каждого своя РПД объёма И9 — 10 тем, 60 практических занятий, 10 лабораторных
/// работ, 10 записей основной литературы; DOCX и PDF. В обычном прогоне не запускается: нужны переменная окружения
/// <c>RPDFOS_BENCH=1</c> и конвертер по адресу <c>BENCH_CONVERTER</c> (по умолчанию http://localhost:3000, как в docker-compose.yml).
/// Настройки — <c>BENCH_POLL</c> (секунды опроса очереди), <c>BENCH_WORKERS</c>, <c>BENCH_ROUNDS</c>, <c>BENCH_LABEL</c>; итог — в
/// <c>out/bench/print-&lt;метка&gt;.md</c>. Запуск: <c>dotnet test --filter "Category=Bench"</c>.
/// </summary>
[Trait("Category", "Bench")]
public class PrintSpeedBench(ITestOutputHelper output)
{
    public const string Switch = "RPDFOS_BENCH";

    private static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

    [BenchFact]
    public async Task Print_time_by_concurrent_users()
    {
        var poll = Env("BENCH_POLL", "15");
        var workers = Env("BENCH_WORKERS", "2");
        var rounds = int.Parse(Env("BENCH_ROUNDS", "3"), CultureInfo.InvariantCulture);
        var label = Env("BENCH_LABEL", $"poll{poll}-workers{workers}");

        await using var scope = await BenchScope.StartAsync(poll, workers, Env("BENCH_CONVERTER", "http://localhost:3000"));
        var host = scope.Host;
        var template = await DocumentData.PublishedTemplateAsync(host);

        var users = new List<(HttpClient Client, Guid DocumentId)>();
        var tried = new HashSet<string>();
        while (users.Count < 5)
            users.Add(await CreateBigRpdAsync(host, template.Id, tried));

        var notes = new StringBuilder();
        // Прогрев: JIT, соединения, первая конвертация в LibreOffice — в замер не входят.
        foreach (var format in new[] { GenerationFormats.Docx, GenerationFormats.Pdf })
        {
            var warm = await PrintAsync(users[0].Client, users[0].DocumentId, format);
            // Объём документа — в отчёт: замер имеет смысл только на документе объёма И9.
            var size = warm.Item.SizeBytes;
            var pages = "";
            if (format == GenerationFormats.Pdf)
            {
                var bytes = await users[0].Client.GetByteArrayAsync($"/api/documents/{users[0].DocumentId}/files/{warm.Item.FileId}");
                using var pdf = UglyToad.PdfPig.PdfDocument.Open(bytes);
                var text = string.Join(" ", pdf.GetPages().Select(p => p.Text));
                pages = $", страниц {pdf.NumberOfPages}; в тексте: тема 10 — {text.Contains("Тема 10.")}, практическое занятие 60 — " +
                        $"{text.Contains("Практическое занятие 60.")}, лабораторная работа 10 — {text.Contains("Лабораторная работа 10.")}, " +
                        $"литература 10 — {text.Contains("часть 10 :")}";
            }
            notes.AppendLine($"- {format}: {size} байт{pages}; предупреждения: {string.Join(" | ", warm.Item.Warnings)}");
        }

        var report = new StringBuilder();
        report.AppendLine($"Метка: {label}; опрос очереди {poll} с; исполнителей формирования {workers}; кругов {rounds}.");
        report.AppendLine();
        report.Append(notes);
        report.AppendLine();
        report.AppendLine("| Формат | Одновременно | Среднее самого медленного, с | Худшее, с | Среднее по пользователям, с | Предупреждения |");
        report.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var format in new[] { GenerationFormats.Docx, GenerationFormats.Pdf })
            for (var n = 1; n <= 5; n++)
            {
                var slowest = new List<double>();
                var all = new List<double>();
                var warnings = 0;
                for (var round = 0; round < rounds; round++)
                {
                    var results = await Task.WhenAll(users.Take(n).Select(u => PrintAsync(u.Client, u.DocumentId, format)));
                    slowest.Add(results.Max(r => r.Seconds));
                    all.AddRange(results.Select(r => r.Seconds));
                    warnings += results.Count(r => r.Warnings > 0);
                }
                var line = $"| {format} | {n} | {slowest.Average():F1} | {slowest.Max():F1} | {all.Average():F1} | {warnings} |";
                report.AppendLine(line);
                output.WriteLine(line);
            }

        var directory = Path.Combine(FindRoot(), "out", "bench");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, $"print-{label}.md"), report.ToString());
    }

    /// <summary>
    /// Нагрузка на базу от опроса очереди: счётчики PostgreSQL за минуту при простое хоста. Хост на пустой базе — те же
    /// сервера Hangfire (разбор, формирование), что и на стенде, без почты.
    /// </summary>
    [BenchFact]
    public async Task Idle_queue_polling_load()
    {
        var poll = Env("BENCH_POLL", "15");
        var window = int.Parse(Env("BENCH_IDLE_SECONDS", "60"), CultureInfo.InvariantCulture);
        await using var scope = await BenchScope.StartAsync(poll, Env("BENCH_WORKERS", "2"), Env("BENCH_CONVERTER", "http://localhost:3000"));
        var host = scope.Host;
        
        await Task.Delay(TimeSpan.FromSeconds(10)); // запуск серверов задач

        var before = await CountersAsync(host);
        await Task.Delay(TimeSpan.FromSeconds(window));
        var after = await CountersAsync(host);
        var line = $"опрос {poll} с, простой {window} с: транзакций {(after.Commits - before.Commits) / (double)window:F1}/с, " +
                   $"строк прочитано {(after.Fetched - before.Fetched) / (double)window:F1}/с";
        output.WriteLine(line);
        var directory = Path.Combine(FindRoot(), "out", "bench");
        Directory.CreateDirectory(directory);
        await File.AppendAllTextAsync(Path.Combine(directory, "idle-polling.txt"), line + Environment.NewLine);
    }

    private static async Task<(long Commits, long Fetched)> CountersAsync(BenchHost host)
    {
        var result = (0L, 0L);
        await host.WithDbAsync(async db =>
        {
            var connection = Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.GetDbConnection(db.Database);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            // Статистика PostgreSQL обновляется с задержкой — читаем после паузы.
            command.CommandText = "select pg_stat_clear_snapshot(); ";
            await command.ExecuteNonQueryAsync();
            command.CommandText = "select xact_commit, tup_fetched from pg_stat_database where datname = current_database()";
            await using var reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();
            result = (reader.GetInt64(0), reader.GetInt64(1));
        });
        return result;
    }

    private static async Task<(HttpClient Client, Guid DocumentId)> CreateBigRpdAsync(BenchHost host, Guid templateId, HashSet<string> tried)
    {
        var (client, _) = await DocumentData.UserAsync(host);
        string[] codes = [];
        await host.WithDbAsync(async db => codes = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToArrayAsync(
            Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AsNoTracking(db.Disciplines).Select(d => d.Code)));
        foreach (var code in codes.OrderBy(c => c, StringComparer.Ordinal))
        {
            if (!tried.Add(code)) continue;
            var response = await client.PostAsJsonAsync("/api/documents",
                new RpdFos.Api.Features.Documents.CreateDocumentRequest(templateId, await DocumentData.DisciplineAsync(host, code)));
            if (!response.IsSuccessStatusCode) continue;
            var created = (await response.Content.ReadFromJsonAsync<RpdFos.Api.Features.Documents.DocumentDetails>())!;
            var visible = created.Metadata.Sections.Where(s => s.Visible).Select(s => s.Key).ToHashSet();
            // Раздел «практические занятия» и «лабораторные работы» у документа есть, только когда план даёт часы.
            if (!visible.Contains("practical_classes") || !visible.Contains("lab_works")) continue;

            var version = await DocumentData.FillForSubmissionAsync(client, created);
            var plan = DocumentData.Sums(created.Payload);
            var semester = created.Payload.GetProperty("hours_distribution").GetProperty("semesters")[0].GetProperty("semester").GetInt32();
            var sections = new Dictionary<string, JsonNode>
            {
                ["thematic_plan"] = new JsonArray(Enumerable.Range(1, 10).Select(i => (JsonNode)new JsonObject
                {
                    ["section_number"] = (i + 1) / 2, ["section_title"] = $"Раздел {(i + 1) / 2}. Методы и модели",
                    ["semester"] = semester, ["topic_title"] = $"Тема {i}. Построение и исследование моделей систем",
                    ["topic_description"] = string.Concat(Enumerable.Repeat("Понятие модели и моделирования. Классификация моделей. Этапы построения модели. ", 6)),
                    ["hours"] = new JsonObject
                    {
                        ["lectures"] = plan.Lectures / 10, ["practice"] = plan.Practice / 10, ["lab_hours"] = plan.Labs / 10,
                        ["self_study"] = plan.SelfStudy / 10
                    },
                    ["current_control_forms"] = new JsonArray("Опрос")
                }).ToArray()),
                ["practical_classes"] = new JsonArray(Enumerable.Range(1, 60).Select(i => (JsonNode)new JsonObject
                    { ["number"] = i.ToString(CultureInfo.InvariantCulture), ["title"] = $"Практическое занятие {i}. Решение задач по теме", ["hours"] = 1 }).ToArray()),
                ["lab_works"] = new JsonArray(Enumerable.Range(1, 10).Select(i => (JsonNode)new JsonObject
                    { ["number"] = i.ToString(CultureInfo.InvariantCulture), ["title"] = $"Лабораторная работа {i}. Исследование модели", ["hours"] = 2 }).ToArray()),
                ["literature"] = new JsonObject
                {
                    ["main"] = new JsonArray(Enumerable.Range(1, 10).Select(i => (JsonNode)new JsonObject
                    {
                        ["citation"] = $"Автор А. А. Моделирование систем, часть {i} : учебник. — Москва : Наука, 2024. — 320 с.", ["year"] = 2024
                    }).ToArray()),
                    ["additional"] = new JsonArray()
                }
            };
            foreach (var (section, value) in sections)
                version = (await DocumentData.PatchAsync(client, created.Document.Id, section, version, value)).Version;
            return (client, created.Document.Id);
        }
        throw new InvalidOperationException("В плане не нашлось дисциплин с практическими занятиями и лабораторными работами.");
    }

    private static async Task<(double Seconds, int Warnings, GenerationItem Item)> PrintAsync(HttpClient client, Guid documentId, string format)
    {
        var watch = Stopwatch.StartNew();
        var response = await client.PostAsync($"/api/documents/{documentId}/print/{format}", null);
        response.EnsureSuccessStatusCode();
        var item = (await response.Content.ReadFromJsonAsync<GenerationItem>())!;
        while (true)
        {
            item = (await client.GetFromJsonAsync<GenerationItem>($"/api/documents/{documentId}/generations/{item.TaskId}"))!;
            if (item.Status == GenerationStatuses.Succeeded) return (watch.Elapsed.TotalSeconds, item.Warnings.Count, item);
            if (item.Status == GenerationStatuses.Failed) throw new InvalidOperationException($"Формирование не удалось: {item.ErrorMessage}");
            if (watch.Elapsed > TimeSpan.FromMinutes(5)) throw new TimeoutException("Формирование не завершилось за 5 минут.");
            await Task.Delay(50); // клиент опрашивает чаще, чем очередь, — задержку даёт сервер, а не замер
        }
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RpdFos.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Корень репозитория не найден.");
    }
}

/// <summary>Хост с настоящим конвертером и заданными опросом очереди и числом исполнителей.</summary>
internal sealed class BenchHost(string poll, string workers, string converterUrl) : DatabaseFixture
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Jobs:QueuePollSeconds", poll);
        builder.UseSetting("Jobs:GenerationWorkers", workers);
        builder.UseSetting("Generation:NumberingTimeoutSeconds", "30");
        builder.ConfigureTestServices(services =>
            services.AddSingleton<IPdfConverter>(new GotenbergPdfConverter(new HttpClient
            {
                BaseAddress = new Uri(converterUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(120)
            })));
    }

}

/// <summary>Запуск и остановка хоста: контейнер базы и каталог файлов освобождает IAsyncLifetime, а не IAsyncDisposable.</summary>
internal sealed class BenchScope : IAsyncDisposable
{
    public BenchHost Host { get; }

    private BenchScope(BenchHost host) => Host = host;

    public static async Task<BenchScope> StartAsync(string poll, string workers, string converterUrl)
    {
        var host = new BenchHost(poll, workers, converterUrl);
        await ((IAsyncLifetime)host).InitializeAsync();
        return new BenchScope(host);
    }

    public async ValueTask DisposeAsync() => await ((IAsyncLifetime)Host).DisposeAsync();
}

/// <summary>Факт, который идёт только при <c>RPDFOS_BENCH=1</c>: замер долгий и нужен конвертер.</summary>
public sealed class BenchFactAttribute : FactAttribute
{
    public BenchFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(PrintSpeedBench.Switch) != "1")
            Skip = $"Замер скорости печати: задайте {PrintSpeedBench.Switch}=1 (этап 8, B3).";
    }
}
