namespace DisciplineDataExtractor.Internal;

/// <summary>Замечания одного разбора. Создаётся на каждый разбор — общего состояния между разборами нет.</summary>
internal sealed class IssueLog
{
    private readonly List<ParseIssue> _items = [];

    public IReadOnlyList<ParseIssue> Items => _items;

    public void Error(string file, string? sheet, int? row, string message) =>
        _items.Add(new ParseIssue(IssueSeverity.Error, file, sheet, row, message));

    public void Warning(string file, string? sheet, int? row, string message) =>
        _items.Add(new ParseIssue(IssueSeverity.Warning, file, sheet, row, message));
}
