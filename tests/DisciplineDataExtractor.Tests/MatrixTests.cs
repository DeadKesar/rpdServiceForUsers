using Xunit;

namespace DisciplineDataExtractor.Tests;

/// <summary>Матрица компетенций: дисциплины в ней — по названию, связи — на уровне компетенций.</summary>
public class MatrixTests(RealFiles files) : IClassFixture<RealFiles>
{
    private IEnumerable<string> CompetencesOf(string code) =>
        files.Result.Matrix.Where(m => m.DisciplineCode == code).Select(m => m.CompetenceCode);

    [Fact]
    public void All_resolved_marks_become_links()
    {
        // 99 отметок «+» в трёх таблицах, 2 из них — у дисциплины, которой нет в плане.
        Assert.Equal(97, files.Result.Matrix.Count);
        Assert.All(files.Result.Matrix, m => Assert.Null(m.IndicatorCode));
    }

    [Fact]
    public void Discipline_links_across_tables()
    {
        Assert.Equal(["УК-1", "ОПК-1", "ОПК-4"], CompetencesOf("Б1.О.10"));
        Assert.Equal(17, CompetencesOf("Б3.01(Д)").Count()); // ВКР формирует все компетенции
    }

    [Fact]
    public void Name_variants_are_matched()
    {
        // «Стандартизация ИТ-продуктов и систем» в матрице = «Стандартизация ИТ продуктов и систем» в плане.
        Assert.Equal(["ПК-2"], CompetencesOf("Б1.В.07"));

        // «…(технологическая (проектно-технологическая))» — по совпадению слов, с предупреждением.
        Assert.Equal(["ОПК-2", "ОПК-3", "ОПК-5", "ОПК-8"], CompetencesOf("Б2.О.03(П)"));
        Assert.Contains(files.Result.Issues, i =>
            i is { Severity: IssueSeverity.Warning, File: Fixtures.CompetenceMatrix, Sheet: "Таблица 2", Row: 47 }
            && i.Message.Contains("по совпадению слов"));
    }

    [Fact]
    public void Unknown_discipline_is_reported_with_location()
    {
        var error = Assert.Single(files.Result.Issues, i => i.Severity == IssueSeverity.Error);

        Assert.Equal((Fixtures.CompetenceMatrix, "Таблица 3", 39), (error.File, error.Sheet, error.Row));
        Assert.Contains("Внедрение и эксплуатация информационных систем", error.Message);
        Assert.Contains("ПК-1, ПК-2", error.Message);
        // Обратная сторона: дисциплины плана без строки в матрице — предупреждение.
        Assert.Contains(files.Result.Issues, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("Б1.В.ДВ.03.01"));
    }
}
