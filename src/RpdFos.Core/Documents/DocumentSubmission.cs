using System.Text.Json;
using RpdFos.Core.DocumentTypes;

namespace RpdFos.Core.Documents;

/// <summary>
/// Полная проверка документа к отправке: схема шаблона в режиме отправки, условные разделы по данным дисциплины
/// и именованные правила (<c>x-validation-rule</c>). Видимый условный раздел (лабораторные, практические, курсовой
/// проект) обязателен — хотя бы одна запись; скрытый не проверяется: в печатной форме вместо него стандартная
/// заглушка. Результат — ошибки и предупреждения; отправку блокируют только ошибки.
/// </summary>
public static class DocumentSubmission
{
    public const string EmptyVisibleSection = "Раздел предусмотрен учебным планом — добавьте хотя бы одну запись";

    public static bool HasErrors(IEnumerable<DocumentValidationError> issues) => issues.Any(i => i.Level == IssueLevels.Error);

    public static IReadOnlyList<DocumentValidationError> Check(string schemaJson, JsonElement payload, DisciplineFacts facts,
        IDocumentValidator validator, ValidationRuleSet rules, Func<string, JsonElement?> resolve,
        string targetStatus = DocumentStatuses.UnderReview)
    {
        var schema = JsonDocument.Parse(schemaJson).RootElement;
        var hidden = new HashSet<string>();
        var errors = new List<DocumentValidationError>();

        if (schema.TryGetProperty("properties", out var properties))
            foreach (var p in properties.EnumerateObject())
            {
                var root = schema;
                if (SchemaInspector.Deref(p.Value, ref root, resolve) is not { ValueKind: JsonValueKind.Object } section ||
                    !section.TryGetProperty("x-visible-if", out _))
                    continue;
                if (!ConditionalSections.IsVisible(section, facts)) hidden.Add(p.Name);
                else if (!payload.TryGetProperty(p.Name, out var value) || IsEmpty(value))
                    errors.Add(new DocumentValidationError($"/{p.Name}", EmptyVisibleSection));
            }

        errors.AddRange(validator.Validate(schemaJson, payload, ValidationMode.Submit, targetStatus)
            .Where(e => !hidden.Contains(Section(e.Path))));
        errors.AddRange(rules.Run(schema, payload, facts, resolve, hidden));
        return errors.Distinct().OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
    }

    /// <summary>Раздел (первый сегмент пути) ошибки.</summary>
    public static string Section(string path) => path.TrimStart('/').Split('/')[0];

    // Запись — элемент списка или заполненное поле раздела-объекта.
    private static bool IsEmpty(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array => value.GetArrayLength() == 0,
        JsonValueKind.Object => !value.EnumerateObject().Any(p => p.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            && !(p.Value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(p.Value.GetString()))
            && !(p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() == 0)),
        JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()),
        _ => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
    };
}
