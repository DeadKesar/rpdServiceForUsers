using RpdFos.Core.Documents;
using Xunit;

namespace RpdFos.Api.Tests.Documents;

/// <summary>
/// Текст отказа преподавателю, который создаёт документ по закреплённой дисциплине (этап 7, A10): фамилии не склоняются —
/// пол сотрудника системе неизвестен, поэтому «за сотрудником: Иванов И. И.», а не «за Ивановым»; названы не больше трёх.
/// </summary>
public class DisciplineAssignmentsTextTests
{
    private static AssignmentHolder Holder(string name) => new(Guid.NewGuid(), name);

    [Fact]
    public void A_single_holder_is_named_after_the_word_employee()
    {
        Assert.Equal(
            "Дисциплина закреплена за сотрудником: Иванов И. И. Документы по ней создают закреплённые преподаватели — обратитесь к методисту.",
            DisciplineAssignments.Refusal([Holder("Иванов И. И.")]));
    }

    [Fact]
    public void Several_holders_are_listed_after_the_plural()
    {
        Assert.Equal(
            "Дисциплина закреплена за сотрудниками: Иванов И. И., Петров П. П. Документы по ней создают закреплённые преподаватели — обратитесь к методисту.",
            DisciplineAssignments.Refusal([Holder("Иванов И. И."), Holder("Петров П. П.")]));
    }

    [Fact]
    public void More_than_three_holders_are_named_by_the_first_three_and_a_number()
    {
        AssignmentHolder[] holders = [Holder("А А. А."), Holder("Б Б. Б."), Holder("В В. В."), Holder("Г Г. Г."), Holder("Д Д. Д.")];

        Assert.Equal("А А. А., Б Б. Б., В В. В. и ещё 2", DisciplineAssignments.Names(holders));
        // «и ещё 2» не кончается точкой — её ставит отказ; фамилия с инициалами её уже имеет.
        Assert.StartsWith("Дисциплина закреплена за сотрудниками: А А. А., Б Б. Б., В В. В. и ещё 2. Документы по ней", DisciplineAssignments.Refusal(holders));
        Assert.Equal("А А. А., Б Б. Б., В В. В.", DisciplineAssignments.Names(holders.Take(3).ToList()));
    }
}
