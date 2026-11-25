using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RpdFos.Api.Infrastructure;
using Xunit;

namespace RpdFos.Api.Tests;

/// <summary>Настройки хоста вне разработки (этап 8, B4): без сертификатов, ключей и адреса хост не стартует, а ошибка называет настройку.</summary>
public sealed class HostingSetupTests : IDisposable
{
    private const string Password = "test-cert-password";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rpdfos-tests", Guid.NewGuid().ToString("N"));

    public HostingSetupTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { /* временная папка теста */ }
    }

    /// <summary>Самоподписанный сертификат в PFX с паролем.</summary>
    private string MakeCertificate(string name, DateTimeOffset notBefore, DateTimeOffset notAfter, string password = Password)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(notBefore, notAfter);
        var path = Path.Combine(_dir, name + ".pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, password));
        return path;
    }

    private string ValidCertificate(string name) =>
        MakeCertificate(name, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private Dictionary<string, string?> ValidSettings() => new()
    {
        [HostingSetup.SigningCertificatePath] = ValidCertificate("signing"),
        [HostingSetup.EncryptionCertificatePath] = ValidCertificate("encryption"),
        [HostingSetup.CertificatePassword] = Password,
        [HostingSetup.PublicUrl] = "https://stand.example",
        [HostingSetup.KeysPath] = Path.Combine(_dir, "keys")
    };

    private static void AssertFails(Action action, string setting)
    {
        var e = Assert.Throws<InvalidOperationException>(action);
        Assert.Contains(setting, e.Message);
    }

    [Fact]
    public void Certificates_load_from_files()
    {
        var (signing, encryption) = HostingSetup.LoadCertificates(Config(ValidSettings()), TimeProvider.System);

        Assert.True(signing.HasPrivateKey);
        Assert.True(encryption.HasPrivateKey);
        Assert.Equal("CN=signing", signing.Subject);
    }

    [Fact]
    public void Missing_password_names_the_setting()
    {
        var s = ValidSettings();
        s.Remove(HostingSetup.CertificatePassword);
        AssertFails(() => HostingSetup.LoadCertificates(Config(s), TimeProvider.System), HostingSetup.CertificatePassword);
    }

    [Theory]
    [InlineData(HostingSetup.SigningCertificatePath)]
    [InlineData(HostingSetup.EncryptionCertificatePath)]
    public void Missing_certificate_path_names_the_setting(string setting)
    {
        var s = ValidSettings();
        s.Remove(setting);
        AssertFails(() => HostingSetup.LoadCertificates(Config(s), TimeProvider.System), setting);
    }

    [Fact]
    public void Missing_certificate_file_names_the_setting_and_the_path()
    {
        var s = ValidSettings();
        s[HostingSetup.SigningCertificatePath] = Path.Combine(_dir, "нет-такого.pfx");
        var e = Assert.Throws<InvalidOperationException>(() => HostingSetup.LoadCertificates(Config(s), TimeProvider.System));
        Assert.Contains(HostingSetup.SigningCertificatePath, e.Message);
        Assert.Contains("нет-такого.pfx", e.Message);
    }

    [Fact]
    public void Wrong_password_names_the_password_setting()
    {
        var s = ValidSettings();
        s[HostingSetup.CertificatePassword] = "другой-пароль";
        AssertFails(() => HostingSetup.LoadCertificates(Config(s), TimeProvider.System), HostingSetup.CertificatePassword);
    }

    [Fact]
    public void Expired_certificate_is_rejected()
    {
        var s = ValidSettings();
        s[HostingSetup.EncryptionCertificatePath] =
            MakeCertificate("old", DateTimeOffset.UtcNow.AddYears(-2), DateTimeOffset.UtcNow.AddYears(-1));
        AssertFails(() => HostingSetup.LoadCertificates(Config(s), TimeProvider.System), "не действует");
    }

    [Fact]
    public void Keys_path_and_public_url_are_required()
    {
        AssertFails(() => HostingSetup.RequireKeysPath(Config([])), HostingSetup.KeysPath);
        AssertFails(() => HostingSetup.RequirePublicUrl(Config([])), HostingSetup.PublicUrl);
        // Без https клиент OIDC не войдёт: OpenIddict принимает только https.
        AssertFails(() => HostingSetup.RequirePublicUrl(Config(new() { [HostingSetup.PublicUrl] = "http://stand" })), HostingSetup.PublicUrl);
    }

    private WebApplicationFactory<Program> ProductionHost(Dictionary<string, string?> settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            // Без базы и фоновых задач: проверяется только запуск и проводка настроек.
            b.UseSetting("Jobs:Enabled", "false");
            foreach (var (key, value) in settings)
                b.UseSetting(key, value);
        });

    [Fact]
    public void Production_host_without_certificates_does_not_start()
    {
        using var host = ProductionHost([]);

        // Исключение выходит из запуска хоста: тип может быть обёрнут, ищем сообщение по цепочке.
        var e = Record.Exception(() => host.CreateClient());

        Assert.NotNull(e);
        Assert.Contains(HostingSetup.CertificatePassword, Messages(e));
    }

    [Fact]
    public void Production_host_without_keys_path_does_not_start()
    {
        var s = ValidSettings();
        s.Remove(HostingSetup.KeysPath);
        using var host = ProductionHost(s);

        var e = Record.Exception(() => host.CreateClient());

        Assert.NotNull(e);
        Assert.Contains(HostingSetup.KeysPath, Messages(e));
    }

    private static string Messages(Exception e) =>
        string.Join(" | ", Chain(e).Select(x => x.Message));

    private static IEnumerable<Exception> Chain(Exception? e)
    {
        for (; e is not null; e = e.InnerException) yield return e;
    }

    [Fact]
    public async Task Production_host_uses_certificates_hides_swagger_and_follows_proxy_headers()
    {
        var s = ValidSettings();
        using var host = ProductionHost(s);
        var client = host.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"), AllowAutoRedirect = false
        });

        // Здоровье — по http, без перенаправления на https: проверку делает контейнер изнутри.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);

        // Описание API — только в разработке: /openapi и /swagger отдают страницу клиента или 404, но не описание.
        foreach (var path in new[] { "/openapi/v1.json", "/swagger/index.html" })
        {
            var response = await client.GetAsync(path);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("openapi", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("swagger-ui", body, StringComparison.OrdinalIgnoreCase);
        }

        // Адрес и схему задаёт nginx: издатель токенов строится по ним, а не по адресу контейнера.
        var request = new HttpRequestMessage(HttpMethod.Get, "/.well-known/openid-configuration");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-Host", "stand.example");
        var discovery = await client.SendAsync(request);
        discovery.EnsureSuccessStatusCode();
        using var doc = System.Text.Json.JsonDocument.Parse(await discovery.Content.ReadAsStringAsync());
        Assert.Equal("https://stand.example/", doc.RootElement.GetProperty("issuer").GetString());

        // Токены подписаны сертификатом из файла, а не ключом разработки.
        var thumbprint = Base64Url(X509CertificateLoader.LoadPkcs12FromFile(s[HostingSetup.SigningCertificatePath]!, Password).GetCertHash());
        var jwksRequest = new HttpRequestMessage(HttpMethod.Get, "/.well-known/jwks");
        jwksRequest.Headers.Add("X-Forwarded-Proto", "https");
        var jwks = await client.SendAsync(jwksRequest);
        jwks.EnsureSuccessStatusCode();
        Assert.Contains(thumbprint, await jwks.Content.ReadAsStringAsync());

        // Ключи Data Protection ложатся в папку на томе и переживают перезапуск контейнера.
        host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Protect("x");
        Assert.NotEmpty(Directory.GetFiles(s[HostingSetup.KeysPath]!, "key-*.xml"));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
