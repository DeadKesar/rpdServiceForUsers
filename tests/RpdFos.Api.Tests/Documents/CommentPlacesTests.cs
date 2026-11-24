using System.Text.Json.Nodes;
using RpdFos.Core.Documents;
using Xunit;

namespace RpdFos.Api.Tests.Documents;

/// <summary>Места замечаний к записям списков после правки списка (этап 8, A14).</summary>
public class CommentPlacesTests
{
    private static JsonObject Section(params string[] cites) => new()
    {
        ["main"] = new JsonArray(cites.Select(c => (JsonNode)new JsonObject { ["citation"] = c, ["year"] = 2020 }).ToArray())
    };

    private static string Path(JsonObject before, JsonObject after, int index, string tail = "citation") =>
        CommentPlaces.Remap(before, after, $"/literature/main/{index}/{tail}").Path;

    [Theory]
    [InlineData(2, 0)] // третья поднята на первое место
    public void Moved_up_record_keeps_its_comment(int from, int to)
    {
        var before = Section("A", "B", "C");
        var after = Section("C", "A", "B");
        Assert.Equal($"/literature/main/{to}/citation", Path(before, after, from));
        Assert.Equal("/literature/main/1/citation", Path(before, after, 0));
    }

    [Fact]
    public void Deletion_above_shifts_and_deletion_of_the_record_is_flagged()
    {
        var before = Section("A", "B", "C");
        Assert.Equal("/literature/main/1/citation", Path(before, Section("B", "C"), 2));

        var gone = CommentPlaces.Remap(before, Section("A", "B"), "/literature/main/2/citation");
        Assert.True(gone.RecordDeleted);
        Assert.Equal("/literature/main/2/citation", gone.Path);
        Assert.False(CommentPlaces.Remap(before, Section("A", "B"), "/literature/main/1/citation").RecordDeleted);
    }

    [Fact]
    public void Insertion_shifts_following_records()
    {
        Assert.Equal("/literature/main/3/citation", Path(Section("A", "B", "C"), Section("A", "N", "B", "C"), 2));
    }

    [Fact]
    public void Edited_record_keeps_its_comment_even_when_moved_and_changed_in_one_save()
    {
        var before = Section("A", "B", "C");
        // Правка текста на месте.
        Assert.Equal("/literature/main/1/citation", Path(before, Section("A", "B, исправлено", "C"), 1));
        // Часть значений совпала (год), запись сдвинута и изменена.
        var after = Section("B", "C", "A, исправлено");
        Assert.Equal("/literature/main/2/citation", Path(before, after, 0));
        // Изменено всё — по порядку, пока несовпавших поровну.
        var all = new JsonObject { ["main"] = new JsonArray(new JsonObject { ["citation"] = "X", ["year"] = 1 }, before["main"]![0]!.DeepClone()) };
        var was = new JsonObject { ["main"] = new JsonArray(new JsonObject { ["citation"] = "Y", ["year"] = 2 }, before["main"]![0]!.DeepClone()) };
        Assert.Equal("/literature/main/0/citation", Path(was, all, 0));
    }

    [Fact]
    public void Edit_with_deletion_in_one_save_is_not_guessed()
    {
        // Несовпавших записей поровну нет: изменённая запись остаётся на прежнем месте, чужая — удалена.
        var before = new JsonObject { ["main"] = new JsonArray(new JsonObject { ["citation"] = "A", ["year"] = 1 },
            new JsonObject { ["citation"] = "B", ["year"] = 2 }, new JsonObject { ["citation"] = "C", ["year"] = 3 }) };
        var after = new JsonObject { ["main"] = new JsonArray(new JsonObject { ["citation"] = "A", ["year"] = 1 },
            new JsonObject { ["citation"] = "новое", ["year"] = 9 }) };
        Assert.False(CommentPlaces.Remap(before, after, "/literature/main/1/citation").RecordDeleted);
        Assert.True(CommentPlaces.Remap(before, after, "/literature/main/2/citation").RecordDeleted);
    }

    [Fact]
    public void Nested_lists_follow_both_levels()
    {
        JsonObject Topic(string name, params string[] classes) => new()
        {
            ["name"] = name, ["classes"] = new JsonArray(classes.Select(c => (JsonNode)new JsonObject { ["title"] = c }).ToArray())
        };
        var before = new JsonObject { ["topics"] = new JsonArray(Topic("T1", "a", "b"), Topic("T2", "c", "d")) };
        var after = new JsonObject { ["topics"] = new JsonArray(Topic("T2", "d", "c"), Topic("T1", "a", "b")) };
        Assert.Equal("/s/topics/0/classes/1/title", CommentPlaces.Remap(before, after, "/s/topics/1/classes/0/title").Path);
        Assert.Equal("/s/topics/1/classes/1/title", CommentPlaces.Remap(before, after, "/s/topics/0/classes/1/title").Path);

        var noClass = new JsonObject { ["topics"] = new JsonArray(Topic("T2", "d", "c"), Topic("T1", "a")) };
        Assert.True(CommentPlaces.Remap(before, noClass, "/s/topics/0/classes/1/title").RecordDeleted);
        var noTopic = new JsonObject { ["topics"] = new JsonArray(Topic("T1", "a", "b")) };
        Assert.True(CommentPlaces.Remap(before, noTopic, "/s/topics/1/classes/0/title").RecordDeleted);
    }

    [Fact]
    public void Paths_without_records_and_vanished_lists_are_handled()
    {
        var before = new JsonObject { ["goals"] = "текст", ["list"] = new JsonArray("a", "b") };
        Assert.Equal(("/s/goals", false), Unpack(CommentPlaces.Remap(before, new JsonObject(), "/s/goals")));
        Assert.Equal(("/s/list", false), Unpack(CommentPlaces.Remap(before, new JsonObject(), "/s/list")));
        // Список исчез — записей нет.
        Assert.Equal(("/s/list/1", true), Unpack(CommentPlaces.Remap(before, new JsonObject(), "/s/list/1")));
        Assert.Equal(("/s/list/1", true), Unpack(CommentPlaces.Remap(before, null, "/s/list/1")));
        // Номер за пределами списка (замечание к ещё не заполненному месту) не меняется.
        Assert.Equal(("/s/list/5/x", false), Unpack(CommentPlaces.Remap(before, before, "/s/list/5/x")));
        // Строки списка: замечание к самой записи.
        Assert.Equal(("/s/list/0", false), Unpack(CommentPlaces.Remap(before, new JsonObject { ["list"] = new JsonArray("b", "a") }, "/s/list/1")));
    }

    private static (string, bool) Unpack(CommentPlaces.Place p) => (p.Path, p.RecordDeleted);
}
