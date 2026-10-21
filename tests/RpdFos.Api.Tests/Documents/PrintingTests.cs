using RpdFos.Core.DocumentTypes;
using RpdFos.Core.Printing;
using Xunit;

namespace RpdFos.Api.Tests.Documents;

/// <summary>Печать без базы (этап 5, B5): форматированный текст в абзацы, формы слов, имя файла.</summary>
public class PrintingTests
{
    [Fact]
    public void Rich_text_becomes_paragraphs_runs_and_lists()
    {
        var id = 0;
        var paragraphs = RichTextBlocks.Parse(
            "<p>Знания <strong>математики</strong> и <em>физики</em></p>текст вне абзаца<br>после переноса" +
            "<ul><li>Алгебра</li><li>Геометрия</li></ul><ol><li>Первый</li></ol>", () => ++id);

        Assert.Equal(["Знания математики и физики", "текст вне абзаца", "после переноса", "Алгебра", "Геометрия", "Первый"],
            paragraphs.Select(p => p.PlainText));
        Assert.Equal([false, true, false, false], paragraphs[0].Runs.Select(r => r.Bold));
        Assert.True(paragraphs[0].Runs[3].Italic);
        // Каждый список — со своим номером: нумерация второго списка начинается заново.
        Assert.Equal((PrintListKind.Bullet, 1), (paragraphs[3].List, paragraphs[3].ListId));
        Assert.Equal(paragraphs[3].ListId, paragraphs[4].ListId);
        Assert.Equal((PrintListKind.Numbered, 2), (paragraphs[5].List, paragraphs[5].ListId));
    }

    [Theory]
    [InlineData("Обязательная часть", WordForms.Dat, "обязательной части")]
    [InlineData("Часть, формируемая участниками образовательных отношений", WordForms.Dat,
        "части, формируемой участниками образовательных отношений")]
    [InlineData("Магистратура", WordForms.Gen, "магистратуры")]
    [InlineData("очная", WordForms.Ins, "очной")]
    [InlineData("магистр", WordForms.Acc, "магистра")]
    [InlineData("Зачёт с оценкой", WordForms.Prep, "зачёте с оценкой")]
    [InlineData("Любое значение", WordForms.Nom, "Любое значение")]
    public void Closed_sets_are_declined_by_dictionary(string value, string form, string expected) =>
        Assert.Equal(expected, Declension.Form(value, form));

    [Fact]
    public void Unknown_value_has_no_form() => Assert.Null(Declension.Form("Программная инженерия", WordForms.Gen));
}
