using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Xunit;

namespace DisciplineDataExtractor.Tests;

/// <summary>Должности сотрудников (обезличенная копия: ФИО — «СотрудникNN И. О.», NN — номер строки файла).</summary>
public class EmployeesTests(RealFiles files) : IClassFixture<RealFiles>
{
    private Employee ByOfficialName(string name) => files.Result.Employees.Single(e => e.OfficialName == name);

    [Fact]
    public void Employees_without_template_rows()
    {
        var employees = files.Result.Employees;

        Assert.Equal(37, employees.Count);
        // Шаблонные строки направлений («09.04.04 — отображение — должность — Фио из файла») — не сотрудники.
        Assert.DoesNotContain(employees, e => Regex.IsMatch(e.OfficialName, @"^\d{2}\.\d{2}\.\d{2}"));
    }

    [Fact]
    public void Department_head()
    {
        var head = ByOfficialName("Информационные технологии и вычислительные системы");

        Assert.Equal("Сотрудник18 И. О.", head.FullName);
        Assert.Equal("Заведующий кафедрой", head.Position);
        Assert.Equal("ИТиВС", head.DocumentTitle);
        Assert.Equal("Информационные технологии и вычислительные системы", head.Department);
        Assert.Equal("Институт информационных технологий", head.Institute);
    }

    [Fact]
    public void Institute_director_and_rector()
    {
        var director = ByOfficialName("Институт информационных технологий");
        Assert.Equal(("Директор ИИТ", (string?)null, "Институт информационных технологий"),
            (director.Position, director.Department, director.Institute));

        var rector = ByOfficialName("Управление");
        Assert.Equal(("Ректор", (string?)null, (string?)null), (rector.Position, rector.Department, rector.Institute));
    }

    [Theory]
    [InlineData(false, IssueSeverity.Error, "Сотрудник21 И. О.")]
    [InlineData(true, IssueSeverity.Warning, "Сотрудник22 И. О.")]
    public void Duplicate_official_name_keeps_first_row(bool samePerson, IssueSeverity severity, string keptName)
    {
        // Как после исправления опечатки «…и управление» → «…и управления»: у строк 22 и 23 одно официальное название.
        const string asoiu = "Автоматизированные системы обработки информации и управления";
        var input = Fixtures.Input() with { Employees = PositionsWithDuplicateRow(samePerson) };

        var result = new DataExtractor().Extract(input);

        Assert.Equal(36, result.Employees.Count);
        Assert.Equal(keptName, Assert.Single(result.Employees, e => e.OfficialName == asoiu).FullName);
        Assert.Contains(result.Issues, i =>
            i.Severity == severity && i is { Sheet: "Сотрудники", Row: 23 } && i.Message.Contains(asoiu) && i.Message.Contains("22"));
    }

    private static SourceFile PositionsWithDuplicateRow(bool samePerson)
    {
        using var workbook = new XLWorkbook(new MemoryStream(Fixtures.File(Fixtures.Positions).Content));
        var ws = workbook.Worksheet("Сотрудники");
        ws.Cell("B22").Value = ws.Cell("B23").GetString();
        if (samePerson) ws.Cell("E22").Value = ws.Cell("E23").GetString();
        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return new SourceFile("positions-duplicate.xlsx", output.ToArray());
    }

    [Fact]
    public void Missing_position_is_a_warning()
    {
        Assert.Null(ByOfficialName("Инженерная графика").Position);
        Assert.Contains(files.Result.Issues, i =>
            i is { Severity: IssueSeverity.Warning, File: Fixtures.Positions, Sheet: "Сотрудники", Row: 10 });
    }
}
