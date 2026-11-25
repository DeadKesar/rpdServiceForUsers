using System.Text.RegularExpressions;
using Xunit;

namespace RpdFos.Api.Tests;

/// <summary>
/// Файлы стенда (этап 8, B5): образец настроек без значений и без расхождений с compose, наружу — только 80 и 443,
/// TLS не ниже 1.2, предел тела запроса не меньше предела загрузки.
/// </summary>
public sealed partial class StandFilesTests
{
    private static readonly string Deploy = Path.Combine(Root(), "deploy");
    private static readonly string Compose = File.ReadAllText(Path.Combine(Deploy, "docker-compose.yml"));
    private static readonly string Nginx = File.ReadAllText(Path.Combine(Deploy, "nginx", "templates", "default.conf.template"));

    private static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "RpdFos.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Корень репозитория не найден.");
    }

    private static Dictionary<string, string> Example() =>
        File.ReadAllLines(Path.Combine(Deploy, ".env.example"))
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split('=', 2))
            .ToDictionary(p => p[0], p => p[1]);

    [Fact]
    public void Env_example_has_no_values() =>
        Assert.All(Example(), setting => Assert.Equal("", setting.Value));

    [Fact]
    public void Env_example_and_compose_name_the_same_settings()
    {
        var used = Variable().Matches(Compose).Select(m => m.Groups[1].Value).Distinct().Order();
        Assert.Equal(used, Example().Keys.Order());
    }

    [Fact]
    public void Required_settings_stop_compose() =>
        Assert.All(new[] { "STAND_HOST", "DB_PASSWORD", "CERT_PASSWORD" }, name => Assert.Contains("${" + name + ":?", Compose));

    [Fact]
    public void Only_80_and_443_are_published()
    {
        var published = Regex.Matches(Compose, @"^\s+- ""(\d+):\d+""", RegexOptions.Multiline).Select(m => m.Groups[1].Value);
        Assert.Equal(["80", "443"], published);
        Assert.Single(Regex.Matches(Compose, @"^\s+ports:", RegexOptions.Multiline));
        // База и конвертер — во внутренней сети, без выхода наружу.
        Assert.Matches(@"(?m)^  back:\s*\n\s+internal: true", Compose.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Services_restart_and_use_moscow_time()
    {
        foreach (var name in new[] { "proxy", "api", "db", "converter", "mail", "backup" })
        {
            var service = Service(name);
            Assert.Contains("<<: *service", service);
            Assert.Contains("TZ: Europe/Moscow", service);
        }
        Assert.Contains("restart: unless-stopped", Compose);
    }

    private static string Service(string name) =>
        Regex.Match(Compose.ReplaceLineEndings("\n"), $@"(?ms)^  {name}:\n(.*?)(?=^  \S|^\S|\z)").Groups[1].Value;

    /// <summary>Почтовый сервер стенда (B6): сидит в сети вдвоём с хостом, принимает письма только своего домена.</summary>
    [Fact]
    public void Mail_server_is_reachable_only_from_the_host()
    {
        var mail = Service("mail");
        Assert.DoesNotContain("ports:", mail);
        Assert.Matches(@"networks: \[mailnet\]", mail);
        Assert.Contains("ALLOWED_SENDER_DOMAINS:", mail);
        // Образ закреплён версией: «latest» подменил бы почтовый сервер при обновлении.
        Assert.Matches(@"image: boky/postfix:\d+\.\d+\.\d+", mail);

        // Кроме почтового сервера, в сети mailnet — только хост API: у базы и конвертера выхода в неё нет.
        var members = new[] { "proxy", "api", "db", "converter" }.Where(n => Service(n).Contains("mailnet"));
        Assert.Equal(["api"], members);
        Assert.DoesNotMatch(@"(?m)^  mailnet:\s*\n\s+internal: true", Compose.ReplaceLineEndings("\n"));

        // Хост по умолчанию отдаёт письма своему серверу, без входа и без шифрования внутри сети.
        var api = Service("api");
        Assert.Contains("Mail__Host: ${MAIL_HOST:-mail}", api);
        Assert.Contains("Mail__Security: ${MAIL_SECURITY:-None}", api);
    }

    /// <summary>Резервные копии (B7, NFR-REL-05): служба ходит только в базу, копий по умолчанию 30, ночью, папка — настройкой.</summary>
    [Fact]
    public void Backup_service_keeps_thirty_copies_nightly_in_a_configurable_folder()
    {
        var backup = Service("backup");
        Assert.DoesNotContain("ports:", backup);
        // Только внутренняя сеть: у службы нет выхода в интернет и нет доступа к почте.
        Assert.Matches(@"networks: \[back\]", backup);
        Assert.Contains("BACKUP_KEEP: ${BACKUP_KEEP:-30}", backup);
        Assert.Contains("BACKUP_TIME: ${BACKUP_TIME:-03:00}", backup);
        Assert.Contains("${BACKUP_DIR:-./backups}:/backups", backup);
        // Тот же мажор PostgreSQL, что у базы: pg_dump старше сервера отказывается работать.
        Assert.Equal(Regex.Match(Service("db"), @"image: (\S+)").Groups[1].Value, Regex.Match(backup, @"image: (\S+)").Groups[1].Value);
        // Служба стартует после базы и сообщает, что копии идут.
        Assert.Matches(@"condition: service_healthy", backup);
        Assert.Contains("healthcheck:", backup);
        // Копии не попадают в git.
        Assert.Contains("deploy/backups/", File.ReadAllText(Path.Combine(Root(), ".gitignore")));
    }

    /// <summary>Сценарий копий (B7): хранение — последние N после удачной новой, восстановление не стирает базу без «--replace».</summary>
    [Fact]
    public void Backup_script_prunes_after_success_and_protects_existing_data()
    {
        var script = File.ReadAllText(Path.Combine(Deploy, "scripts", "backup.sh"));
        Assert.DoesNotContain('\r', script);
        // Хранится BACKUP_KEEP (по умолчанию 30), прореживание — только после удачной копии.
        Assert.Contains("KEEP=\"${BACKUP_KEEP:-30}\"", script);
        Assert.Matches(@"make_backup rpdfos; then\s+last=\$today\s+prune", script.ReplaceLineEndings("\n"));
        Assert.Contains("--replace", script);
        // Недоступная база — ожидание с одним сообщением, а не пустое число таблиц в сравнении.
        Assert.Matches(@"if ! tables=\$\(table_count ""\$DB"" 2> /tmp/psql.err\); then", script);
        Assert.DoesNotContain("[ \"$(table_count \"$DB\")\" -eq 0 ]", script);
        // Копия — в формате custom, сначала во временный файл.
        Assert.Contains("pg_dump -Fc", script);
        Assert.Contains(".partial", script);
        // Восстановление идёт во временную базу, подмена — после успеха.
        Assert.Matches(@"(?s)pg_restore -d ""\$tmp"".*?DROP DATABASE IF EXISTS \$DB.*?RENAME TO \$DB", script.Replace("\r", ""));
    }

    [Fact]
    public void Nginx_limits_tls_and_body_size_and_hides_query()
    {
        Assert.Equal("TLSv1.2 TLSv1.3", Regex.Match(Nginx, "ssl_protocols ([^;]+);").Groups[1].Value);
        Assert.True(int.Parse(Regex.Match(Nginx, @"client_max_body_size (\d+)m;").Groups[1].Value) >= 3 * 20);
        // Журнал общего уровня из nginx.conf пишет строку запроса с токенами: у каждого сервера должен быть свой.
        Assert.Equal(Regex.Count(Nginx, @"(?m)^server \{"), Regex.Count(Nginx, @"(?m)^    access_log \S+ stand;"));
    }

    [GeneratedRegex(@"\$\{([A-Z_]+)")]
    private static partial Regex Variable();
}
