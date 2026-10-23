using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.DocumentTypes;

namespace RpdFos.Core.Documents;

public enum SectionUpdateProblem { UnknownSection, HiddenSection, ReadOnlyChanged, Invalid }

/// <summary>Итог изменения раздела: отказ с ошибками по полям или новое состояние раздела.</summary>
public sealed record SectionUpdateResult(SectionUpdateProblem? Problem, IReadOnlyList<DocumentValidationError> Errors, SectionState? Section);

/// <summary>
/// Изменение и сохранение документа. Раздел проверяется в режиме черновика, форматированный текст санитизируется,
/// поля из учебного плана не меняются; нормализованные таблицы, снимок версии и история статусов попадают в то же
/// сохранение, что и содержимое. Сохраняет вызывающий — с версией клиента (оптимистичная блокировка).
/// </summary>
public sealed class DocumentEditing(
    AppDbContext db, DocumentTypeRegistry registry, IDocumentValidator validator, RichTextSanitizer sanitizer,
    ValidationRuleSet rules, DocumentHistory history, TimeProvider clock)
{
    public const string ReadOnlyChanged = "Поле из учебного плана — изменять нельзя";

    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public async Task<SectionUpdateResult> UpdateSectionAsync(
        Document document, string section, JsonElement value, Guid editorId, CancellationToken ct = default)
    {
        var schemaJson = await SchemaAsync(document, ct);
        var schema = JsonDocument.Parse(schemaJson).RootElement;
        var root = schema;
        if (!schema.TryGetProperty("properties", out var properties) || !properties.TryGetProperty(section, out var raw) ||
            SchemaInspector.Deref(raw, ref root, registry.FindSchema) is not { ValueKind: JsonValueKind.Object } sectionSchema)
            return new(SectionUpdateProblem.UnknownSection, [], null);

        var facts = (await DisciplineFacts.LoadAsync(db, document.DisciplineId, ct))!;
        if (!ConditionalSections.IsVisible(sectionSchema, facts))
            return new(SectionUpdateProblem.HiddenSection, [], null);

        var payload = JsonNode.Parse(document.Payload)!.AsObject();
        var updated = payload.DeepClone().AsObject();
        // Очищенный раздел (null) уходит из содержимого: в схеме пустых значений нет.
        if (Sanitize(JsonNode.Parse(value.GetRawText()), sectionSchema, root, 0) is { } sanitized) updated[section] = sanitized;
        else updated.Remove(section);

        // Поля из плана и закрытые статусом: присланное должно совпасть с тем, что уже в документе.
        var changed = DocumentMetadata.ReadOnly(schema, document.Status, registry.FindSchema)
            .Where(p => p == $"/{section}" || p.StartsWith($"/{section}/", StringComparison.Ordinal))
            .Where(p => !JsonNode.DeepEquals(At(payload, p), At(updated, p)))
            .Select(p => new DocumentValidationError(p, ReadOnlyChanged)).ToList();
        if (changed.Count > 0) return new(SectionUpdateProblem.ReadOnlyChanged, changed, null);

        var updatedJson = updated.ToJsonString(Json);
        var element = JsonDocument.Parse(updatedJson).RootElement;
        var errors = validator.Validate(schemaJson, element, ValidationMode.Draft);
        if (errors.Count > 0) return new(SectionUpdateProblem.Invalid, errors, null);
        // Правила об ошибках в самих данных (год издания в будущем) не дают сохранить и черновик — в этом разделе.
        var wrong = rules.Run(schema, element, facts, registry.FindSchema, new HashSet<string>(), onSave: true)
            .Where(e => DocumentSubmission.Section(e.Path) == section).ToList();
        if (wrong.Count > 0) return new(SectionUpdateProblem.Invalid, wrong, null);

        var previous = document.Edit(updatedJson);
        if (previous is not null) history.StatusChanged(document, previous, editorId);
        await history.SnapshotAsync(document, editorId, VersionTriggers.AutoSave, ct);

        var metadata = DocumentMetadata.Build(schemaJson, element, document.Status, facts, validator, rules, registry.FindSchema);
        return new(null, [], metadata.Sections.Single(s => s.Key == section));
    }

    /// <summary>«Сохранить»: новый снимок версии; черновик становится сохранённым черновиком.</summary>
    public async Task SaveAsync(Document document, Guid editorId, CancellationToken ct = default)
    {
        var previous = document.Save();
        if (previous is not null) history.StatusChanged(document, previous, editorId);
        await history.SnapshotAsync(document, editorId, VersionTriggers.ManualSave, ct);
    }

    /// <summary>Мягкое удаление с записью в истории статусов.</summary>
    public void Delete(Document document, Guid editorId) =>
        history.StatusChanged(document, document.Delete(clock.GetUtcNow()), editorId);

    private Task<string> SchemaAsync(Document document, CancellationToken ct) =>
        db.DocumentTemplates.IgnoreQueryFilters().Where(t => t.Id == document.TemplateId).Select(t => t.Schema).SingleAsync(ct);

    // Строки с x-ui-widget: richtext — через санитайзер (NFR-SEC-10); клиенту не доверяем.
    private JsonNode? Sanitize(JsonNode? node, JsonElement schema, JsonElement root, int depth)
    {
        if (node is null || depth > 16) return node;
        if (node is JsonValue v && v.TryGetValue(out string? text) && schema.TryGetProperty("x-ui-widget", out var widget) &&
            widget.GetString() == "richtext")
            return sanitizer.Sanitize(text);
        if (node is JsonObject o && schema.TryGetProperty("properties", out var props))
            foreach (var p in props.EnumerateObject())
            {
                var childRoot = root;
                if (o[p.Name] is { } child && SchemaInspector.Deref(p.Value, ref childRoot, registry.FindSchema) is { } childSchema)
                    o[p.Name] = Sanitize(child.DeepClone(), childSchema, childRoot, depth + 1);
            }
        if (node is JsonArray a && schema.TryGetProperty("items", out var items))
        {
            var itemsRoot = root;
            if (SchemaInspector.Deref(items, ref itemsRoot, registry.FindSchema) is { } itemSchema)
                for (var i = 0; i < a.Count; i++)
                    a[i] = Sanitize(a[i]?.DeepClone(), itemSchema, itemsRoot, depth + 1);
        }
        return node;
    }

    private static JsonNode? At(JsonNode node, string pointer)
    {
        JsonNode? current = node;
        foreach (var raw in pointer.TrimStart('/').Split('/'))
        {
            var segment = raw.Replace("~1", "/").Replace("~0", "~");
            current = current switch
            {
                JsonObject o => o[segment],
                JsonArray a when int.TryParse(segment, out var i) && i < a.Count => a[i],
                _ => null
            };
            if (current is null) return null;
        }
        return current;
    }
}
