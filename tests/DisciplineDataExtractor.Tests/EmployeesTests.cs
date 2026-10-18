using System.Text.RegularExpressions;
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

    [Fact]
    public void Missing_position_is_a_warning()
    {
        Assert.Null(ByOfficialName("Инженерная графика").Position);
        Assert.Contains(files.Result.Issues, i =>
            i is { Severity: IssueSeverity.Warning, File: Fixtures.Positions, Sheet: "Сотрудники", Row: 10 });
    }
}
