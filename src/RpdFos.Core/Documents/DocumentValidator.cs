using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RpdFos.Core.DocumentTypes;
using CompiledSchema = Corvus.Text.Json.Validator.JsonSchema;
using ResultsCollector = Corvus.Text.Json.JsonSchemaResultsCollector;
using ResultsLevel = Corvus.Text.Json.JsonSchemaResultsLevel;

namespace RpdFos.Core.Documents;

/// <summary>
/// Режим проверки. Черновик сохраняется незаполненным: обязательность и полнота (required, minLength, minItems)
/// не проверяются, ошибки типов и форматов блокируют сохранение. Отправка — полная проверка с обязательными
/// полями и <c>x-required-for-status</c>.
/// </summary>
public enum ValidationMode { Draft, Submit }

/// <summary>Ошибка проверки содержимого: место (JSON Pointer поля) и сообщение для пользователя.</summary>
public sealed record DocumentValidationError(string Path, string Message);

/// <summary>Проверка содержимого документа по JSON Schema его шаблона (FR-VAL-01).</summary>
public interface IDocumentValidator
{
    /// <param name="schemaJson">Снимок схемы из шаблона документа.</param>
    /// <param name="payload">Содержимое документа.</param>
    /// <param name="mode">Черновик или отправка.</param>
    /// <param name="targetStatus">Статус, в который переходит документ, — для <c>x-required-for-status</c>.</param>
    IReadOnlyList<DocumentValidationError> Validate(string schemaJson, JsonElement payload, ValidationMode mode,
        string targetStatus = DocumentStatuses.UnderReview);
}

/// <summary>
/// Валидатор на Corvus.Text.Json.Validator (Draft 2020-12, Apache 2.0). x-расширения схем — аннотации,
/// валидатор их пропускает. Схема компилируется один раз на текст снимка; ссылки на схемы других типов
/// (ФОС → РПД) разрешает реестр типов — ни файловой системы, ни сети.
/// </summary>
public sealed class DocumentValidator(DocumentTypeRegistry registry) : IDocumentValidator
{
    // Полнота заполнения: в черновике не проверяется.
    private static readonly HashSet<string> Completeness = ["required", "minLength", "minItems", "minProperties"];

    private readonly ConcurrentDictionary<string, CompiledSchema> _compiled = new();

    /// <summary>
    /// Ключевые слова-утверждения, о которых валидатор сообщает пользователю. Остальные отказы — обёртки
    /// применителей (properties, items, $ref: «подсхема не прошла») — повторяют отказ вложенного поля.
    /// </summary>
    public static readonly IReadOnlySet<string> Assertions = new HashSet<string>
    {
        "type", "enum", "const", "pattern", "format", "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum",
        "multipleOf", "minLength", "maxLength", "minItems", "maxItems", "uniqueItems", "required", "minProperties",
        "maxProperties", "additionalProperties", "dependentRequired"
    };

    public IReadOnlyList<DocumentValidationError> Validate(string schemaJson, JsonElement payload, ValidationMode mode,
        string targetStatus = DocumentStatuses.UnderReview)
    {
        var schema = JsonDocument.Parse(schemaJson).RootElement;
        var errors = new List<DocumentValidationError>();

        using (var collector = ResultsCollector.Create(ResultsLevel.Detailed))
        {
            Compile(schemaJson).Validate(payload.GetRawText(), collector);
            foreach (var result in collector.EnumerateResults())
            {
                if (result.IsMatch) continue;
                var location = result.GetSchemaEvaluationLocationText();
                var keyword = location[(location.LastIndexOf('/') + 1)..];
                if (!Assertions.Contains(keyword) || mode == ValidationMode.Draft && Completeness.Contains(keyword))
                    continue;
                // Место — поле; у required — само недостающее поле, а не объект, где его нет.
                var path = result.GetDocumentEvaluationLocationText();
                errors.Add(new DocumentValidationError(path, Message(keyword, SchemaInspector.SubschemaAt(schema, path, registry.FindSchema))));
            }
        }

        if (mode == ValidationMode.Submit)
            errors.AddRange(RequiredForStatus(schema, payload, targetStatus));

        return errors.Distinct().OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
    }

    // Поле обязательно при переходе в указанный статус и дальше по пути к утверждению.
    private IEnumerable<DocumentValidationError> RequiredForStatus(JsonElement schema, JsonElement payload, string targetStatus)
    {
        foreach (var (pattern, status) in SchemaInspector.RequiredForStatus(schema, registry.FindSchema))
        {
            if (DocumentStatuses.Progress(targetStatus) < Math.Max(1, DocumentStatuses.Progress(status))) continue;
            foreach (var path in Missing(payload, pattern.TrimStart('/').Split('/'), ""))
                yield return new DocumentValidationError(path,
                    $"Обязательно для перехода в статус «{DocumentStatuses.Title(status)}»");
        }
    }

    // Места по шаблону пути, где поля нет. Нет родителя — молчим: его отсутствие уже ошибка required выше.
    private static IEnumerable<string> Missing(JsonElement node, string[] segments, string path)
    {
        if (segments.Length == 0) yield break;
        var segment = segments[0];
        if (segment == "*")
        {
            if (node.ValueKind != JsonValueKind.Array) yield break;
            var i = 0;
            foreach (var item in node.EnumerateArray())
                foreach (var missing in Missing(item, segments[1..], $"{path}/{i++}"))
                    yield return missing;
            yield break;
        }
        if (node.ValueKind != JsonValueKind.Object) yield break;
        var name = segment.Replace("~1", "/").Replace("~0", "~");
        var present = node.TryGetProperty(name, out var child) && child.ValueKind != JsonValueKind.Null;
        if (segments.Length == 1)
        {
            if (!present) yield return $"{path}/{segment}";
            yield break;
        }
        if (present)
            foreach (var missing in Missing(child, segments[1..], $"{path}/{segment}"))
                yield return missing;
    }

    private static string Message(string keyword, JsonElement? field)
    {
        string? Value(string name) => field is { ValueKind: JsonValueKind.Object } f && f.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()
            : null;

        return keyword switch
        {
            "required" => "Обязательное поле не заполнено",
            "type" => $"Ожидается {TypeTitle(field)}",
            "enum" or "const" => "Значение не из списка допустимых",
            "pattern" => Value("description") is { } hint ? $"Неверный формат. {hint}" : "Неверный формат",
            "format" => Value("format") switch
            {
                "date" => "Ожидается дата в формате ГГГГ-ММ-ДД",
                "uri" => "Ожидается адрес ссылки, например https://…",
                "email" => "Ожидается адрес электронной почты",
                var format => $"Неверный формат ({format})"
            },
            "minimum" => $"Значение должно быть не меньше {Value("minimum")}",
            "maximum" => $"Значение должно быть не больше {Value("maximum")}",
            "exclusiveMinimum" => $"Значение должно быть больше {Value("exclusiveMinimum")}",
            "exclusiveMaximum" => $"Значение должно быть меньше {Value("exclusiveMaximum")}",
            "multipleOf" => $"Значение должно быть кратно {Value("multipleOf")}",
            "minLength" => $"Слишком короткий текст: нужно не меньше {Value("minLength")} символов",
            "maxLength" => $"Слишком длинный текст: допустимо не больше {Value("maxLength")} символов",
            "minItems" => $"Нужно не меньше {Value("minItems")} элементов",
            "maxItems" => $"Допустимо не больше {Value("maxItems")} элементов",
            "uniqueItems" => "Элементы списка повторяются",
            "additionalProperties" => "Поле не предусмотрено структурой документа",
            "minProperties" or "dependentRequired" => "Раздел заполнен не полностью",
            "maxProperties" => "В разделе слишком много полей",
            _ => "Значение не соответствует структуре документа"
        };
    }

    private static string TypeTitle(JsonElement? field)
    {
        var type = field is { ValueKind: JsonValueKind.Object } f && f.TryGetProperty("type", out var t)
            ? t.ValueKind == JsonValueKind.Array ? t.EnumerateArray().FirstOrDefault().GetString() : t.GetString()
            : null;
        return type switch
        {
            "string" => "текст",
            "integer" => "целое число",
            "number" => "число",
            "boolean" => "«да» или «нет»",
            "array" => "список",
            "object" => "раздел с полями",
            _ => "значение другого вида"
        };
    }

    private CompiledSchema Compile(string schemaJson) =>
        _compiled.GetOrAdd(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(schemaJson))), _ =>
            // Corvus кэширует схемы по $id; у снимков разных шаблонов $id один, поэтому кэш обновляется,
            // а скомпилированная схема хранится здесь — по содержимому снимка.
            CompiledSchema.FromText(schemaJson, null, Options, refreshCache: true));

    private CompiledSchema.Options Options => new(
        allowFileSystemAndHttpResolution: false,
        alwaysAssertFormat: true,
        additionalDocumentResolver: Resolve);

    // Схема другого типа по $id (ФОС ссылается на титул и лист согласования РПД).
    private bool Resolve(string uri, out ReadOnlyMemory<byte> utf8Json)
    {
        var type = registry.All.FirstOrDefault(t => t.SchemaId == uri);
        utf8Json = type is null ? default : Encoding.UTF8.GetBytes(type.SchemaJson);
        return type is not null;
    }
}
