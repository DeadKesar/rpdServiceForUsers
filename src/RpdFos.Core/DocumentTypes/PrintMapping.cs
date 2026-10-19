using System.Text.Json;
using System.Text.RegularExpressions;

namespace RpdFos.Core.DocumentTypes;

/// <summary>
/// Цель закладки DOCX-шаблона: <see cref="Kind"/> — вид, <see cref="Target"/> — путь источника, JSON Pointer
/// поля, место подписанта, имя построителя таблицы или текст с подстановками; <see cref="Part"/> — для места
/// подписанта: должность, ФИО или формулировка для документа; <see cref="Note"/> — пояснение к спорной цели.
/// </summary>
public sealed record BookmarkTarget(string Kind, string Target, string? Part = null, string? Note = null);

public static class BookmarkTargetKinds
{
    public const string Source = "source", Field = "field", Signature = "signature", Table = "table", Text = "text";
}

public static class SignatureParts
{
    // Должность, ФИО («И. О. Фамилия»), формулировка для документа (сокращение подразделения).
    public const string Position = "position", Name = "name", DocumentTitle = "document_title";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { Position, Name, DocumentTitle };
}

/// <summary>
/// Место подписанта в листе согласования шаблона: категория, правило определения (имя резолвера),
/// участие в электронном согласовании, размещение — гриф «УТВЕРЖДАЮ» на титуле или лист согласования.
/// </summary>
public sealed record SignatureSeat(string Id, string Title, string Role, string Resolver, bool Electronic, string Placement);

public static class SignaturePlacements
{
    public const string Title = "title", ApprovalSheet = "approval_sheet";
}

/// <summary>Проверка сопоставления закладок и листа согласования: каждая закладка сопоставлена, каждая цель существует.</summary>
public static partial class PrintMappingValidator
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    // Подстановка в тексте: {parser.discipline.name}.
    [GeneratedRegex(@"\{(?<source>[^{}]+)\}")]
    private static partial Regex Placeholder();

    /// <summary>Ошибки сопоставления закладок; пустой список — всё сопоставлено и все цели существуют.</summary>
    public static IReadOnlyList<string> Validate(
        DocumentTypeRegistry registry, string documentType, IReadOnlyDictionary<string, BookmarkTarget> mapping,
        IReadOnlyList<SignatureSeat> signatures, IEnumerable<string> bookmarkBaseNames)
    {
        var errors = new List<string>();
        foreach (var bookmark in bookmarkBaseNames.Where(b => !mapping.ContainsKey(b)))
            errors.Add($"Закладка {bookmark} не сопоставлена");

        var seats = signatures.Select(s => s.Id).ToHashSet();
        foreach (var (bookmark, target) in mapping)
        {
            var problem = target.Kind switch
            {
                BookmarkTargetKinds.Source when !DocumentSources.IsKnown(target.Target) =>
                    $"неизвестный источник {target.Target}",
                BookmarkTargetKinds.Field when !registry.FieldExists(documentType, target.Target) =>
                    $"поля {target.Target} нет в схеме",
                BookmarkTargetKinds.Signature when !seats.Contains(target.Target) =>
                    $"места {target.Target} нет в листе согласования",
                BookmarkTargetKinds.Signature when target.Part is null || !SignatureParts.All.Contains(target.Part) =>
                    $"неизвестная часть подписи «{target.Part}»",
                BookmarkTargetKinds.Table when !TableBuilders.All.ContainsKey(target.Target) =>
                    $"неизвестный построитель таблицы {target.Target}",
                BookmarkTargetKinds.Text when string.IsNullOrWhiteSpace(target.Target) => "пустой текст",
                BookmarkTargetKinds.Text => Placeholder().Matches(target.Target)
                    .Select(m => m.Groups["source"].Value).FirstOrDefault(s => !DocumentSources.IsKnown(s)) is { } unknown
                    ? $"неизвестный источник {unknown} в тексте"
                    : null,
                BookmarkTargetKinds.Source or BookmarkTargetKinds.Field or BookmarkTargetKinds.Signature
                    or BookmarkTargetKinds.Table => null,
                _ => $"неизвестный вид цели «{target.Kind}»"
            };
            if (problem is not null) errors.Add($"Закладка {bookmark}: {problem}");
        }
        return errors;
    }

    /// <summary>Ошибки листа согласования: только известные правила определения и размещения, без повторов мест.</summary>
    public static IReadOnlyList<string> ValidateSignatures(IReadOnlyList<SignatureSeat> signatures)
    {
        var errors = new List<string>();
        foreach (var seat in signatures)
        {
            if (!SignatureResolvers.All.ContainsKey(seat.Resolver))
                errors.Add($"Место {seat.Id}: неизвестное правило определения {seat.Resolver}");
            if (seat.Placement is not (SignaturePlacements.Title or SignaturePlacements.ApprovalSheet))
                errors.Add($"Место {seat.Id}: неизвестное размещение {seat.Placement}");
        }
        foreach (var duplicate in signatures.GroupBy(s => s.Id).Where(g => g.Count() > 1))
            errors.Add($"Место {duplicate.Key} повторяется");
        return errors;
    }
}
