namespace DisciplineDataExtractor.Tests;

/// <summary>
/// Реальные файлы: учебный план 09.04.04 (2023), список и матрица компетенций, обезличенные должности.
/// По умолчанию — tests/fixtures (копируются в выходной каталог). Переменная RPDFOS_FIXTURES задаёт
/// другой каталог с файлами тех же имён — чтобы прогнать тесты на файлах вне репозитория.
/// </summary>
internal static class Fixtures
{
    public const string Plan = "plan-09.04.04-2023.xlsx";
    public const string CompetenceList = "competences-09.04.04.docx";
    public const string CompetenceMatrix = "matrix-09.04.04.docx";
    public const string Positions = "positions-anonymized.xlsx";

    private static string Dir =>
        Environment.GetEnvironmentVariable("RPDFOS_FIXTURES") is { Length: > 0 } dir
            ? dir
            : Path.Combine(AppContext.BaseDirectory, "fixtures");

    public static SourceFile File(string name) => SourceFile.FromPath(Path.Combine(Dir, name));

    public static ExtractionInput Input() =>
        new(File(Plan), File(CompetenceList), File(CompetenceMatrix), File(Positions));
}

/// <summary>Один разбор реальных файлов на класс тестов.</summary>
public sealed class RealFiles
{
    public ExtractionResult Result { get; } = new DataExtractor().Extract(Fixtures.Input());

    public Discipline Discipline(string code) => Result.Disciplines.Single(d => d.Code == code);

    public Competence Competence(string code) => Result.Competences.Single(c => c.Code == code);
}
