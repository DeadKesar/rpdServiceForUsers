using System.Reflection;
using System.Text.Json;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Mail;
using Xunit;

namespace RpdFos.Api.Tests.Mail;

/// <summary>Письмо по уведомлению и перечень событий с умолчаниями почты (этап 7, A4) — без базы и сервера.</summary>
public class NotificationLetterTests
{
    private static readonly Guid DocumentId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static Notification Note(object payload, Guid? documentId = null) => new()
    {
        EventType = NotificationEvents.ReturnedForRework, DocumentId = documentId, Payload = JsonSerializer.Serialize(payload)
    };

    [Fact]
    public void Subject_and_text_come_from_the_payload_and_the_link_leads_to_the_document()
    {
        var letter = NotificationLetter.Compose(
            Note(new { text = "Возвращён на доработку: РПД «Модели». Замечаний: 2.", subject = "Возвращён на доработку: РПД «Модели»" }, DocumentId),
            "teacher@test.local", "Иванов И. И.", "https://rpdfos.test/");

        Assert.Equal(("teacher@test.local", "Иванов И. И.", "Возвращён на доработку: РПД «Модели»"), (letter.To, letter.ToName, letter.Subject));
        Assert.StartsWith("Возвращён на доработку: РПД «Модели». Замечаний: 2.\n\n", letter.Body);
        // Косая черта в конце настроенного адреса не удваивается.
        Assert.Contains($"Открыть документ: https://rpdfos.test/documents/{DocumentId}\n", letter.Body);
        Assert.EndsWith("Автоматическое сообщение системы создания РПД и ФОС.", letter.Body);
    }

    [Fact]
    public void There_is_no_link_without_a_document_or_without_the_address_of_the_system()
    {
        var payload = new { text = "Согласующий заменён: РПД «Модели».", subject = "Согласующий заменён: РПД «Модели»" };

        // Уведомление без ссылки (документ получателю недоступен, этап 7, A3) — и письмо без ссылки.
        Assert.DoesNotContain("documents/", NotificationLetter.Compose(Note(payload), "a@test.local", null, "https://rpdfos.test").Body);
        // Адрес системы не настроен — письмо уходит без ссылки, а не со ссылкой в никуда.
        Assert.DoesNotContain("documents/", NotificationLetter.Compose(Note(payload, DocumentId), "a@test.local", null, "  ").Body);
    }

    [Fact]
    public void A_notification_without_a_subject_takes_it_from_the_beginning_of_the_text()
    {
        // Уведомления, созданные до появления темы в payload.
        var letter = NotificationLetter.Compose(Note(new { text = "Принят: РПД «Модели». Согласование начато. Методист: Петров П. П." }),
            "a@test.local", null, "");

        Assert.Equal("Принят: РПД «Модели». Согласование начато. Методист: Петров П. П.", letter.Subject);
        Assert.Null(letter.ToName);
    }

    [Fact]
    public void The_subject_is_one_line_and_short_enough_to_read_in_a_list_of_letters()
    {
        var multiline = NotificationLetter.Compose(Note(new { text = "Текст", subject = "Первая\r\nвторая   строка\n" }), "a@test.local", null, "");
        Assert.Equal("Первая вторая строка", multiline.Subject);

        var long_ = NotificationLetter.Compose(Note(new { text = "Текст", subject = new string('я', 400) }), "a@test.local", null, "");
        Assert.Equal(150, long_.Subject.Length);
        Assert.EndsWith("…", long_.Subject);

        // Нет ни темы, ни текста — письмо всё равно получает тему.
        Assert.Equal("Уведомление системы создания РПД и ФОС", NotificationLetter.Compose(Note(new { }), "a@test.local", null, "").Subject);
    }

    [Fact]
    public void Every_event_is_in_the_list_with_a_default_and_only_events_that_require_action_are_mailed_at_once()
    {
        // Все константы NotificationEvents — в перечне, без повторов: новое событие (A6 — напоминания) нельзя забыть добавить.
        var constants = typeof(NotificationEvents).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.NotEmpty(constants);
        Assert.Equal(constants.Order(), NotificationEvents.All.Select(e => e.Type).Order());

        Assert.Equal(
            new[]
            {
                NotificationEvents.ReturnedForRework, NotificationEvents.ReturnedAfterPlanCorrection,
                NotificationEvents.AwaitingMark, NotificationEvents.ReturnedFromApproval
            }.Order(),
            NotificationEvents.All.Where(e => e.DefaultMail == MailMode.Immediately).Select(e => e.Type).Order());

        Assert.Equal(MailMode.Immediately, NotificationEvents.DefaultMailOf(NotificationEvents.AwaitingMark));
        Assert.Equal(MailMode.Off, NotificationEvents.DefaultMailOf(NotificationEvents.Approved));
        Assert.Equal(MailMode.Off, NotificationEvents.DefaultMailOf("неизвестное.событие"));
    }
}
