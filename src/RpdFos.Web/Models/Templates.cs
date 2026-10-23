using System.Text.Json;

namespace RpdFos.Web.Models;

// Контракт API шаблонов (Features/Templates) на стороне клиента: клиент не ссылается на сборку хоста.

public sealed record PlanItem(Guid Id, string DirectionCode, string DirectionName, string ProfileName, int Year,
    string StudyForm, int Version)
{
    public string Title => $"{DirectionCode} {DirectionName} — {ProfileName}, набор {Year}, версия {Version}";
}

public sealed record TemplateItem(Guid Id, string Type, string TypeTitle, string Name, string Status, string StatusTitle,
    int Version, int Revision, Guid LineageId, PlanItem? Plan, DateTimeOffset CreatedAt, DateTimeOffset? PublishedAt);

public sealed record TemplateDetails(TemplateItem Template, string? PrintTemplateName, List<BookmarkRow> Bookmarks,
    List<SignatureRow> Signatures, List<string> Errors, bool CanPublish, JsonElement Schema, List<DefaultField> DefaultFields,
    JsonElement? Defaults);

/// <summary>Поле, для которого шаблон задаёт значение по умолчанию: JSON Pointer и подпись «Раздел → Поле».</summary>
public sealed record DefaultField(string Pointer, string Title);

public sealed record TemplateDefaultsRequest(int Version, JsonElement Defaults);

public sealed record BookmarkRow(string BaseName, int Count, List<string> Names, BookmarkTarget? Target);

public sealed record BookmarkTarget(string Kind, string Target, string? Part, string? Note,
    Dictionary<string, BookmarkOccurrence>? Occurrences);

public sealed record BookmarkOccurrence(string? Form, string? Kind, string? Target, string? Part, string? Note);

public sealed record SignatureRow(string Id, string Title, string Role, string RoleTitle, string Resolver,
    string? ResolverTitle, bool Electronic, string Placement);

/// <summary>Ответ об ошибке (ProblemDetails); <see cref="Errors"/> — причины, по которым шаблон не прошёл проверку.</summary>
public sealed record TemplateProblem(string? Title, string? Detail, List<string>? Errors);

/// <summary>Русские названия кодов, которые сервер отдаёт как есть.</summary>
public static class TemplateTitles
{
    public static readonly (string Code, string Title)[] Types = [("rpd", "РПД"), ("fos", "ФОС")];

    public static readonly (string Code, string Title)[] Statuses =
    [
        ("empty", "Пустой"), ("draft", "Черновик"), ("saved_draft", "Сохранённый черновик"),
        ("published", "Опубликован"), ("archived", "Архивный")
    ];

    public static string Kind(string kind) => kind switch
    {
        "source" => "Данные плана и справочников",
        "field" => "Поле документа",
        "signature" => "Подпись",
        "table" => "Таблица",
        "text" => "Текст",
        _ => kind
    };

    public static string Part(string? part) => part switch
    {
        "position" => "должность",
        "name" => "ФИО",
        "document_title" => "наименование для документа",
        _ => part ?? ""
    };

    public static string Form(string? form) => form switch
    {
        "nom" => "именительный падеж",
        "gen" => "родительный падеж",
        "dat" => "дательный падеж",
        "acc" => "винительный падеж",
        "ins" => "творительный падеж",
        "prep" => "предложный падеж",
        _ => form ?? ""
    };

    public static string Placement(string placement) => placement switch
    {
        "title" => "Гриф «УТВЕРЖДАЮ» на титуле",
        "approval_sheet" => "Лист согласования",
        _ => placement
    };
}
