using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.DocumentTypes;

namespace RpdFos.Core.Documents;

public enum SectionUpdateProblem { UnknownSection, HiddenSection, ReadOnlyChanged, Invalid, ReturnNotConfirmed }

/// <summary>
/// Итог изменения раздела: отказ с ошибками по полям или новое состояние раздела — и всех разделов: правка одного
/// меняет заполненность других (тематический план сверяется с трудоёмкостью), исправление трудоёмкости — состав
/// разделов и скрытые места (этап 5.2).
/// </summary>
public sealed record SectionUpdateResult(SectionUpdateProblem? Problem, IReadOnlyList<DocumentValidationError> Errors, SectionState? Section,
    IReadOnlyList<SectionState>? Sections = null, IReadOnlyList<string>? HiddenPaths = null, JsonElement? Payload = null);

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

    public const string PlanOnlyChanged = "В этом статусе исправляются только поля из учебного плана";

    public const string ReturnedAfterPlanCorrection = "возвращён после исправления данных плана";

    /// <param name="scope">
    /// Что можно менять (<see cref="DocumentTransitions.EditScope"/>): поля из плана — только методисту и администратору;
    /// каждое их изменение — запись в журнале аудита (кто, когда, было, стало).
    /// </param>
    /// <param name="confirmReturn">
    /// Исправление принятого документа, после которого полная проверка находит ошибки, возвращает его автору — только
    /// с подтверждением исправляющего; без него — <see cref="SectionUpdateProblem.ReturnNotConfirmed"/> с причинами.
    /// </param>
    public async Task<SectionUpdateResult> UpdateSectionAsync(
        Document document, string section, JsonElement value, Guid editorId, CancellationToken ct = default,
        DocumentEditScope scope = DocumentEditScope.Content, bool confirmReturn = false)
    {
        var schemaJson = await SchemaAsync(document, ct);
        var schema = JsonDocument.Parse(schemaJson).RootElement;
        var root = schema;
        if (!schema.TryGetProperty("properties", out var properties) || !properties.TryGetProperty(section, out var raw) ||
            SchemaInspector.Deref(raw, ref root, registry.FindSchema) is not { ValueKind: JsonValueKind.Object } sectionSchema)
            return new(SectionUpdateProblem.UnknownSection, [], null);

        var facts = (await DisciplineFacts.LoadAsync(db, document.DisciplineId, ct))!;
        if (!ConditionalSections.IsVisible(sectionSchema, DocumentSemesters.Of(JsonDocument.Parse(document.Payload).RootElement, facts)))
            return new(SectionUpdateProblem.HiddenSection, [], null);

        var payload = JsonNode.Parse(document.Payload)!.AsObject();
        var updated = payload.DeepClone().AsObject();
        // Очищенный раздел (null) уходит из содержимого: в схеме пустых значений нет.
        if (Sanitize(JsonNode.Parse(value.GetRawText()), sectionSchema, root, 0) is { } sanitized) updated[section] = sanitized;
        else updated.Remove(section);

        // Поля из плана и закрытые статусом: присланное должно совпасть с тем, что уже в документе. Поля плана
        // открыты исправляющему (методист, администратор), закрытые статусом — нет.
        bool InSection(string p) => p == $"/{section}" || p.StartsWith($"/{section}/", StringComparison.Ordinal);
        var planPaths = PlanFields.Paths(schema, registry.FindSchema).Where(InSection).ToList();
        var locked = DocumentMetadata.ReadOnly(schema, document.Status, registry.FindSchema).Where(InSection)
            .Where(p => scope == DocumentEditScope.Content || !planPaths.Contains(p));
        var changed = locked
            .Where(p => !JsonNode.DeepEquals(At(payload, p), At(updated, p)))
            .Select(p => new DocumentValidationError(p, ReadOnlyChanged)).ToList();
        if (changed.Count > 0) return new(SectionUpdateProblem.ReadOnlyChanged, changed, null);
        // После приёма и у администратора — только поля плана: остальное содержимое раздела должно совпасть.
        if (scope == DocumentEditScope.PlanOnly && !JsonNode.DeepEquals(Without(payload[section], section, planPaths),
                Without(updated[section], section, planPaths)))
            return new(SectionUpdateProblem.ReadOnlyChanged, [new DocumentValidationError($"/{section}", PlanOnlyChanged)], null);
        var corrections = scope == DocumentEditScope.Content ? [] : PlanFields.Changes(payload, updated, planPaths).ToList();
        // Производное следует за исправлением (этап 5.2): вид курсового проектирования — за видами аттестации,
        // контактная работа — за часами занятий. Клиент получит содержимое обратно: он этих изменений не видел.
        var followed = scope == DocumentEditScope.Content ? []
            : PlanFields.FollowCourseWorkKind(schema, payload, updated, facts, registry.FindSchema)
                .Concat(PlanFields.FollowContactHours(payload, updated)).ToList();
        corrections.AddRange(followed);

        var updatedJson = updated.ToJsonString(Json);
        var element = JsonDocument.Parse(updatedJson).RootElement;
        var errors = validator.Validate(schemaJson, element, ValidationMode.Draft);
        if (errors.Count > 0) return new(SectionUpdateProblem.Invalid, errors, null);
        // Правила об ошибках в самих данных (год издания в будущем) не дают сохранить и черновик — в этом разделе.
        var wrong = rules.Run(schema, element, facts, registry.FindSchema, new HashSet<string>(), onSave: true)
            .Where(e => DocumentSubmission.Section(e.Path) == section).ToList();
        if (wrong.Count > 0) return new(SectionUpdateProblem.Invalid, wrong, null);

        // Принятый документ после исправления должен оставаться полным: иначе — возврат автору, с подтверждением.
        var broken = corrections.Count > 0 && DocumentTransitions.CanReturnAfterPlanCorrection(document.Status)
            ? DocumentSubmission.Check(schemaJson, element, facts, validator, rules, registry.FindSchema)
                .Where(e => e.Level == IssueLevels.Error).ToList()
            : [];
        if (broken.Count > 0 && !confirmReturn) return new(SectionUpdateProblem.ReturnNotConfirmed, broken, null);

        if (scope == DocumentEditScope.PlanOnly) document.CorrectPlanFields(updatedJson);
        else if (document.Edit(updatedJson) is { } previous) history.StatusChanged(document, previous, editorId);
        var now = clock.GetUtcNow();
        if (broken.Count > 0) ReturnToAuthor(document, broken, schema, editorId, now);
        foreach (var c in corrections)
            db.AuditLog.Add(new AuditLogEntry
            {
                UserId = editorId, EventType = AuditEvents.PlanFieldCorrected, EntityType = AuditEntities.Document,
                EntityId = document.Id, OccurredAt = now,
                Payload = new JsonObject { ["path"] = c.Path, ["before"] = c.Before, ["after"] = c.After }.ToJsonString(Json)
            });
        await history.SnapshotAsync(document, editorId, broken.Count > 0 ? VersionTriggers.StatusChange : VersionTriggers.AutoSave, ct);

        var metadata = DocumentMetadata.Build(schemaJson, element, document.Status, facts, validator, rules, registry.FindSchema);
        return new(null, [], metadata.Sections.Single(s => s.Key == section), metadata.Sections, metadata.HiddenPaths,
            followed.Count > 0 ? element : null);
    }

    // «Принят → На доработке»: замечание от исправившего к месту первой ошибки раздела — по одному на раздел, текст —
    // ошибки раздела; в истории — причина возврата.
    private void ReturnToAuthor(Document document, IReadOnlyList<DocumentValidationError> errors, JsonElement schema, Guid editorId,
        DateTimeOffset now)
    {
        history.StatusChanged(document, document.ReturnAfterPlanCorrection(), editorId, ReturnedAfterPlanCorrection);
        foreach (var group in errors.GroupBy(e => DocumentSubmission.Section(e.Path)))
        {
            var place = group.First().Path;
            if (SchemaInspector.SubschemaAt(schema, place, registry.FindSchema) is null) place = $"/{group.Key}";
            db.DocumentComments.Add(new DocumentComment
            {
                DocumentId = document.Id, AuthorId = editorId, FieldPath = place, CreatedAt = now,
                Text = $"После исправления данных плана: {string.Join("; ", group.Select(e => e.Message).Distinct())}."
            });
        }
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

    // Раздел без полей из плана — чтобы сравнить остальное содержимое.
    private static JsonNode? Without(JsonNode? sectionValue, string section, IEnumerable<string> planPaths)
    {
        if (sectionValue?.DeepClone() is not JsonObject copy) return sectionValue?.DeepClone();
        foreach (var path in planPaths)
        {
            var segments = path.TrimStart('/').Split('/')[1..];
            if (segments.Length == 0) return null;
            JsonNode? parent = copy;
            foreach (var segment in segments[..^1]) parent = (parent as JsonObject)?[segment];
            (parent as JsonObject)?.Remove(segments[^1]);
        }
        return copy;
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
