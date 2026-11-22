using RpdFos.Web.Models;
using Xunit;

namespace RpdFos.Api.Tests.Client;

/// <summary>
/// Выбор дисциплины в строках блока «Строки матрицы без дисциплины плана» (этап 7.1): держится за идентификатором
/// дисциплины. Замечание автора: выбрал Б1.В.01.04 в двух строках, применил первую — во второй показано другое название,
/// а привязывается Б1.В.01.04: порядок вариантов сменился, а браузер держал выбор по положению в списке.
/// </summary>
public sealed class MatrixRowChoicesTests
{
    private static readonly MatrixRowOptionItem Elective = new(Guid.NewGuid(), "Б1.В.ДВ.03.01", "Факультатив", false, false, true);
    private static readonly MatrixRowOptionItem Project = new(Guid.NewGuid(), "Б1.В.01.04", "Проект по дисциплине", false, false, true);
    private static readonly MatrixRowOptionItem Basics = new(Guid.NewGuid(), "Б1.О.01", "Основы", false, false, false);

    private static MatrixRowItem Row(Guid id, string status, params MatrixRowOptionItem[] options) =>
        new(id, "Строка", ["Таблица 1, строка 49"], "3", ["УК-1"], status, null, null, null, null, [.. options]);

    [Fact]
    public void Choice_follows_the_discipline_when_the_list_order_changes()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var choices = new MatrixRowChoices();
        var before = new[] { Row(first, "pending", Project, Elective, Basics), Row(second, "pending", Project, Elective, Basics) };
        choices.Reconcile(before);
        // Первая строка — Б1.В.ДВ.03.01, вторая — Б1.В.01.04.
        choices.Set(first, Elective.Id.ToString());
        choices.Set(second, Project.Id.ToString());

        // Первую строку привязали: у дисциплины появились связи, во второй строке она встала на первое место,
        // а выбранная там дисциплина сдвинулась на одно место вниз.
        var after = Row(second, "pending", Elective with { LinkedHere = true, WithoutMatrix = false }, Project, Basics);
        choices.Reconcile([Row(first, "linked", Elective), after]);

        Assert.Equal(Project.Id.ToString(), choices.Get(second));
        Assert.True(choices.TryGetTarget(second, out var target));
        Assert.Equal(Project.Id, target);
        // Положение в списке изменилось: на прежнем месте теперь другая дисциплина — по положению выбрана была бы она.
        var oldIndex = before[1].Options.FindIndex(o => o.Id == Project.Id);
        Assert.NotEqual(Project.Id, after.Options[oldIndex].Id);
        // Решённая строка выбора не держит.
        Assert.Equal("", choices.Get(first));
    }

    [Fact]
    public void Choice_in_a_row_is_dropped_when_it_cannot_be_applied()
    {
        var resolved = Guid.NewGuid();
        var vanished = Guid.NewGuid();
        var withoutOption = Guid.NewGuid();
        var ignored = Guid.NewGuid();
        var choices = new MatrixRowChoices();
        choices.Set(resolved, Project.Id.ToString());
        choices.Set(vanished, Project.Id.ToString());
        choices.Set(withoutOption, Project.Id.ToString());
        choices.Set(ignored, MatrixRowChoices.Ignore);

        choices.Reconcile([
            Row(resolved, "linked", Project), Row(withoutOption, "pending", Basics), Row(ignored, "pending", Basics)]);

        // Решённая строка и строка, где такой дисциплины нет среди вариантов, — без выбора; «Не учитывать» остаётся;
        // строки, которой больше нет, — тоже.
        Assert.Equal("", choices.Get(resolved));
        Assert.Equal("", choices.Get(withoutOption));
        Assert.Equal(MatrixRowChoices.Ignore, choices.Get(ignored));
        Assert.Equal("", choices.Get(vanished));
        Assert.False(choices.TryGetTarget(resolved, out _));
    }

    [Fact]
    public void Ignore_applies_without_a_discipline_and_nothing_chosen_applies_to_nothing()
    {
        var id = Guid.NewGuid();
        var choices = new MatrixRowChoices();
        Assert.False(choices.TryGetTarget(id, out _));

        choices.Set(id, MatrixRowChoices.Ignore);
        Assert.True(choices.TryGetTarget(id, out var target));
        Assert.Null(target);

        choices.Set(id, null);
        Assert.False(choices.TryGetTarget(id, out _));
    }
}
