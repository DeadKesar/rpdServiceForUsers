using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using RpdFos.Core.Printing;
using Xunit;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// Настоящий конвертер PDF (Gotenberg) в контейнере — того же образа, что в docker-compose.yml: номера страниц зависят от версии
/// LibreOffice. Нужен только проверкам PDF; остальным тестам хватает <see cref="FakePdfConverter"/>.
/// </summary>
public sealed class ConverterFixture : IAsyncLifetime
{
    private const ushort Port = 3000;

    private readonly IContainer _container = new ContainerBuilder("gotenberg/gotenberg:8.38.1")
        .WithPortBinding(Port, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(Port).ForPath("/health")))
        .Build();

    public IPdfConverter Converter { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Converter = new GotenbergPdfConverter(new HttpClient
        {
            BaseAddress = new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(Port)}/"), Timeout = TimeSpan.FromMinutes(2)
        });
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}
