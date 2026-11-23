using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using MailKit.Security;
using Microsoft.Extensions.Options;
using RpdFos.Core.Mail;
using Xunit;

namespace RpdFos.Api.Tests.Mail;

/// <summary>
/// Почтовая ловушка для проверки настоящего отправителя. Образ — тот же, что в docker-compose.yml (`mail`): письмо уходит по
/// SMTP, как в разработке, и читается из REST API ловушки. Контейнер один на класс тестов.
/// </summary>
public class MailpitFixture : IAsyncLifetime
{
    private const int SmtpPort = 1025, HttpPort = 8025;

    private readonly IContainer _container;

    public MailpitFixture() : this([]) { }

    // Настройки ловушки — переменные окружения Mailpit (MP_…).
    protected MailpitFixture(Dictionary<string, string> environment)
    {
        var builder = new ContainerBuilder("axllent/mailpit:v1.33.1")
            .WithPortBinding(SmtpPort, true)
            .WithPortBinding(HttpPort, true)
            .WithEnvironment("MP_SMTP_DISABLE_RDNS", "true") // как в docker-compose.yml
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(HttpPort).ForPath("/livez")));
        foreach (var (name, value) in environment) builder = builder.WithEnvironment(name, value);
        _container = builder.Build();
    }

    public string Host => _container.Hostname;

    public int Smtp => _container.GetMappedPublicPort(SmtpPort);

    public Uri Api => new($"http://{_container.Hostname}:{_container.GetMappedPublicPort(HttpPort)}/");

    public Task InitializeAsync() => _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();

    // Письмо по теме — из REST API ловушки. Тема уникальна, поэтому находится одно письмо.
    public async Task<JsonElement> ReadAsync(string subject)
    {
        using var http = new HttpClient { BaseAddress = Api };
        var list = await http.GetFromJsonAsync<JsonElement>("api/v1/messages?limit=100");
        var id = list.GetProperty("messages").EnumerateArray()
            .Single(m => m.GetProperty("Subject").GetString() == subject).GetProperty("ID").GetString();
        return await http.GetFromJsonAsync<JsonElement>($"api/v1/message/{id}");
    }

    public async Task<bool> HasAsync(string subject)
    {
        using var http = new HttpClient { BaseAddress = Api };
        var list = await http.GetFromJsonAsync<JsonElement>("api/v1/messages?limit=100");
        return list.GetProperty("messages").EnumerateArray().Any(m => m.GetProperty("Subject").GetString() == subject);
    }
}

/// <summary>Ловушка, которая требует вход (этап 8, A5) и принимает письма только на адреса @rpdfos.test.</summary>
public sealed class MailpitWithLoginFixture() : MailpitFixture(new Dictionary<string, string>
{
    ["MP_SMTP_AUTH"] = $"{User}:{Password}",
    ["MP_SMTP_AUTH_ALLOW_INSECURE"] = "true", // вход без TLS: ловушка не умеет TLS без сертификата
    ["MP_SMTP_ALLOWED_RECIPIENTS"] = @"@rpdfos\.test$"
})
{
    public const string User = "rpdfos-mailer", Password = "Test-Passw0rd";
}

internal static class Senders
{
    public static SmtpMailSender To(MailpitFixture mailpit, Action<MailSettings>? configure = null) => To(mailpit.Host, mailpit.Smtp, configure);

    public static SmtpMailSender To(string host, int port, Action<MailSettings>? configure = null)
    {
        var settings = new MailSettings { Host = host, Port = port, From = "АС РПД и ФОС <rpdfos@rpdfos.test>" };
        configure?.Invoke(settings);
        return new SmtpMailSender(Options.Create(settings));
    }
}

/// <summary>Отправитель по SMTP (этап 7, A4) на настоящем SMTP-сервере: кириллица в теме, имени и тексте, письмо без имени.</summary>
public class SmtpMailSenderTests(MailpitFixture mailpit) : IClassFixture<MailpitFixture>
{
    [Fact]
    public async Task A_letter_reaches_the_server_with_the_cyrillic_subject_the_name_of_the_recipient_and_the_text()
    {
        var subject = $"Возвращён на доработку: РПД «Моделирование систем» {Guid.NewGuid():N}";
        var body = "Возвращён на доработку. Замечаний: 2.\n\nОткрыть документ: https://rpdfos.test/documents/1";

        await Senders.To(mailpit).SendAsync(new OutgoingMail("teacher@rpdfos.test", "Иванов И. И.", subject, body));

        var message = await mailpit.ReadAsync(subject);
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

        await Senders.To(mailpit).SendAsync(new OutgoingMail("approver@rpdfos.test", null, subject, "Текст."));

        var to = Assert.Single((await mailpit.ReadAsync(subject)).GetProperty("To").EnumerateArray());
        Assert.Equal(("", "approver@rpdfos.test"), (to.GetProperty("Name").GetString(), to.GetProperty("Address").GetString()));
    }

    [Fact]
    public async Task Several_letters_go_through_one_open_connection()
    {
        var subjects = Enumerable.Range(1, 3).Select(i => $"Письмо {i} {Guid.NewGuid():N}").ToList();

        await using (var connection = await Senders.To(mailpit).OpenAsync())
            foreach (var subject in subjects)
                await connection.SendAsync(new OutgoingMail("teacher@rpdfos.test", null, subject, "Текст."));

        foreach (var subject in subjects) Assert.True(await mailpit.HasAsync(subject), subject);
    }

    [Fact]
    public async Task The_mode_none_works_against_a_server_without_encryption()
    {
        var subject = $"Режим None {Guid.NewGuid():N}";

        await Senders.To(mailpit, s => s.Security = MailSecurity.None)
            .SendAsync(new OutgoingMail("teacher@rpdfos.test", null, subject, "Текст."));

        Assert.True(await mailpit.HasAsync(subject));
    }

    // STARTTLS обязателен: сервер без него отвергается, а не получает письмо открытым текстом. Режим дошёл до клиента.
    [Fact]
    public async Task The_mode_starttls_refuses_a_server_that_does_not_offer_it()
    {
        var failure = await Assert.ThrowsAsync<MailServerUnavailableException>(() =>
            Senders.To(mailpit, s => s.Security = MailSecurity.StartTls).OpenAsync());

        Assert.Contains("STARTTLS", failure.InnerException!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(MailSecurity.Auto, SecureSocketOptions.Auto)]
    [InlineData(MailSecurity.None, SecureSocketOptions.None)]
    [InlineData(MailSecurity.StartTls, SecureSocketOptions.StartTls)]
    [InlineData(MailSecurity.SslOnConnect, SecureSocketOptions.SslOnConnect)]
    public void Every_mode_of_the_setting_maps_to_its_own_mode_of_the_client(MailSecurity setting, SecureSocketOptions expected) =>
        Assert.Equal(expected, SmtpMailSender.SocketOptions(setting));

    [Fact]
    public async Task A_server_that_does_not_answer_is_reported_as_unavailable()
    {
        // Порт, на котором никто не слушает: занять и сразу освободить.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        await Assert.ThrowsAsync<MailServerUnavailableException>(() =>
            Senders.To("127.0.0.1", port).SendAsync(new OutgoingMail("a@rpdfos.test", null, "Тема", "Текст.")));
    }
}

/// <summary>Вход на почтовом сервере и отказ по одному адресу (этап 8, A5) — на ловушке, которая требует логин и пароль.</summary>
public class SmtpMailSenderLoginTests(MailpitWithLoginFixture mailpit) : IClassFixture<MailpitWithLoginFixture>
{
    [Fact]
    public async Task The_login_and_the_password_of_the_settings_reach_the_server()
    {
        var subject = $"С входом {Guid.NewGuid():N}";

        await Senders.To(mailpit, Login(MailpitWithLoginFixture.Password))
            .SendAsync(new OutgoingMail("teacher@rpdfos.test", null, subject, "Текст."));

        Assert.True(await mailpit.HasAsync(subject));
    }

    [Fact]
    public async Task A_wrong_password_is_reported_as_unavailable_and_does_not_leak_into_the_message()
    {
        var failure = await Assert.ThrowsAsync<MailServerUnavailableException>(() =>
            Senders.To(mailpit, Login("Wrong-Passw0rd")).OpenAsync());

        Assert.Contains(MailpitWithLoginFixture.User, failure.Message);
        Assert.DoesNotContain("Wrong-Passw0rd", failure.Message);
        Assert.DoesNotContain(MailpitWithLoginFixture.Password, failure.Message);
    }

    [Fact]
    public async Task A_server_that_requires_a_login_does_not_take_a_letter_without_one()
    {
        // Без входа соединение открывается (это ещё не отправка), а письмо сервер не принимает — это не «сервер недоступен».
        await using var connection = await Senders.To(mailpit).OpenAsync();

        var failure = await Record.ExceptionAsync(() =>
            connection.SendAsync(new OutgoingMail("teacher@rpdfos.test", null, "Без входа", "Текст.")));

        Assert.NotNull(failure);
        Assert.IsNotType<MailServerUnavailableException>(failure);
    }

    [Fact]
    public async Task A_recipient_the_server_refuses_does_not_break_the_connection_for_the_next_letter()
    {
        var subject = $"После отказа {Guid.NewGuid():N}";
        await using var connection = await Senders.To(mailpit, Login(MailpitWithLoginFixture.Password)).OpenAsync();

        // Адрес вне @rpdfos.test ловушка отвергает кодом 5xx; соединение остаётся рабочим, и это не «сервер недоступен».
        var failure = await Record.ExceptionAsync(() =>
            connection.SendAsync(new OutgoingMail("someone@elsewhere.example", null, "Чужой адрес", "Текст.")));
        Assert.NotNull(failure);
        Assert.IsNotType<MailServerUnavailableException>(failure);

        await connection.SendAsync(new OutgoingMail("teacher@rpdfos.test", null, subject, "Текст."));
        Assert.True(await mailpit.HasAsync(subject));
    }

    private static Action<MailSettings> Login(string password) => s =>
    {
        s.User = MailpitWithLoginFixture.User;
        s.Password = password;
        s.Security = MailSecurity.None;
    };
}
