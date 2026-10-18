using Xunit;

namespace DisciplineDataExtractor.Tests;

/// <summary>Список компетенций 09.04.04: УК (с категориями), ОПК, ПК с индикаторами.</summary>
public class CompetenceTests(RealFiles files) : IClassFixture<RealFiles>
{
    [Fact]
    public void All_competences_and_indicators_are_read()
    {
        var c = files.Result.Competences;

        Assert.Equal(17, c.Count);
        Assert.Equal(6, c.Count(x => x.Category == CompetenceCategory.Universal));
        Assert.Equal(8, c.Count(x => x.Category == CompetenceCategory.GeneralProfessional));
        Assert.Equal(3, c.Count(x => x.Category == CompetenceCategory.Professional));
        Assert.Equal(57, c.Sum(x => x.Indicators.Count));
        Assert.All(c.SelectMany(x => x.Indicators), i => Assert.NotNull(i.Type));
    }

    [Fact]
    public void Universal_competence_with_category_and_indicator_types()
    {
        var uk1 = files.Competence("УК-1");

        Assert.Equal("Системное и критическое мышление", uk1.CategoryName);
        Assert.StartsWith("Способен осуществлять критический анализ проблемных ситуаций", uk1.Formulation);
        Assert.Equal(["УК-1.1", "УК-1.2", "УК-1.3"], uk1.Indicators.Select(i => i.Code));
        Assert.Equal([IndicatorType.Knows, IndicatorType.Can, IndicatorType.Owns], uk1.Indicators.Select(i => i.Type));
        Assert.StartsWith("Знать: процедуры критического анализа", uk1.Indicators[0].Text);
    }

    [Fact]
    public void Text_split_across_paragraphs_is_joined()
    {
        // Категория УК-6 и индикатор УК-2.3 в документе разбиты на несколько абзацев.
        Assert.Equal("Самоорганизация и саморазвитие (в том числе здоровьесбережение)", files.Competence("УК-6").CategoryName);
        Assert.EndsWith("методами оценки эффективности проекта, а также потребности в ресурсах",
            files.Competence("УК-2").Indicators.Single(i => i.Code == "УК-2.3").Text);
    }

    [Fact]
    public void Professional_competence_with_three_level_indicator_codes()
    {
        var pk1 = files.Competence("ПК-1");

        Assert.Null(pk1.CategoryName);
        Assert.Equal(["ПК-1.1.1", "ПК-1.1.2", "ПК-1.2", "ПК-1.3"], pk1.Indicators.Select(i => i.Code));
        Assert.Equal(IndicatorType.Owns, pk1.Indicators.Single(i => i.Code == "ПК-1.3").Type); // «Демонстрирует навыки»
        Assert.Equal(IndicatorType.Owns, files.Competence("ОПК-1").Indicators.Single(i => i.Code == "ОПК-1.3").Type); // «Иметь навыки»
    }

    [Fact]
    public void Formulation_has_no_list_punctuation()
    {
        Assert.All(files.Result.Competences, c => Assert.False(c.Formulation.EndsWith(';'), c.Code));
        Assert.EndsWith("в междисциплинарном контексте", files.Competence("ОПК-1").Formulation);
    }
}
