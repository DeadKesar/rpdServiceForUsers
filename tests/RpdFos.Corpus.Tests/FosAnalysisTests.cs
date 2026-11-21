using RpdFos.Corpus;
using Xunit;
using static RpdFos.Corpus.FosHeuristics;

namespace RpdFos.Corpus.Tests;

/// <summary>Правила разбора настоящих ФОС (шаг B1): отчёты строятся по ним, поэтому правила закреплены тестами.</summary>
public class FosHeuristicsTests
{
    [Theory]
    [InlineData("Составитель Фамилия И. О., доцент")]
    [InlineData("Зав. кафедрой Образцов А.Б.")]
    [InlineData("И. О. Фамилия, профессор")]
    [InlineData("Фамилиев Имя Отчествович")]
    public void Names_are_masked_in_reports(string text)
    {
        var masked = Mask(text);
        Assert.Contains("‹ФИО›", masked);
        Assert.DoesNotContain("Фамилия", masked);
        Assert.DoesNotContain("Образцов", masked);
        Assert.DoesNotContain("Отчествович", masked);
    }

    [Fact]
    public void Surname_in_file_name_brackets_is_masked()
    {
        var masked = MaskFileName("Новый ФОС 09.03.03 УД Администрирование в информационных системах (Фамилия) (1).pdf");
        Assert.DoesNotContain("Фамилия", masked);
        Assert.Contains("(‹ФИО›)", masked);
    }

    [Theory]
    [InlineData("МИНОБРНАУКИ РОССИИ", false)]
    [InlineData("федеральное государственное бюджетное образовательное учреждение", false)]
    [InlineData("(ФГБОУ ВО «МГТУ «СТАНКИН»)", false)]
    [InlineData("по дисциплине «Прикладная физическая культура»", false)]
    [InlineData("ТЕМЫ РЕФЕРАТОВ", true)]
    [InlineData("Это обычное предложение из задания, оно не заголовок.", false)]
    public void Boilerplate_and_plain_text_are_not_headings(string text, bool expected) =>
        Assert.Equal(expected, LooksLikeHeading(text, styleHeading: false, bold: false, centered: false));

    [Fact]
    public void Bold_short_line_is_a_heading()
    {
        Assert.True(LooksLikeHeading("Вопросы для подготовки к зачету", false, bold: true, false));
        Assert.False(LooksLikeHeading("Вопросы для подготовки к зачету", false, bold: false, false));
    }

    [Fact]
    public void Competence_codes_are_found_and_standards_are_skipped()
    {
        var codes = Codes("ОК-8, ПК-1.2 и ГОСТ-12, а также ПСК-3").ToList();
        Assert.Equal(["ОК-8", "ПК-1.2", "ПСК-3"], codes.Select(c => c.Full));
        Assert.Equal([false, true, false], codes.Select(c => c.Indicator));
    }

    [Theory]
    [InlineData("Вопросы для подготовки к зачету", "intermediate")]
    [InlineData("Темы эссе по разделу «Шахматы» (1 семестр)", "essay")]
    [InlineData("по разделу «Шашки» (3 семестр)", "group")]
    [InlineData("Экзаменационный билет № 5", "tickets")]
    [InlineData("Паспорт фонда оценочных средств по дисциплине", "passport")]
    [InlineData("Лабораторная работа №2", "bank")]
    [InlineData("Раздел 2. Линейные резистивные цепи и методы их анализа", "group")]
    [InlineData("Уровень обучаемости «ЗНАТЬ»", "group")]
    [InlineData("Критерии формирования оценок", "scale")]
    public void Headings_get_the_expected_kind(string heading, string kind) =>
        Assert.Equal(kind, MatchKinds(heading).First().Id);
}

public class FosAnalyzerTests
{
    private static FosParagraph P(int index, string text, bool heading = false) =>
        new(index, text, heading, heading, false, null, 0, "", 0, 0, 0, false, false);

    private static FosDoc Doc(IEnumerable<string> lines, params int[] headings)
    {
        var blocks = lines.Select((text, i) => (FosBlock)P(i, text, headings.Contains(i))).ToList();
        return new FosDoc("T01", "t.docx", "docx", "исходный", blocks, [], 1, 0, 0, 1000, null);
    }

    [Fact]
    public void Numbering_restarts_in_every_group()
    {
        var a = FosAnalyzer.Analyze(Doc(
            ["Вопросы к зачёту", "1. Первый", "2. Второй", "3. Третий", "Темы эссе", "1. Тема", "2. Тема", "3. Тема", "4. Тема"], 0, 4));
        Assert.Equal("по группам, каждая с 1", FosReports.NumberingKind(a));
        Assert.Equal([3, 4], a.Runs.Select(r => r.Length));
        Assert.DoesNotContain(a.NoSlot, n => n.Key.Contains("сквозная"));
    }

    [Fact]
    public void Numbering_continuing_across_a_heading_is_reported()
    {
        var a = FosAnalyzer.Analyze(Doc(
            ["Вопросы к зачёту", "1. Первый", "2. Второй", "3. Третий", "Раздел 2", "4. Четвёртый", "5. Пятый", "6. Шестой"], 0, 4));
        var run = Assert.Single(a.Runs);
        Assert.Equal(6, run.Length);
        Assert.Equal(1, run.HeadingsInside);
        Assert.Contains(a.NoSlot, n => n.Key.Contains("сквозная"));
    }

    [Fact]
    public void Labeled_numbers_without_delimiter_count_as_tasks_and_lms_tests_are_recognised()
    {
        var a = FosAnalyzer.Analyze(Doc([
            "Тестовые вопросы", "Вопрос 1 (МВ) Какая команда монтирует файловую систему", "Ответ 1 (0) chmod", "Ответ 2 (100) mount",
            "Вопрос 2 (КО) Какая команда создаёт каталог", "Ответ (100) mkdir", "Вопрос 3 (КО) Какая команда удаляет файл", "Ответ (100) rm"], 0));
        Assert.Equal(3, a.LmsQuestions);
        Assert.Equal(["КО", "МВ"], a.LmsTypes.Order());
        Assert.Equal(4, a.LmsAnswers);
        Assert.Equal(3, a.TypedItems);
        Assert.Contains(a.NoSlot, n => n.Key.StartsWith("тесты в формате системы тестирования"));
    }

    [Fact]
    public void Tickets_are_counted_with_their_questions()
    {
        var a = FosAnalyzer.Analyze(Doc([
            "Билет № 1", "1. Вопрос", "2. Вопрос", "Билет № 2", "1. Вопрос", "2. Вопрос", "3. Вопрос", "Билет № 3", "1. Вопрос"]));
        Assert.Equal(3, a.Tickets);
        Assert.Equal(1, a.TicketQuestions[2]);
        Assert.Equal(1, a.TicketQuestions[3]);
        Assert.Equal(1, a.TicketQuestions[1]);
        Assert.Contains(a.NoSlot, n => n.Key.StartsWith("билеты не с двумя-тремя вопросами"));
    }

    [Theory]
    [InlineData("Индикатор ПК-1.2 достигнут", "3++")]
    [InlineData("Компетенция ОК-8", "3+")]
    [InlineData("Текст без кодов", "?")]
    public void Standard_follows_the_codes(string line, string standard) =>
        Assert.Equal(standard, FosAnalyzer.Analyze(Doc([line])).Standard);

    [Fact]
    public void Passport_table_is_recognised_with_ranges_instead_of_task_numbers_in_names()
    {
        var table = new FosTable(1, [
            ["№ п/п", "Контролируемые разделы дисциплины", "Контролируемые компетенции", "Проверочные задания"],
            ["1", "Шахматы", "УК-7", "Вопросы к зачету 1-30"],
            ["2", "Шашки", "УК-7", "Вопросы к зачету 1-37"]], 0, 0, 0, 0, 0);
        var a = FosAnalyzer.Analyze(new FosDoc("T01", "t.docx", "docx", "исходный", [P(0, "Паспорт", heading: true), table], [], 1, 0, 0, 1000, null));
        Assert.NotNull(a.Passport);
        Assert.Equal(2, a.Passport!.Rows);
        Assert.True(a.Passport.TaskNumbers);
        Assert.Equal(["УК-7"], a.Passport.CompetenceCodes);
    }

    [Fact]
    public void Passport_without_task_numbers_is_reported_as_gap()
    {
        var table = new FosTable(1, [
            ["№ п/п", "Контролируемые разделы дисциплины", "Контролируемые компетенции", "Проверочные задания"],
            ["1", "Практический раздел", "ОК-8", "Вопросы для подготовки к зачету"]], 0, 0, 0, 0, 0);
        var a = FosAnalyzer.Analyze(new FosDoc("T01", "t.docx", "docx", "исходный", [table], [], 1, 0, 0, 1000, null));
        Assert.False(a.Passport!.TaskNumbers);
        Assert.Contains(a.NoSlot, n => n.Key.StartsWith("паспорт ссылается на оценочные средства названием"));
        Assert.Contains(a.NoSlot, n => n.Key.StartsWith("коды компетенций с префиксом вне шаблона"));
    }
}

/// <summary>Файл оформления ФОС из tests/fixtures: факты, на которые опираются предложения автору в журнале.</summary>
public class FosTemplateTests
{
    private static readonly string Path_ = System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", "FOS_TemplateBookmarks.docx");

    [Fact]
    public void Template_has_46_bookmarks_and_examining_covers_the_whole_ticket_appendix()
    {
        var doc = FosReader.Read("T", Path_, null);
        Assert.Null(doc.Error);
        Assert.Equal(46, doc.Bookmarks.Count);
        var examining = Assert.Single(doc.Bookmarks, b => b.Name == "AutofillExaming1");
        // Закладка охватывает приложение с билетом целиком (десятки блоков), а не слово в заголовке.
        Assert.True(examining.EndBlock - examining.StartBlock >= 20);
    }

    [Fact]
    public void Template_passport_has_two_tables_and_only_one_is_bookmarked()
    {
        var doc = FosReader.Read("T", Path_, null);
        var passports = doc.Tables.Where(t => t.Rows[0].Any(c => c.Contains("Контролируемые разделы")) || t.Rows[0].Any(c => c.Contains("индикатор"))).ToList();
        Assert.True(passports.Count >= 2);
        var uncovered = passports.Count(t => !doc.Bookmarks.Any(b => b.StartBlock <= t.Index && b.EndBlock >= t.Index));
        Assert.True(uncovered >= 1);
    }
}
