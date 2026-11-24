using System.Text.Json.Nodes;
using RpdFos.Core.Documents;
using RpdFos.Web.Forms;
using Xunit;

namespace RpdFos.Api.Tests.Client;

/// <summary>
/// Записи, к которым методист оставляет замечание из формы у списка (этап 8, A15): место и подпись каждой, вложенные списки,
/// и что место — то же, по которому замечание следует за записью (<see cref="CommentPlaces"/>, A14).
/// </summary>
public sealed class CommentTargetsTests
{
    private static JsonObject Json(string text) => JsonNode.Parse(text)!.AsObject();

    [Fact]
    public void Records_of_a_list_are_offered_with_their_numbers_and_titles()
    {
        var payload = Json("""
            { "literature": { "main": [ { "isbn": "978-5", "year": 2024, "citation": "Иванов И. И. Сети" }, { "year": 2023 } ],
                              "software": [ "Браузер", "LibreOffice" ] } }
            """);

        Assert.Equal(new[] { ("/literature/main/0", "№ 1 — Иванов И. И. Сети"), ("/literature/main/1", "№ 2") },
            CommentTargets.Records(payload, "/literature/main").Select(t => (t.Path, t.Title)));
        Assert.Equal(new[] { ("/literature/software/0", "№ 1 — Браузер"), ("/literature/software/1", "№ 2 — LibreOffice") },
            CommentTargets.Records(payload, "/literature/software").Select(t => (t.Path, t.Title)));
        // Поля и отсутствующие списки выбирать не дают.
        Assert.Empty(CommentTargets.Records(payload, "/literature/main/0/citation"));
        Assert.Empty(CommentTargets.Records(payload, "/thematic_plan"));
    }

    [Fact]
    public void Tasks_of_a_bank_group_are_offered_under_the_group_without_markup_and_codes()
    {
        var payload = Json("""
            { "task_bank": [ { "group": "Вопросы", "tasks": [ { "number": 1, "text": "<p>Опишите&nbsp;<b>HTTP</b></p>", "indicator_codes": ["УК-6.1"] } ] } ] }
            """);

        Assert.Equal(new[] { ("/task_bank/0", "№ 1 — Вопросы"), ("/task_bank/0/tasks/0", "№ 1 → № 1 — Опишите HTTP") },
            CommentTargets.Records(payload, "/task_bank").Select(t => (t.Path, t.Title)));
    }

    [Fact]
    public void A_chosen_record_keeps_its_place_after_the_list_is_reordered()
    {
        var payload = Json("""{ "literature": { "main": [ { "citation": "Первая" }, { "citation": "Вторая" }, { "citation": "Третья" } ] } }""");
        var before = payload["literature"]!.DeepClone();
        var chosen = CommentTargets.Records(payload, "/literature/main")[2];

        var list = payload["literature"]!["main"]!.AsArray();
        list.Remove(chosen.Node);
        list.Insert(0, chosen.Node);

        var now = CommentTargets.Records(payload, "/literature/main").Single(t => t.Node == chosen.Node);
        Assert.Equal("/literature/main/0", now.Path);
        Assert.Equal(new CommentPlaces.Place(now.Path, false), CommentPlaces.Remap(before, payload["literature"], chosen.Path));
    }
}
