namespace RpdFos.Api.Infrastructure;

/// <summary>Политики доступа к API (роли — из токена доступа).</summary>
public static class Policies
{
    public const string MethodistOrAdmin = "methodist-or-admin";
    public const string Admin = "admin-only";

    /// <summary>Авторы документов: преподаватель создаёт РПД, методист и администратор — тоже (за кафедру).</summary>
    public const string DocumentAuthors = "document-authors";

    /// <summary>Проверяющие: возврат, приём и замечания — только методист (этап 5).</summary>
    public const string Reviewers = "reviewers";

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

    /// <summary>Формирование DOCX и PDF (FR-GEN-04) и очистка сформированных файлов по сроку хранения.</summary>
    public const string Generation = "generation";
}
