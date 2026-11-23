using RpdFos.Web.Models;
using Xunit;

namespace RpdFos.Api.Tests.Client;

/// <summary>Текст подтверждения «Принять» (этап 8, A1): по тому, найдены ли подписанты всех мест.</summary>
public class AcceptConfirmationTests
{
    [Fact]
    public void All_signatories_found_the_confirmation_says_the_document_goes_to_approval()
    {
        foreach (IReadOnlyList<string>? none in new IReadOnlyList<string>?[] { null, [] })
            Assert.Equal("Вы отправляете документ на согласование. Уверены?", AcceptConfirmation.Question(none, 0));
    }

    [Fact]
    public void Missing_signatory_the_confirmation_names_the_seats_and_says_the_document_will_only_be_accepted()
    {
        var text = AcceptConfirmation.Question(["Председатель УМК", "Начальник УМУ"], 0);

        Assert.Equal("Подписанты не найдены у мест: Председатель УМК; Начальник УМУ. " +
            "Документ станет «Принят», согласование начнётся после назначения согласующих. Уверены?", text);
        Assert.DoesNotContain("отправляете документ на согласование", text);
    }

    [Fact]
    public void Cross_check_warnings_come_first_in_both_texts()
    {
        Assert.StartsWith("Сквозная проверка РПД ↔ ФОС: расхождений 2. Приём они не блокируют. Вы отправляете", AcceptConfirmation.Question([], 2));
        Assert.StartsWith("Сквозная проверка РПД ↔ ФОС: расхождений 1. Приём они не блокируют. Подписанты не найдены", AcceptConfirmation.Question(["УМУ"], 1));
    }
}
