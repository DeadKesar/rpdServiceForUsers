using RpdFos.Corpus;
using Xunit;

namespace RpdFos.Corpus.Tests;

/// <summary>Реестр номеров F01…: журнал и предложения автору ссылаются на номера, поэтому они не должны сдвигаться.</summary>
public class FosRegistryTests
{
    private static RegistryEntry E(string id, string name, string hash = "", bool present = true) => new(id, name, hash, present);

    private static IEnumerable<string> Show(IEnumerable<RegistryEntry> entries) =>
        entries.Select(e => $"{e.Id} {e.Name}{(e.Present ? "" : " (нет)")}");

    [Fact]
    public void New_files_get_next_numbers_and_old_numbers_stay_even_if_they_sort_earlier()
    {
        var previous = new[] { E("F01", "b.docx", "h2"), E("F02", "c.docx", "h3") };
        var result = FosRegistry.Assign([("a.docx", "h1"), ("b.docx", "h2"), ("c.docx", "h3"), ("+d.docx", "h4")], previous);
        // «+d» и «a» по алфавиту раньше «b», но прежние номера остаются; новые идут по порядку имён.
        Assert.Equal(["F01 b.docx", "F02 c.docx", "F03 +d.docx", "F04 a.docx"], Show(result));
    }

    [Fact]
    public void Missing_file_keeps_its_number_and_the_number_is_not_reused()
    {
        var previous = new[] { E("F01", "a.docx", "h1"), E("F02", "b.docx", "h2") };
        var result = FosRegistry.Assign([("a.docx", "h1"), ("z.docx", "h9")], previous);
        Assert.Equal(["F01 a.docx", "F02 b.docx (нет)", "F03 z.docx"], Show(result));
    }

    [Fact]
    public void Renamed_file_is_found_by_content()
    {
        var previous = new[] { E("F01", "old.docx", "h1") };
        var result = FosRegistry.Assign([("new.docx", "h1")], previous);
        var entry = Assert.Single(result);
        Assert.Equal(("F01", "new.docx", true), (entry.Id, entry.Name, entry.Present));
    }

    [Fact]
    public void Same_name_with_changed_content_keeps_the_number_and_takes_the_new_hash()
    {
        var result = FosRegistry.Assign([("a.docx", "h2")], [E("F05", "a.docx", "h1")]);
        var entry = Assert.Single(result);
        Assert.Equal(("F05", "h2"), (entry.Id, entry.Hash));
    }

    [Fact]
    public void Numbers_come_from_the_previous_report_by_masked_names_and_missing_files_stay_missing()
    {
        // Прежний Файлы.csv знает только скрытые имена; настоящие берутся из папки.
        var rows = new[] { ("F01", "Новый ФОС (‹ФИО›).pdf"), ("F02", "y.pdf"), ("F03", "gone.pdf") };
        var names = new[] { "Новый ФОС (Фамилия).pdf", "y.pdf", "new.pdf" };
        var previous = FosRegistry.FromReport(names, rows);
        Assert.Equal(["F01 Новый ФОС (Фамилия).pdf", "F02 y.pdf", "F03 gone.pdf (нет)"], Show(previous));

        var result = FosRegistry.Assign(names.Select(n => (n, "")).ToList(), previous);
        Assert.Equal("F04", result.Single(e => e.Name == "new.pdf").Id);
        Assert.False(result.Single(e => e.Id == "F03").Present);
    }

    [Fact]
    public void Files_with_the_same_masked_name_are_numbered_in_name_order()
    {
        var rows = new[] { ("F01", "a (‹ФИО›).pdf"), ("F02", "a (‹ФИО›).pdf") };
        var previous = FosRegistry.FromReport(["a (Первый).pdf", "a (Второй).pdf"], rows);
        Assert.Equal(["F01 a (Второй).pdf", "F02 a (Первый).pdf"], Show(previous));
    }

    [Fact]
    public void Exact_copies_point_to_the_file_with_the_lowest_number()
    {
        var copies = FosRegistry.Copies([E("F07", "a", "h1"), E("F03", "b", "h1"), E("F05", "c", "h2"), E("F09", "d", "h1")]);
        Assert.Equal(2, copies.Count);
        Assert.Equal("F03", copies["F07"]);
        Assert.Equal("F03", copies["F09"]);
    }

    [Fact]
    public void Registry_survives_a_round_trip_through_csv_with_semicolons_and_quotes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fos-registry-{Guid.NewGuid():N}.csv");
        try
        {
            var entries = new[] { E("F01", "ФОС; «Тест» \"кавычки\".docx", "0123456789abcdef"), E("F02", "ушёл.pdf", "", present: false) };
            FosRegistry.Write(path, entries);
            Assert.Equal(entries, FosRegistry.Read(path));
        }
        finally { File.Delete(path); }
    }
}

/// <summary>Рабочая программа в папке ФОС (пара к ФОС) не должна попадать в статистику ФОС.</summary>
public class FosDocKindTests
{
    private static FosDoc Doc(params string[] lines) =>
        new("T01", "t.docx", "docx", "исходный",
            lines.Select((text, i) => (FosBlock)new FosParagraph(i, text, false, false, false, null, 0, "", 0, 0, 0, false, false)).ToList(),
            [], 1, 0, 0, 1000, null);

    [Theory]
    [InlineData("РАБОЧАЯ ПРОГРАММА ДИСЦИПЛИНЫ", "РПД")]
    [InlineData("ФОНД ОЦЕНОЧНЫХ СРЕДСТВ", "ФОС")]
    [InlineData("Фонд оценочных средств", "ФОС")]
    [InlineData("Что-то другое", "?")]
    public void Title_decides_what_the_document_is(string title, string kind) =>
        Assert.Equal(kind, FosAnalyzer.Analyze(Doc("МИНОБРНАУКИ РОССИИ", "Кафедра", title, "Электротехника")).DocKind);

    [Fact]
    public void Working_program_and_exact_copies_stay_out_of_statistics()
    {
        var program = FosAnalyzer.Analyze(Doc("РАБОЧАЯ ПРОГРАММА ДИСЦИПЛИНЫ"));
        Assert.False(FosReports.InStatistics(program));
        var copy = FosAnalyzer.Analyze(Doc("ФОНД ОЦЕНОЧНЫХ СРЕДСТВ"));
        copy.DuplicateOf = "F01";
        Assert.False(FosReports.InStatistics(copy));
    }
}
