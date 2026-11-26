using System.Text.Json;
using System.Text.RegularExpressions;

namespace RpdFos.Manuals;

/// <summary>Настройка и файл, где она встретилась.</summary>
public sealed record Setting(string Key, string Source);

/// <summary>
/// Полнота руководства системного программиста (этап 8, B11): каждая настройка из <c>appsettings*.json</c> хоста, образца
/// <c>.env</c> стенда, переменных хоста в compose стенда и в Dockerfile, ключей, которые код хоста читает строкой, упомянута в руководстве — в обратных кавычках, как
/// пишется в таблице настроек. Настройку, добавленную в файл и забытую в руководстве, ловят проверка инструмента и тест.
/// </summary>
public static partial class SettingsCoverage
{
    public const string ManualName = "sysadmin.md";

    /// <summary>Все настройки из файлов репозитория; ключи конфигурации — через двоеточие, как в разделах <c>appsettings</c>.</summary>
    public static IReadOnlyList<Setting> Collect(string root)
    {
        var found = new List<Setting>();
        var api = Path.Combine(root, "src", "RpdFos.Api");
        foreach (var file in Directory.GetFiles(api, "appsettings*.json").Order(StringComparer.Ordinal))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(file));
            Flatten(json.RootElement, "", Path.GetFileName(file), found);
        }

        found.AddRange(Lines(root, "deploy/.env.example").Select(l => EnvLine().Match(l)).Where(m => m.Success)
            .Select(m => new Setting(m.Groups[1].Value, "deploy/.env.example")));
        // Переменные хоста: «Раздел__Ключ» в compose и Dockerfile — те же ключи конфигурации, что в appsettings.
        found.AddRange(Lines(root, "deploy/docker-compose.yml").Select(l => ComposeVariable().Match(l)).Where(m => m.Success)
            .Select(m => new Setting(m.Groups[1].Value.Replace("__", ":"), "deploy/docker-compose.yml")));
        found.AddRange(Lines(root, "Dockerfile").SelectMany(l => DockerVariable().Matches(l))
            .Select(m => new Setting(m.Groups[1].Value.Replace("__", ":"), "Dockerfile")));

        // Ключи, которые хост читает в коде строкой («Imports:MaxFileBytes»): у них может не быть строки ни в одном файле.
        foreach (var project in new[] { "RpdFos.Api", "RpdFos.Core" })
        {
            var dir = Path.Combine(root, "src", project);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                         .Where(f => !Excluded().IsMatch(f.Replace('\\', '/'))).Order(StringComparer.Ordinal))
                found.AddRange(CodeKey().Matches(File.ReadAllText(file)).Select(m => m.Groups[1].Value)
                    .Where(k => !k.StartsWith("Npgsql:") && !k.StartsWith("Relational:"))
                    .Select(k => new Setting(k, Path.GetRelativePath(root, file).Replace('\\', '/'))));
        }

        return found.DistinctBy(s => s.Key).ToList();
    }

    /// <summary>Замечания: настройки, которых нет в руководстве системного программиста.</summary>
    public static IReadOnlyList<string> Check(string root)
    {
        var manual = Path.Combine(root, "docs", "manuals", ManualName);
        if (!File.Exists(manual)) return [$"{ManualName}: руководства нет."];
        var text = File.ReadAllText(manual);
        return Collect(root).Where(s => !text.Contains($"`{s.Key}`"))
            .Select(s => $"{ManualName}: настройка `{s.Key}` ({s.Source}) не описана.").ToList();
    }

    private static void Flatten(JsonElement element, string prefix, string source, List<Setting> found)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            found.Add(new Setting(prefix, source));
            return;
        }
        foreach (var p in element.EnumerateObject())
            Flatten(p.Value, prefix.Length == 0 ? p.Name : $"{prefix}:{p.Name}", source, found);
    }

    private static IEnumerable<string> Lines(string root, string relative)
    {
        var path = Path.Combine(root, relative);
        return File.Exists(path) ? File.ReadLines(path) : [];
    }

    // Строковый литерал из одного ключа «Раздел:Ключ»; сообщения, которые начинаются с ключа, не подходят.
    [GeneratedRegex(@"""((?:[A-Z][A-Za-z]+:)+[A-Z][A-Za-z]+)""")]
    private static partial Regex CodeKey();

    [GeneratedRegex(@"/(obj|bin|Migrations)/")]
    private static partial Regex Excluded();

    [GeneratedRegex(@"^([A-Z][A-Z0-9_]*)=")]
    private static partial Regex EnvLine();

    [GeneratedRegex(@"^\s+([A-Za-z]+__[A-Za-z0-9_.]+):")]
    private static partial Regex ComposeVariable();

    // ENV в Dockerfile: «Раздел__Ключ=…» и ASPNETCORE_*.
    [GeneratedRegex(@"\b((?:[A-Za-z]+__[A-Za-z0-9_.]+)|ASPNETCORE_[A-Z_]+)=")]
    private static partial Regex DockerVariable();
}
