using Xunit;

namespace DisciplineDataExtractor.Tests;

/// <summary>Учебный план 09.04.04 «Программная инженерия», набор 2023 года.</summary>
public class PlanTests(RealFiles files) : IClassFixture<RealFiles>
{
    [Fact]
    public void Title_sheet_is_read()
    {
        var plan = files.Result.Plan!;

        Assert.Equal("09.04.04", plan.DirectionCode);
        Assert.Equal("Программная инженерия", plan.DirectionName);
        Assert.Equal("Технологии разработки интеллектуальных систем и программных комплексов", plan.Profile);
        Assert.Equal(EducationLevel.Master, plan.Level);
        Assert.Equal(StudyForm.FullTime, plan.StudyForm);
        Assert.Equal(2023, plan.Year);
        Assert.Equal("Институт информационных технологий", plan.Institute);
        Assert.Equal("Информационные технологии и вычислительные системы", plan.ResponsibleDepartment);
        Assert.Equal("932", plan.Fgos!.OrderNumber);
        Assert.Equal(new DateOnly(2017, 9, 19), plan.Fgos.ApprovedOn);
    }

    [Fact]
    public void Departments_sheet_is_read()
    {
        var departments = files.Result.Plan!.Departments;

        Assert.Equal(30, departments.Count);
        Assert.Contains(new DepartmentInfo("9", "ИТиВС", "Информационные технологии и вычислительные системы"), departments);
    }

    [Fact]
    public void All_plan_rows_are_read()
    {
        var d = files.Result.Disciplines;

        Assert.Equal(46, d.Count);
        Assert.Equal(38, d.Count(x => x.Block == PlanBlock.B1));
        Assert.Equal(5, d.Count(x => x.Block == PlanBlock.B2));
        Assert.Equal(1, d.Count(x => x.Block == PlanBlock.B3));
        Assert.Equal(2, d.Count(x => x.Block == PlanBlock.Ftd));
        Assert.Equal(3, d.Count(x => x.Part == PlanPart.ElectiveGroup));
        Assert.Equal(12, d.Count(x => x.Part == PlanPart.ElectiveItem));
        // Заголовки блоков и частей («ФТД.Факультативы», «Обязательная часть») — не дисциплины.
        Assert.DoesNotContain(d, x => x.Code.StartsWith("ФТД.Ф"));
    }

    [Fact]
    public void Discipline_hours_by_semester()
    {
        var d = files.Discipline("Б1.О.10");

        Assert.Equal("Математическое и компьютерное моделирование", d.Name);
        Assert.Equal((PlanBlock.B1, PlanPart.Mandatory, EntryKind.Discipline), (d.Block, d.Part, d.Kind));
        Assert.Equal(("12", "Прикладная математика"), (d.DepartmentCode, d.DepartmentName));
        Assert.Equal((3, 108), (d.Credits, d.TotalHours));

        var s = Assert.Single(d.Semesters);
        Assert.Equal(new SemesterDetails(1, Credits: 3, Lectures: 24, Labs: 16, Practice: 8, SelfStudy: 24,
            Control: 36, ContactHours: 48, ControlTypes: s.ControlTypes), s);
        Assert.Equal([ControlType.Exam], s.ControlTypes);
    }

    [Fact]
    public void Practice_spans_two_semesters()
    {
        var d = files.Discipline("Б2.О.03(П)");

        Assert.Equal((PlanBlock.B2, EntryKind.Practice), (d.Block, d.Kind));
        Assert.Equal([3, 4], d.Semesters.Select(s => s.Semester));
        Assert.Equal([5, 2], d.Semesters.Select(s => s.Credits));
        Assert.Equal([180, 72], d.Semesters.Select(s => s.SelfStudy));
        Assert.All(d.Semesters, s => Assert.Equal([ControlType.Credit], s.ControlTypes));
    }

    [Fact]
    public void Coursework_project_is_a_control_type()
    {
        var s = Assert.Single(files.Discipline("Б1.В.01").Semesters);

        Assert.Equal(2, s.Semester);
        Assert.Equal([ControlType.CourseworkProject], s.ControlTypes);
    }

    [Fact]
    public void Hierarchy_modules_and_electives()
    {
        Assert.Equal(EntryKind.Module, files.Discipline("Б1.О.14").Kind);
        Assert.Equal("Б1.О.14", files.Discipline("Б1.О.14.01").ParentCode);
        Assert.Equal(PlanPart.ElectiveGroup, files.Discipline("Б1.В.ДВ.02").Part);

        var item = files.Discipline("Б1.В.ДВ.02.03");
        Assert.Equal((PlanPart.ElectiveItem, "Б1.В.ДВ.02"), (item.Part, item.ParentCode));

        Assert.Equal((PlanPart.Mandatory, EntryKind.Gia), (files.Discipline("Б3.01(Д)").Part, files.Discipline("Б3.01(Д)").Kind));
        Assert.Equal((PlanPart.Variable, EntryKind.Optional), (files.Discipline("ФТД.В.01").Part, files.Discipline("ФТД.В.01").Kind));
        Assert.Null(files.Discipline("Б1.О.01").ParentCode);
    }
}
