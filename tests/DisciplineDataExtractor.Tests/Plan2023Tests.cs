using Xunit;

namespace DisciplineDataExtractor.Tests;

/// <summary>
/// Учебный план формата 2023 года (09.03.01, бакалавриат): код направления — отдельной ячейкой титула, название и
/// профиль — в ячейке без кода; на листе «План» по семестрам только з. е., часы — на листах «Курс N».
/// Список и матрица компетенций — от 09.04.04: проверяется только план.
/// </summary>
public sealed class Plan2023Files
{
    public ExtractionResult Result { get; } = new DataExtractor().Extract(new ExtractionInput(
        Fixtures.File(Fixtures.Plan2023), Fixtures.File(Fixtures.CompetenceList), Fixtures.File(Fixtures.CompetenceMatrix)));

    public Discipline Discipline(string code) => Result.Disciplines.Single(d => d.Code == code);
}

public class Plan2023Tests(Plan2023Files files) : IClassFixture<Plan2023Files>
{
    [Fact]
    public void Direction_and_profile_are_read_by_labels()
    {
        var plan = files.Result.Plan!;

        Assert.Equal("09.03.01", plan.DirectionCode);
        Assert.Equal("Информатика и вычислительная техника", plan.DirectionName);
        Assert.Equal("Разработка программных комплексов в рамках цифровой трансформации деятельности предприятий", plan.Profile);
        Assert.Equal(EducationLevel.Bachelor, plan.Level);
        Assert.Equal(2023, plan.Year);
        Assert.DoesNotContain(files.Result.Issues, i => i.Message.StartsWith("Не найдена направленность")
                                                        || i.Message.StartsWith("Не найдено наименование направления"));
    }
}
