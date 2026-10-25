using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.DocumentTypes;

/// <summary>
/// Цель закладки DOCX-шаблона: <see cref="Kind"/> — вид, <see cref="Target"/> — путь источника, JSON Pointer
/// поля, место подписанта, имя построителя таблицы или текст с подстановками; <see cref="Part"/> — для места
/// подписанта: должность, ФИО или формулировка для документа; <see cref="Note"/> — пояснение к цели;
/// <see cref="Occurrences"/> — переопределения для отдельных вхождений по полному имени закладки; <see cref="Stub"/> —
/// своя формулировка, когда место не предусмотрено планом, вместо общей заглушки раздела (<c>x-stub-text</c>);
/// <see cref="NoCandidates"/> — формулировка по уровню образования направления (код → текст), когда поле выбора из
/// плана (<c>x-plan-disciplines</c>) пусто, потому что выбирать не из чего: у дисциплины 1 семестра нет предшественников.
/// </summary>
public sealed record BookmarkTarget(
    string Kind, string Target, string? Part = null, string? Note = null,
    IReadOnlyDictionary<string, BookmarkOccurrence>? Occurrences = null, string? Stub = null,
    IReadOnlyDictionary<string, string>? NoCandidates = null);

/// <summary>
/// Переопределение для одного вхождения закладки (полное имя с номером): форма слова и, если вхождение
/// по смыслу другое, — своя цель и своя заглушка. Незаданное берётся из цели базового имени.
/// </summary>
public sealed record BookmarkOccurrence(
    string? Form = null, string? Kind = null, string? Target = null, string? Part = null, string? Note = null, string? Stub = null);

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

    /// <summary>Со строчной буквы: значение внутри фразы — «Ответственный исполнитель, заведующий кафедрой».</summary>
    public const string Lower = "lower";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { Nom, Gen, Dat, Acc, Ins, Prep, Lower };
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

/// <summary>JSON сопоставления закладок и листа согласования: файлы реестра типов и снимки в шаблоне (jsonb).</summary>
public static class PrintMappingJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Пояснения к целям — по-русски; без экранирования jsonb читается и в psql.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static IReadOnlyDictionary<string, BookmarkTarget> ReadMapping(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, BookmarkTarget>>(json, Options)!;

    public static IReadOnlyList<SignatureSeat> ReadSignatures(string json) =>
        JsonSerializer.Deserialize<List<SignatureSeat>>(json, Options)!;

    public static string Write(IReadOnlyDictionary<string, BookmarkTarget> mapping) => JsonSerializer.Serialize(mapping, Options);
}

/// <summary>Проверка сопоставления закладок и листа согласования: каждая закладка сопоставлена, каждая цель существует.</summary>
public static partial class PrintMappingValidator
{
    // Подстановка в тексте: {parser.discipline.name}.
    [GeneratedRegex(@"\{(?<source>[^{}]+)\}")]
    private static partial Regex Placeholder();

    /// <summary>Ошибки сопоставления по схеме типа из реестра.</summary>
    public static IReadOnlyList<string> Validate(
        DocumentTypeRegistry registry, string documentType, IReadOnlyDictionary<string, BookmarkTarget> mapping,
        IReadOnlyList<SignatureSeat> signatures, IEnumerable<string> bookmarkBaseNames) =>
        Validate(pointer => registry.FieldExists(documentType, pointer), mapping, signatures, bookmarkBaseNames);

    /// <summary>
    /// Ошибки сопоставления закладок; пустой список — всё сопоставлено и все цели существуют.
    /// <paramref name="fieldExists"/> — есть ли поле в схеме: у шаблона это его снимок схемы.
    /// </summary>
    public static IReadOnlyList<string> Validate(
        Func<string, bool> fieldExists, IReadOnlyDictionary<string, BookmarkTarget> mapping,
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
            BookmarkTargetKinds.Field when !fieldExists(value) => $"поля {value} нет в схеме",
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

    /// <summary>
    /// Ошибки листа согласования: только известные категории подписантов, правила определения и размещения,
    /// без повторов мест.
    /// </summary>
    public static IReadOnlyList<string> ValidateSignatures(IReadOnlyList<SignatureSeat> signatures)
    {
        var errors = new List<string>();
        foreach (var seat in signatures)
        {
            if (!SignatoryRoles.All.Contains(seat.Role))
                errors.Add($"Место {seat.Id}: неизвестная категория подписанта {seat.Role}");
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
