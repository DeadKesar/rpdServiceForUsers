using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Options;
using RpdFos.Core.Mail;
using Xunit;

namespace RpdFos.Api.Tests.Mail;

/// <summary>
/// Почтовая ловушка для проверки настоящего отправителя. Образ — тот же, что в docker-compose.yml (`mail`): письмо уходит по
/// SMTP, как в разработке, и читается из REST API ловушки. Контейнер один на класс тестов.
/// </summary>
public sealed class MailpitFixture : IAsyncLifetime
{
    private const int SmtpPort = 1025, HttpPort = 8025;

    private readonly IContainer _container = new ContainerBuilder("axllent/mailpit:v1.33.1")
        .WithPortBinding(SmtpPort, true)
        .WithPortBinding(HttpPort, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(HttpPort).ForPath("/livez")))
        .Build();

    public string Host => _container.Hostname;

    public int Smtp => _container.GetMappedPublicPort(SmtpPort);

    public Uri Api => new($"http://{_container.Hostname}:{_container.GetMappedPublicPort(HttpPort)}/");

    public Task InitializeAsync() => _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

/// <summary>Отправитель по SMTP (этап 7, A4) на настоящем SMTP-сервере: кириллица в теме, имени и тексте, письмо без имени.</summary>
public class SmtpMailSenderTests(MailpitFixture mailpit) : IClassFixture<MailpitFixture>
{
    [Fact]
    public async Task A_letter_reaches_the_server_with_the_cyrillic_subject_the_name_of_the_recipient_and_the_text()
    {
        var subject = $"Возвращён на доработку: РПД «Моделирование систем» {Guid.NewGuid():N}";
        var body = "Возвращён на доработку. Замечаний: 2.\n\nОткрыть документ: https://rpdfos.test/documents/1";

        await Sender(mailpit.Host, mailpit.Smtp).SendAsync(new OutgoingMail("teacher@rpdfos.test", "Иванов И. И.", subject, body));

        var message = await ReadAsync(subject);
        Assert.Equal(("АС РПД и ФОС", "rpdfos@rpdfos.test"),
            (message.GetProperty("From").GetProperty("Name").GetString(), message.GetProperty("From").GetProperty("Address").GetString()));
        var to = Assert.Single(message.GetProperty("To").EnumerateArray());
        Assert.Equal(("Иванов И. И.", "teacher@rpdfos.test"), (to.GetProperty("Name").GetString(), to.GetProperty("Address").GetString()));
        Assert.Equal(body, message.GetProperty("Text").GetString()!.Replace("\r\n", "\n").TrimEnd('\n'));
    }

    [Fact]
    public async Task A_letter_without_the_name_of_the_recipient_goes_to_the_bare_address()
    {
        var subject = $"Без имени {Guid.NewGuid():N}";

        await Sender(mailpit.Host, mailpit.Smtp).SendAsync(new OutgoingMail("approver@rpdfos.test", null, subject, "Текст."));

        var to = Assert.Single((await ReadAsync(subject)).GetProperty("To").EnumerateArray());
        Assert.Equal(("", "approver@rpdfos.test"), (to.GetProperty("Name").GetString(), to.GetProperty("Address").GetString()));
    }

    [Fact]
    public async Task A_server_that_does_not_answer_is_reported_as_unavailable()
    {
        // Порт, на котором никто не слушает: занять и сразу освободить.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        await Assert.ThrowsAsync<MailServerUnavailableException>(() =>
            Sender("127.0.0.1", port).SendAsync(new OutgoingMail("a@rpdfos.test", null, "Тема", "Текст.")));
    }

    private static SmtpMailSender Sender(string host, int port) => new(Options.Create(new MailSettings
    {
        Host = host, Port = port, From = "АС РПД и ФОС <rpdfos@rpdfos.test>"
    }));

    // Письмо по теме — из REST API ловушки. Тема уникальна, поэтому находится одно письмо.
    private async Task<JsonElement> ReadAsync(string subject)
    {
        using var http = new HttpClient { BaseAddress = mailpit.Api };
        var list = await http.GetFromJsonAsync<JsonElement>("api/v1/messages?limit=100");
        var id = list.GetProperty("messages").EnumerateArray()
            .Single(m => m.GetProperty("Subject").GetString() == subject).GetProperty("ID").GetString();
        return await http.GetFromJsonAsync<JsonElement>($"api/v1/message/{id}");
    }
}
