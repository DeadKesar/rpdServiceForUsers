using System.Text.Json;

namespace RpdFos.Core.DocumentTypes;

/// <summary>
/// Тип документа: JSON Schema содержимого (2020-12 с x-расширениями), сопоставление закладок DOCX и лист
/// согласования по умолчанию. Тексты JSON — то, что копируется в шаблон при создании (снимок);
/// разобранные значения — только для чтения.
/// </summary>
public sealed record DocumentTypeDefinition(
    string Code, string Title, string SchemaJson, JsonElement Schema, string SchemaId,
    string PrintMappingJson, IReadOnlyDictionary<string, BookmarkTarget> PrintMapping,
    string SignaturesJson, IReadOnlyList<SignatureSeat> Signatures);

/// <summary>
/// Реестр типов документов. Структуры типов — файлы JSON во встроенных ресурсах ядра (DocumentTypes/*.json):
/// в прототипе их ведёт разработчик, редактор структуры для Администратора — перспектива.
/// Данные только для чтения — один экземпляр на приложение.
/// </summary>
public sealed class DocumentTypeRegistry
{
    public const string Rpd = "rpd", Fos = "fos";

    private readonly Dictionary<string, DocumentTypeDefinition> _types;

    public DocumentTypeRegistry()
    {
        _types = new[] { Load(Rpd, "Рабочая программа дисциплины"), Load(Fos, "Фонд оценочных средств") }
            .ToDictionary(t => t.Code);
    }

    public IReadOnlyCollection<DocumentTypeDefinition> All => _types.Values;

    public DocumentTypeDefinition Get(string code) => _types[code];

    public bool TryGet(string code, out DocumentTypeDefinition definition) => _types.TryGetValue(code, out definition!);

    /// <summary>Схема типа по её <c>$id</c> — для ссылок одной схемы на другую.</summary>
    public JsonElement? FindSchema(string id) =>
        _types.Values.FirstOrDefault(t => t.SchemaId == id) is { } t ? t.Schema : null;

    /// <summary>Есть ли в схеме типа поле содержимого с таким JSON Pointer.</summary>
    public bool FieldExists(string code, string pointer) =>
        SchemaInspector.FieldExists(Get(code).Schema, pointer, FindSchema);

    private static DocumentTypeDefinition Load(string code, string title)
    {
        var schemaJson = ReadResource($"{code}.schema.json");
        var schema = JsonDocument.Parse(schemaJson).RootElement.Clone();
        var mappingJson = ReadResource($"{code}.print-mapping.json");
        var mapping = JsonSerializer.Deserialize<Dictionary<string, BookmarkTarget>>(mappingJson, PrintMappingValidator.Json)!;
        var signaturesJson = ReadResource($"{code}.signatures.json");
        var signatures = JsonSerializer.Deserialize<List<SignatureSeat>>(signaturesJson, PrintMappingValidator.Json)!;
        return new DocumentTypeDefinition(code, title, schemaJson, schema, schema.GetProperty("$id").GetString()!,
            mappingJson, mapping, signaturesJson, signatures);
    }

    internal static string ReadResource(string name)
    {
        using var stream = typeof(DocumentTypeRegistry).Assembly.GetManifestResourceStream($"DocumentTypes.{name}")
            ?? throw new InvalidOperationException($"Нет встроенного ресурса DocumentTypes/{name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
