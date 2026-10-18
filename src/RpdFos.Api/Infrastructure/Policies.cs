namespace RpdFos.Api.Infrastructure;

/// <summary>Политики доступа к API (роли — из токена доступа).</summary>
public static class Policies
{
    public const string MethodistOrAdmin = "methodist-or-admin";
    public const string Admin = "admin-only";

    /// <summary>
    /// Панель фоновых задач — только администратору. Её открывают в браузере переходом по адресу,
    /// без токена доступа, поэтому вход проверяется по cookie страницы входа.
    /// </summary>
    public const string JobsDashboard = "jobs-dashboard";
}

/// <summary>Очереди фоновых задач Hangfire.</summary>
public static class JobQueues
{
    /// <summary>Разбор и импорт исходных файлов — один исполнитель: импорты не идут параллельно.</summary>
    public const string Parsing = "parsing";
}
