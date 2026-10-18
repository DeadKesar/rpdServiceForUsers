using Xunit;

namespace DisciplineDataExtractor.Tests;

/// <summary>Ошибки — списком, а не исключениями; разборы не делят состояние.</summary>
public class RobustnessTests
{
    [Fact]
    public void Broken_file_is_an_error_and_other_files_are_still_read()
    {
        var input = Fixtures.Input() with { Plan = new SourceFile("plan.xlsx", "это не xlsx"u8.ToArray()) };

        var result = new DataExtractor().Extract(input);

        Assert.Contains(result.Issues, i => i is { Severity: IssueSeverity.Error, File: "plan.xlsx" } && i.Message.StartsWith("Файл не прочитан"));
        Assert.Empty(result.Disciplines);
        Assert.Equal(17, result.Competences.Count);
        Assert.Empty(result.Matrix); // без плана сопоставлять не с чем — и это тоже ошибка в списке
        Assert.Contains(result.Issues, i => i.File == Fixtures.CompetenceMatrix && i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void Wrong_file_in_slot_is_reported()
    {
        // Список компетенций вместо матрицы: таблиц матрицы в нём нет.
        var input = Fixtures.Input() with { CompetenceMatrix = Fixtures.File(Fixtures.CompetenceList) };

        var result = new DataExtractor().Extract(input);

        Assert.Empty(result.Matrix);
        Assert.Contains(result.Issues, i => i.Severity == IssueSeverity.Error && i.Message.Contains("матрицы"));
    }

    [Fact]
    public void Parallel_extractions_give_identical_results()
    {
        var extractor = new DataExtractor(); // один экземпляр на все потоки — состояния в нём нет
        var results = Enumerable.Range(0, 4).AsParallel().Select(_ => extractor.Extract(Fixtures.Input())).ToList();

        Assert.All(results, r =>
        {
            Assert.Equal(results[0].Disciplines.Count, r.Disciplines.Count);
            Assert.Equal(results[0].Matrix, r.Matrix);
            Assert.Equal(results[0].Issues, r.Issues);
        });
    }
}
