using DisciplineDataExtractor.Internal;
using Xunit;

namespace DisciplineDataExtractor.Tests;

/// <summary>Сопоставление строк матрицы с дисциплинами плана по похожести названия (этап 5.4, B7).</summary>
public class MatrixSimilarityTests
{
    private static Discipline D(string code, string name, params int[] semesters) =>
        new(code, name, PlanBlock.B1, PlanPart.Mandatory, EntryKind.Discipline, null, null, null, 3, 108,
            semesters.Select(s => new SemesterDetails(s, 3, 16, 0, 16, 40, 36, 32, [ControlType.Exam])).ToList());

    private static MatrixRow Row(string name, string semesters = "") =>
        new("Таблица 1", 5, name, semesters, [new CompetenceCode(CompetenceCategory.Universal, "УК-1", "УК-1.1")]);

    private static (IReadOnlyList<MatrixLink> Links, IReadOnlyList<UnmatchedMatrixRow> Unmatched, IssueLog Log) Resolve(
        MatrixRow row, params Discipline[] plan)
    {
        var log = new IssueLog();
        var (links, unmatched) = MatrixResolver.Resolve("МК.docx", [row], plan, [], log);
        return (links, unmatched, log);
    }

    [Fact]
    public void Word_endings_and_abbreviations_do_not_matter()
    {
        var (links, _, log) = Resolve(Row("Физика твёрдого тела"), D("Б1.О.20", "Физика твердых тел"));

        Assert.Equal("Б1.О.20", Assert.Single(links).DisciplineCode);
        Assert.Contains(log.Items, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("по похожести названия"));
        Assert.Equal(TextRules.NameKey("Безопасность, в том числе пожарная"), TextRules.NameKey("Безопасность, в т.ч. пожарная"));
    }

    [Fact]
    public void Different_disciplines_with_common_words_stay_unmatched()
    {
        // 2 общих основы из 5 — не похожи: строка остаётся для ручной привязки.
        var (links, unmatched, _) = Resolve(Row("Методы и средства обеспечения качества"), D("Б1.В.ДВ.02.01", "Основы обеспечения качества"));

        Assert.Empty(links);
        Assert.Single(unmatched);
    }

    [Fact]
    public void Equally_similar_candidates_are_told_apart_by_semesters()
    {
        var (links, _, _) = Resolve(Row("Иностранный язык", "3"),
            D("Б1.О.05", "Иностранные языки", 1, 2), D("Б1.В.07", "Иностранного языка", 3));

        Assert.Equal("Б1.В.07", Assert.Single(links).DisciplineCode);
    }
}
