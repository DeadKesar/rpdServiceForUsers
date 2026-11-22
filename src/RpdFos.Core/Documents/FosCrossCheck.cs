using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.DocumentTypes;

namespace RpdFos.Core.Documents;

/// <summary>
/// Сквозная проверка РПД ↔ ФОС (этап 7, B8; FR-VAL-04, FR-VAL-07, FR-VAL-09, FR-VAL-10): замечания правил ФОС к содержимому
/// ФОС по связанной РПД как она есть сейчас. Нужна там, где ФОС не открыт целиком, — в строке «ФОС: статус, расхождений N» на
/// странице РПД; в самом ФОС те же замечания приходят вместе с разделами (<see cref="DocumentMetadata"/>). Все правила ФОС —
/// сквозные: любое замечание с именем правила в ФОС — расхождение между ФОС и РПД или внутри ФОС.
/// </summary>
public sealed class FosCrossCheck(AppDbContext db, DocumentTypeRegistry registry, ValidationRuleSet rules)
{
    public async Task<IReadOnlyList<DocumentValidationError>> RunAsync(Document fos, CancellationToken ct = default)
    {
        var schemaJson = await db.DocumentTemplates.IgnoreQueryFilters().Where(t => t.Id == fos.TemplateId)
            .Select(t => t.Schema).SingleAsync(ct);
        var facts = await DisciplineFacts.LoadForAsync(db, fos, ct);
        if (facts is null) return [];
        return rules.Run(JsonDocument.Parse(schemaJson).RootElement, JsonDocument.Parse(fos.Payload).RootElement, facts,
                registry.FindSchema, new HashSet<string>())
            .Where(i => i.Rule is not null).ToList();
    }

    /// <summary>Расхождений — предупреждений сквозной проверки; ошибка отправки («РПД — черновик») расхождением не считается.</summary>
    public static int Discrepancies(IEnumerable<DocumentValidationError> issues) => issues.Count(i => i.Level == IssueLevels.Warning);
}
