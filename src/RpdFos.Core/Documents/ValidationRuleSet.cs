using System.Text.Json;
using RpdFos.Core.DocumentTypes;

namespace RpdFos.Core.Documents;

/// <summary>
/// Место, к которому привязано правило (<c>x-validation-rule</c> у поля схемы), и всё, что правилу нужно знать:
/// значение поля, содержимое документа целиком, данные дисциплины из плана и матрицы, текущее время.
/// </summary>
public sealed record RuleContext(string Path, JsonElement Value, JsonElement Payload, DisciplineFacts Facts, DateTimeOffset Now);

/// <summary>
/// Именованное правило проверки содержимого (каталог правил, раздел 2.x.5 проекта). Уровень задаёт само правило:
/// ошибка блокирует отправку, предупреждение — нет.
/// </summary>
public interface IValidationRule
{
    string Name { get; }
    string Level { get; }
    IEnumerable<DocumentValidationError> Check(RuleContext context);
}

/// <summary>Настройки правил — из конфигурации (раздел <c>ValidationRules</c>).</summary>
public sealed class ValidationRuleSettings
{
    /// <summary>Основная литература старше этого числа лет — предупреждение (<c>literature_main_recent</c>).</summary>
    public int LiteratureMaxAgeYears { get; set; } = 10;
}

/// <summary>
/// Реестр правил: обходит схему документа и для каждого поля с <c>x-validation-rule</c> (строка или список имён)
/// вызывает правило, если поле заполнено и его раздел предусмотрен планом. Правила, которых ещё нет в коде,
/// пропускаются: имя в схеме — заявка на правило, а не ошибка шаблона.
/// </summary>
public sealed class ValidationRuleSet(IEnumerable<IValidationRule> rules, TimeProvider clock)
{
    private readonly Dictionary<string, IValidationRule> _rules = rules.ToDictionary(r => r.Name);

    public IReadOnlyCollection<string> Names => _rules.Keys;

    public IReadOnlyList<DocumentValidationError> Run(JsonElement schema, JsonElement payload, DisciplineFacts facts,
        Func<string, JsonElement?> resolve, IReadOnlySet<string> hiddenSections)
    {
        var issues = new List<DocumentValidationError>();
        var root = schema;
        Walk(SchemaInspector.Deref(schema, ref root, resolve), root, payload, "", 0);
        return issues;

        void Walk(JsonElement? node, JsonElement currentRoot, JsonElement value, string path, int depth)
        {
            if (node is not { ValueKind: JsonValueKind.Object } n || depth > 16) return;
            if (path.Length > 0 && n.TryGetProperty("x-validation-rule", out var names))
                foreach (var name in Names(names))
                    if (_rules.TryGetValue(name, out var rule))
                        issues.AddRange(rule.Check(new RuleContext(path, value, payload, facts, clock.GetUtcNow()))
                            .Select(i => i with { Level = rule.Level }));

            if (value.ValueKind != JsonValueKind.Object || !n.TryGetProperty("properties", out var props)) return;
            foreach (var p in props.EnumerateObject())
            {
                if (depth == 0 && hiddenSections.Contains(p.Name) || !value.TryGetProperty(p.Name, out var child)) continue;
                var childRoot = currentRoot;
                Walk(SchemaInspector.Deref(p.Value, ref childRoot, resolve), childRoot, child, $"{path}/{p.Name}", depth + 1);
            }
        }

        static IEnumerable<string> Names(JsonElement names) => names.ValueKind == JsonValueKind.Array
            ? names.EnumerateArray().Select(n => n.GetString() ?? "")
            : [names.GetString() ?? ""];
    }
}
