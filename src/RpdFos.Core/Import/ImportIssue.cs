using System.Text.Json;
using DisciplineDataExtractor;

namespace RpdFos.Core.Import;

/// <summary>Замечание импорта для parser_runs.errors_log: разбора или самого конвейера.</summary>
public sealed record ImportIssue(string Severity, string File, string? Sheet, int? Row, string Message)
{
    public const string Error = "error", Warning = "warning";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static ImportIssue From(ParseIssue issue) =>
        new(issue.Severity == IssueSeverity.Error ? Error : Warning, issue.File, issue.Sheet, issue.Row, issue.Message);

    public static string Serialize(IEnumerable<ImportIssue> issues) => JsonSerializer.Serialize(issues, Json);

    public static IReadOnlyList<ImportIssue> Deserialize(string json) =>
        JsonSerializer.Deserialize<List<ImportIssue>>(json, Json) ?? [];
}
