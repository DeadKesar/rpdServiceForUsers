using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.HttpOverrides;

namespace RpdFos.Api.Infrastructure;

/// <summary>
/// Настройки хоста вне разработки (этап 8, B4): сертификаты OpenIddict, ключи Data Protection, адрес системы, заголовки прокси.
/// Вынесены из <c>Program.cs</c>, чтобы ошибки запуска читались тестом. Каждая ошибка называет настройку: хост без них
/// не стартует, а не подставляет ключи разработки.
/// </summary>
public static class HostingSetup
{
    public const string SigningCertificatePath = "Auth:SigningCertificatePath";
    public const string EncryptionCertificatePath = "Auth:EncryptionCertificatePath";
    public const string CertificatePassword = "Auth:CertificatePassword";
    public const string PublicUrl = "Auth:PublicUrl";
    public const string KeysPath = "DataProtection:KeysPath";

    /// <summary>Имя приложения Data Protection: ключи не зависят от каталога, из которого запущен хост.</summary>
    public const string DataProtectionApplicationName = "RpdFos";

    /// <summary>
    /// Окружения, где допустимы ключи разработки. Testing — окружение тестов: хост поднимается без сертификатов и без тома.
    /// Всё остальное (Production и любое иное имя) требует настоящих настроек.
    /// </summary>
    public static bool UsesDevelopmentKeys(IHostEnvironment environment) =>
        environment.IsDevelopment() || environment.IsEnvironment("Testing");

    /// <summary>Сертификаты подписи и шифрования токенов OpenIddict из файлов PFX; пароль — из переменной окружения.</summary>
    public static (X509Certificate2 Signing, X509Certificate2 Encryption) LoadCertificates(IConfiguration config, TimeProvider time)
    {
        var password = config[CertificatePassword];
        if (string.IsNullOrEmpty(password))
            throw new InvalidOperationException(
                $"Не задан пароль сертификатов — настройка {CertificatePassword} (переменная окружения Auth__CertificatePassword).");
        return (Load(config, SigningCertificatePath, "подписи", password, time),
                Load(config, EncryptionCertificatePath, "шифрования", password, time));
    }

    private static X509Certificate2 Load(IConfiguration config, string setting, string purpose, string password, TimeProvider time)
    {
        var path = config[setting];
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException($"Не задан файл сертификата {purpose} — настройка {setting}.");
        if (!File.Exists(path))
            throw new InvalidOperationException($"Файл сертификата {purpose} не найден: {path} (настройка {setting}).");

        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password);
        }
        catch (System.Security.Cryptography.CryptographicException e)
        {
            throw new InvalidOperationException(
                $"Не удалось прочитать сертификат {purpose} из {path}: неверный пароль ({CertificatePassword}) или файл не в формате PFX.", e);
        }

        if (!certificate.HasPrivateKey)
            throw new InvalidOperationException($"В файле сертификата {purpose} нет закрытого ключа: {path} (настройка {setting}).");
        var now = time.GetUtcNow();
        if (certificate.NotAfter.ToUniversalTime() < now || certificate.NotBefore.ToUniversalTime() > now)
            throw new InvalidOperationException(
                $"Сертификат {purpose} не действует на сегодня (срок с {certificate.NotBefore:yyyy-MM-dd} по {certificate.NotAfter:yyyy-MM-dd}): {path}.");
        return certificate;
    }

    /// <summary>Папка ключей Data Protection на томе; без неё после перезапуска контейнера перестают действовать вход и ссылки из писем.</summary>
    public static string RequireKeysPath(IConfiguration config)
    {
        var path = config[KeysPath];
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException(
                $"Не задана папка ключей Data Protection — настройка {KeysPath}: она должна лежать на томе, иначе после перезапуска входы и ссылки из писем перестанут действовать.");
        return path;
    }

    /// <summary>Адрес системы: от него зависят адреса возврата клиента OIDC; значение по умолчанию (localhost) на стенде ошибочно.</summary>
    public static string RequirePublicUrl(IConfiguration config)
    {
        var url = config[PublicUrl];
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException($"Не задан адрес системы — настройка {PublicUrl} (например, https://rpdfos.example).");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"Адрес системы {PublicUrl} = «{url}»: нужен абсолютный адрес https.");
        return url;
    }

    /// <summary>
    /// Заголовки nginx: адрес, схема, имя узла. Прокси не перечисляются: порт хоста API наружу не публикуется (compose стенда),
    /// запросы приходят только от nginx из внутренней сети.
    /// </summary>
    public static void ConfigureForwardedHeaders(ForwardedHeadersOptions o)
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    }
}
