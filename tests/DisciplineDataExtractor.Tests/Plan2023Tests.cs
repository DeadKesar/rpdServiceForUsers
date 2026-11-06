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

    [Fact]
    public void Hours_by_semester_come_from_course_sheets()
    {
        var d = files.Discipline("Б1.О.01.01");

        Assert.Equal("Математический анализ", d.Name);
        Assert.Equal([1, 2], d.Semesters.Select(s => s.Semester));
        Assert.All(d.Semesters, s => Assert.Equal(
            (4, 28, 0, 28, 52, 36, 56), (s.Credits, s.Lectures, s.Labs, s.Practice, s.SelfStudy, s.Control, s.ContactHours)));
        Assert.All(d.Semesters, s => Assert.Equal([ControlType.Exam], s.ControlTypes));
    }

    [Fact]
    public void Multi_semester_discipline_takes_each_semester_from_its_course()
    {
        // Строки дисциплины есть в блоках 1-го и 2-го курса: значения каждого блока — семестры своего курса.
        var s = files.Discipline("Б1.О.09.01").Semesters;

        Assert.Equal([1, 2, 3], s.Select(x => x.Semester));
        Assert.Equal((28, 24, 12, 44, 0, 64), (s[0].Lectures, s[0].Labs, s[0].Practice, s[0].SelfStudy, s[0].Control, s[0].ContactHours));
        Assert.Equal((20, 20, 8, 24, 36, 48), (s[1].Lectures, s[1].Labs, s[1].Practice, s[1].SelfStudy, s[1].Control, s[1].ContactHours));
        Assert.Equal((24, 16, 0, 32, 36, 40), (s[2].Lectures, s[2].Labs, s[2].Practice, s[2].SelfStudy, s[2].Control, s[2].ContactHours));
        Assert.Equal([ControlType.Credit], s[0].ControlTypes);
        Assert.Equal([ControlType.Exam], s[1].ControlTypes);
    }

    [Fact]
    public void Course_work_discipline()
    {
        var s = Assert.Single(files.Discipline("Б1.В.01.01").Semesters);

        Assert.Equal(4, s.Semester);
        Assert.Equal((1, 0, 10, 26, 10), (s.Credits, s.Lectures, s.Practice, s.SelfStudy, s.ContactHours));
        Assert.Equal([ControlType.CourseworkPaper], s.ControlTypes);
    }

    [Fact]
    public void Zero_credit_discipline_has_hours_in_every_semester()
    {
        // Элективы по физической культуре: 0 з. е., 328 ч, часы в 1–6-м семестрах, зачёты в 3–6-м.
        var d = files.Discipline("Б1.В.ДВ.01.01");

        Assert.Equal((0, 328), (d.Credits, d.TotalHours));
        Assert.Equal([1, 2, 3, 4, 5, 6], d.Semesters.Select(s => s.Semester));
        Assert.Equal(144, d.Semesters.Sum(s => s.ContactHours));
        Assert.Equal(184, d.Semesters.Sum(s => s.SelfStudy));
    }

    [Fact]
    public void Contact_hours_by_semester_match_plan_totals()
    {
        Assert.Empty(files.Result.Issues.Where(i => i.Message.Contains("контактные часы по семестрам")).Select(i => i.Message));
    }
}
