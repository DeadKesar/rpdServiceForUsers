namespace DisciplineDataExtractor;

/// <summary>
/// Результат разбора. Частичный результат допустим: что удалось разобрать — в коллекциях,
/// что не удалось — в <see cref="Issues"/>.
/// </summary>
public sealed record ExtractionResult(
    CurriculumPlanInfo? Plan,
    IReadOnlyList<Discipline> Disciplines,
    IReadOnlyList<Competence> Competences,
    IReadOnlyList<MatrixLink> Matrix,
    IReadOnlyList<Employee> Employees,
    IReadOnlyList<ParseIssue> Issues)
{
    public bool HasErrors => Issues.Any(i => i.Severity == IssueSeverity.Error);
}
