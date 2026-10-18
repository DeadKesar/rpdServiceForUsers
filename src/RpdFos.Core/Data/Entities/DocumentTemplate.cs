namespace RpdFos.Core.Data.Entities;

/// <summary>Шаблон документа (раздел 2.4 ВКР, контекст Documents). Минимальный срез для скелета.</summary>
public class DocumentTemplate : IVersioned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Type { get; set; } = TemplateTypes.Rpd;
    public string Name { get; set; } = "";
    public string Status { get; set; } = TemplateStatuses.Empty;
    public int Version { get; set; } = 1;
    public int Year { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class TemplateTypes
{
    // В прототипе типы документов — константы, а их JSON Schema — файлы в репозитории.
    // Ведение структуры типов Администратором через интерфейс — перспектива.
    public const string Rpd = "rpd", Fos = "fos";
}

public static class TemplateStatuses
{
    // Шесть состояний шаблона с диаграммы состояний (рис. 2.6 ВКР).
    public const string Empty = "empty", Draft = "draft", SavedDraft = "saved_draft",
        Published = "published", Archived = "archived", Deleted = "deleted";
}

/// <summary>Разрешённые переходы статусов шаблона — в одном месте, а не в эндпоинтах.</summary>
public static class TemplateTransitions
{
    // Сверить с диаграммой состояний шаблона. Публикацию из «Пустой» на этапе 3
    // закроет финальная проверка шаблона (FR-TPL-11); пока она нужна для проверки среза.
    public static bool CanPublish(string status) =>
        status is TemplateStatuses.Empty or TemplateStatuses.Draft or TemplateStatuses.SavedDraft;
}
