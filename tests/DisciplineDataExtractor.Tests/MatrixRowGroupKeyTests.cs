using Xunit;

namespace DisciplineDataExtractor.Tests;

/// <summary>Ключ группы одинаковых строк матрицы без дисциплины плана (этап 7.1): название по правилу этапа 5.4 и семестры.</summary>
public class MatrixRowGroupKeyTests
{
    private const string Project = "Проект по дисциплине \"Машинное обучение и интеллектуальные системы\"";

    [Fact]
    public void Name_is_compared_the_way_matrix_is_matched_to_the_plan()
    {
        // Регистр, пробелы, «ё», вид кавычек и сокращение «в т.ч.» — не различие.
        Assert.Equal(
            UnmatchedMatrixRow.GroupKey(Project, "3"),
            UnmatchedMatrixRow.GroupKey("проект по  дисциплине «Машинное обучение и интеллектуальные системы»", "3"));
        Assert.Equal(
            UnmatchedMatrixRow.GroupKey("Безопасность, в т.ч. пожарная", "1"),
            UnmatchedMatrixRow.GroupKey("Безопасность, в том числе пожарная", "1"));
        Assert.NotEqual(
            UnmatchedMatrixRow.GroupKey(Project, "3"),
            UnmatchedMatrixRow.GroupKey("Проект по дисциплине \"Проектирование информационных систем\"", "3"));
    }

    [Fact]
    public void Semesters_are_a_set_whatever_the_separators()
    {
        Assert.Equal(UnmatchedMatrixRow.GroupKey(Project, "3, 4"), UnmatchedMatrixRow.GroupKey(Project, "4,3"));
        Assert.NotEqual(UnmatchedMatrixRow.GroupKey(Project, "3"), UnmatchedMatrixRow.GroupKey(Project, "4"));
        Assert.NotEqual(UnmatchedMatrixRow.GroupKey(Project, "3"), UnmatchedMatrixRow.GroupKey(Project, "3, 4"));
        // Семестров нет в обеих строках — тоже одинаковые.
        Assert.Equal(UnmatchedMatrixRow.GroupKey(Project, ""), UnmatchedMatrixRow.GroupKey(Project, null));
    }
}
