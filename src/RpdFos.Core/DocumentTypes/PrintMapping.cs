using System.Text.Json;
using System.Text.RegularExpressions;

namespace RpdFos.Core.DocumentTypes;

/// <summary>
/// Цель закладки DOCX-шаблона: <see cref="Kind"/> — вид, <see cref="Target"/> — путь источника, JSON Pointer
/// поля, место подписанта, имя построителя таблицы или текст с подстановками; <see cref="Part"/> — для места
/// подписанта: должность, ФИО или формулировка для документа; <see cref="Note"/> — пояснение к цели;
/// <see cref="Occurrences"/> — переопределения для отдельных вхождений по полному имени закладки.
/// </summary>
public sealed record BookmarkTarget(
    string Kind, string Target, string? Part = null, string? Note = null,
    IReadOnlyDictionary<string, BookmarkOccurrence>? Occurrences = null);

/// <summary>
/// Переопределение для одного вхождения закладки (полное имя с номером): форма слова и, если вхождение
/// по смыслу другое, — своя цель. Незаданное берётся из цели базового имени.
/// </summary>
public sealed record BookmarkOccurrence(
    string? Form = null, string? Kind = null, string? Target = null, string? Part = null, string? Note = null);

public static class BookmarkTargetKinds
{
    public const string Source = "source", Field = "field", Signature = "signature", Table = "table", Text = "text";
}

/// <summary>
/// Падежные формы значения. Одна закладка бывает в разных формах: «Обязательная часть» на титуле РПД
/// и «относится к обязательной части» в разделе 2. Склонение — на этапе 5, словарём форм для закрытых
/// наборов значений (части плана, формы обучения, уровни, квалификации), без библиотек морфологии.
/// Без переопределения — именительный.
/// </summary>
public static class WordForms
{
    public const string Nom = "nom", Gen = "gen", Dat = "dat", Acc = "acc", Ins = "ins", Prep = "prep";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { Nom, Gen, Dat, Acc, Ins, Prep };
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
            if (Problem(target.Kind, target.Target, target.Part) is { } problem)
                errors.Add($"Закладка {bookmark}: {problem}");

            // Вхождения, которых нет в загруженном шаблоне, не мешают: сопоставление по умолчанию общее для шаблонов.
            foreach (var (occurrence, o) in target.Occurrences ?? new Dictionary<string, BookmarkOccurrence>())
            {
                var occurrenceProblem =
                    DocxBookmarks.BaseName(occurrence) != bookmark ? "вхождение относится к другой закладке" :
                    o.Form is not null && !WordForms.All.Contains(o.Form) ? $"неизвестная форма «{o.Form}»" :
                    o.Kind is not null || o.Target is not null || o.Part is not null
                        ? Problem(o.Kind ?? target.Kind, o.Target ?? target.Target, o.Part ?? target.Part)
                        : null;
                if (occurrenceProblem is not null)
                    errors.Add($"Закладка {bookmark}, вхождение {occurrence}: {occurrenceProblem}");
            }
        }
        return errors;

        string? Problem(string kind, string value, string? part) => kind switch
        {
            BookmarkTargetKinds.Source when !DocumentSources.IsKnown(value) => $"неизвестный источник {value}",
            BookmarkTargetKinds.Field when !registry.FieldExists(documentType, value) => $"поля {value} нет в схеме",
            BookmarkTargetKinds.Signature when !seats.Contains(value) => $"места {value} нет в листе согласования",
            BookmarkTargetKinds.Signature when part is null || !SignatureParts.All.Contains(part) =>
                $"неизвестная часть подписи «{part}»",
            BookmarkTargetKinds.Table when !TableBuilders.All.ContainsKey(value) => $"неизвестный построитель таблицы {value}",
            BookmarkTargetKinds.Text when string.IsNullOrWhiteSpace(value) => "пустой текст",
            BookmarkTargetKinds.Text => Placeholder().Matches(value)
                .Select(m => m.Groups["source"].Value).FirstOrDefault(s => !DocumentSources.IsKnown(s)) is { } unknown
                ? $"неизвестный источник {unknown} в тексте"
                : null,
            BookmarkTargetKinds.Source or BookmarkTargetKinds.Field or BookmarkTargetKinds.Signature
                or BookmarkTargetKinds.Table => null,
            _ => $"неизвестный вид цели «{kind}»"
        };
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
