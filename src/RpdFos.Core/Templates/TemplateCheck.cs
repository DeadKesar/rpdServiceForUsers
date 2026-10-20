using System.Text.Json;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.DocumentTypes;

namespace RpdFos.Core.Templates;

/// <summary>
/// Шаблон глазами проверки: закладки загруженного шаблона оформления, его сопоставление, лист согласования
/// и ошибки проверки перед публикацией. Пустой список ошибок — шаблон можно публиковать.
/// </summary>
public sealed record TemplateInspection(
    IReadOnlyList<BookmarkGroup> Bookmarks,
    IReadOnlyDictionary<string, BookmarkTarget> Mapping,
    IReadOnlyList<SignatureSeat> Signatures,
    IReadOnlyList<string> Errors)
{
    public bool CanPublish => Errors.Count == 0;
}

/// <summary>
/// Проверка шаблона перед публикацией (FR-TPL-11, FR-TPL-13): учебный план привязан, DOCX-шаблон оформления
/// загружен, каждая его закладка сопоставлена и каждая цель существует, в листе согласования — только
/// известные правила. Без базы данных: содержимое файла передаёт вызывающий.
/// </summary>
public static class TemplateCheck
{
    public static TemplateInspection Inspect(DocumentTemplate template, byte[]? printTemplate, DocumentTypeRegistry registry)
    {
        var errors = new List<string>();
        if (template.CurriculumPlanId is null)
            errors.Add("Не привязан учебный план.");

        IReadOnlyList<BookmarkGroup> bookmarks = [];
        if (printTemplate is null)
            errors.Add("Не загружен DOCX-шаблон оформления.");
        else
        {
            bookmarks = DocxBookmarks.Read(printTemplate);
            if (bookmarks.Count == 0)
                errors.Add($"В шаблоне оформления нет закладок {DocxBookmarks.Prefix}.");
        }

        var mapping = template.PrintMapping is null
            ? new Dictionary<string, BookmarkTarget>()
            : PrintMappingJson.ReadMapping(template.PrintMapping);
        var signatures = PrintMappingJson.ReadSignatures(template.SignaturesConfig);

        if (printTemplate is not null)
        {
            // Поля — по снимку схемы шаблона; ссылки на схемы других типов ($ref) — по реестру.
            var schema = JsonDocument.Parse(template.Schema).RootElement;
            errors.AddRange(PrintMappingValidator.Validate(
                pointer => SchemaInspector.FieldExists(schema, pointer, registry.FindSchema),
                mapping, signatures, bookmarks.Select(b => b.BaseName)));
        }
        errors.AddRange(PrintMappingValidator.ValidateSignatures(signatures));

        return new TemplateInspection(bookmarks, mapping, signatures, errors);
    }

    /// <summary>
    /// Сопоставление по умолчанию для загруженного шаблона оформления: цели из реестра типов — для закладок,
    /// которые в нём есть. Закладки, которых нет в реестре, остаются несопоставленными — их покажет проверка.
    /// </summary>
    public static string DefaultMapping(DocumentTypeDefinition type, IEnumerable<BookmarkGroup> bookmarks) =>
        PrintMappingJson.Write(bookmarks
            .Where(b => type.PrintMapping.ContainsKey(b.BaseName))
            .ToDictionary(b => b.BaseName, b => type.PrintMapping[b.BaseName]));
}
