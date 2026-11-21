using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Directory;

namespace RpdFos.Core.Documents;

public enum ImageUploadProblem { Invalid, TooLarge, LimitReached }

/// <summary>Итог загрузки: рисунок документа или причина отказа (422).</summary>
public sealed record ImageUploadResult(Guid? FileId, ImageUploadProblem? Problem, string? Detail);

/// <summary>
/// Рисунки документов (этап 7, B4а; FR-DOC-21 в части изображений). Рисунок — запись <c>uploaded_files</c> с содержимым в
/// базе (невосстановимый файл, как исходные данные), связь с документом — <c>document_images</c>. Рисунок читает тот, кто
/// читает хотя бы один связанный документ: правило открытия документа то же, что у самого документа
/// (<see cref="DocumentVisibility"/>). Файл один на несколько документов: «Создать на основе» и вставка рисунка из чужого
/// документа, который автор читает, добавляют связь, файл не копируется.
/// </summary>
public sealed partial class DocumentImages(
    AppDbContext db, DirectoryAccounts directory, DocumentApproval approval, DisciplineAssignments assignments, LinkedDocuments links,
    TimeProvider clock)
{
    private const string AddressPrefix = "/api/images/";

    /// <summary>Постоянный адрес рисунка в разметке: без номера документа и без токена.</summary>
    public static string Address(Guid fileId) => AddressPrefix + fileId.ToString("D");

    [GeneratedRegex(@"/api/images/([0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12})", RegexOptions.CultureInvariant)]
    private static partial Regex AddressInText();

    /// <summary>Номера рисунков, на которые ссылается строка (разметка).</summary>
    public static IEnumerable<Guid> IdsIn(string? text) =>
        string.IsNullOrEmpty(text) ? [] : AddressInText().Matches(text).Select(m => Guid.Parse(m.Groups[1].Value));

    /// <summary>Номера рисунков во всех строках содержимого.</summary>
    public static HashSet<Guid> IdsIn(JsonNode? node)
    {
        var found = new HashSet<Guid>();
        Collect(node, found, 0);
        return found;
    }

    private static void Collect(JsonNode? node, HashSet<Guid> found, int depth)
    {
        if (node is null || depth > 32) return;
        switch (node)
        {
            case JsonValue v when v.TryGetValue(out string? text): found.UnionWith(IdsIn(text)); break;
            case JsonObject o: foreach (var p in o) Collect(p.Value, found, depth + 1); break;
            case JsonArray a: foreach (var item in a) Collect(item, found, depth + 1); break;
        }
    }

    /// <summary>
    /// Загрузка рисунка в документ: проверка по сигнатуре, снятие метаданных, лимиты. Тот же файл в документе (по
    /// <c>checksum</c> очищенного содержимого) второй записи не даёт. Сохраняет сама.
    /// </summary>
    public async Task<ImageUploadResult> AddAsync(Document document, Guid uploaderId, string? fileName, byte[] content, CancellationToken ct = default)
    {
        if (content.Length > ImageFiles.MaxBytes)
            return new(null, ImageUploadProblem.TooLarge, $"Рисунок больше {ImageFiles.MaxBytes / 1024 / 1024} МБ.");
        var (image, problem) = ImageFiles.Inspect(content);
        if (image is null) return new(null, ImageUploadProblem.Invalid, problem);

        var checksum = Convert.ToHexStringLower(SHA256.HashData(image.Content));
        var existing = await (from link in db.DocumentImages
                              join stored in db.UploadedFiles on link.FileId equals stored.Id
                              where link.DocumentId == document.Id && stored.FileType == UploadedFileTypes.Image && stored.Checksum == checksum
                              select (Guid?)stored.Id).FirstOrDefaultAsync(ct);
        if (existing is { } same) return new(same, null, null);

        if (await db.DocumentImages.CountAsync(i => i.DocumentId == document.Id, ct) >= ImageFiles.MaxPerDocument)
            return new(null, ImageUploadProblem.LimitReached, $"В документе уже {ImageFiles.MaxPerDocument} рисунков: больше не вставляется.");

        var now = clock.GetUtcNow();
        var file = new UploadedFile
        {
            UploadedBy = uploaderId, FileType = UploadedFileTypes.Image, OriginalName = Name(fileName, image.Extension),
            Content = image.Content, MimeType = image.MimeType, SizeBytes = image.Content.Length, Checksum = checksum, UploadedAt = now
        };
        db.UploadedFiles.Add(file);
        db.DocumentImages.Add(new DocumentImage { DocumentId = document.Id, FileId = file.Id, LinkedAt = now });
        await db.SaveChangesAsync(ct);
        return new(file.Id, null, null);
    }

    // Имя нужно только человеку (журнал, выгрузка): без пути и управляющих символов, в пределах колонки.
    private static string Name(string? fileName, string extension)
    {
        var name = Path.GetFileName((fileName ?? "").Replace('\\', '/'));
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length == 0) name = "image" + extension;
        return name.Length > 200 ? name[^200..] : name;
    }

    /// <summary>
    /// Какие из рисунков можно оставить в тексте документа, который правит сотрудник: уже связанные с ним и рисунки
    /// документов, которые сотрудник читает (вставка из буфера — те же файлы). Остальные не существуют или чужие: в тексте их
    /// быть не должно. Вторым значением — те, у которых связи с этим документом ещё нет.
    /// </summary>
    public async Task<(HashSet<Guid> Allowed, List<Guid> ToLink)> AllowedAsync(
        Document document, Guid editorId, IReadOnlyList<string>? roles, IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        var allowed = new HashSet<Guid>();
        var toLink = new List<Guid>();
        if (ids.Count == 0) return (allowed, toLink);

        var linked = await db.DocumentImages.Where(i => i.DocumentId == document.Id && ids.Contains(i.FileId))
            .Select(i => i.FileId).ToListAsync(ct);
        allowed.UnionWith(linked);

        var others = ids.Except(allowed).ToList();
        if (others.Count == 0) return (allowed, toLink);
        var images = await db.UploadedFiles.Where(f => others.Contains(f.Id) && f.FileType == UploadedFileTypes.Image)
            .Select(f => f.Id).ToListAsync(ct);
        if (images.Count == 0) return (allowed, toLink);

        roles ??= await directory.RolesOfActiveAsync(editorId, ct);
        if (roles is null) return (allowed, toLink);
        foreach (var id in images)
            if (await CanReadAsync(id, editorId, roles, ct))
            {
                allowed.Add(id);
                toLink.Add(id);
            }
        return (allowed, toLink);
    }

    /// <summary>Связывает рисунки с документом; уже связанные пропускаются. Сохраняет вызывающий.</summary>
    public async Task LinkAsync(Guid documentId, IEnumerable<Guid> fileIds, CancellationToken ct = default)
    {
        var wanted = fileIds.Distinct().ToList();
        if (wanted.Count == 0) return;
        var linked = await db.DocumentImages.Where(i => i.DocumentId == documentId && wanted.Contains(i.FileId))
            .Select(i => i.FileId).ToListAsync(ct);
        var now = clock.GetUtcNow();
        foreach (var id in wanted.Except(linked))
            db.DocumentImages.Add(new DocumentImage { DocumentId = documentId, FileId = id, LinkedAt = now });
    }

    /// <summary>
    /// Новый документ «на основе»: рисунки из его содержимого — те же файлы основы, связи добавляются (этап 7, B4а). Содержимое
    /// собрано из основы самой системой, поэтому права не проверяются; несуществующие номера отбрасываются.
    /// </summary>
    public async Task LinkFromContentAsync(Document document, JsonNode? payload, CancellationToken ct = default)
    {
        var ids = IdsIn(payload);
        if (ids.Count == 0) return;
        var images = await db.UploadedFiles.Where(f => ids.Contains(f.Id) && f.FileType == UploadedFileTypes.Image)
            .Select(f => f.Id).ToListAsync(ct);
        await LinkAsync(document.Id, images, ct);
    }

    /// <summary>Есть ли рисунок с таким номером: файл другого типа (исходные данные) рисунком не считается.</summary>
    public Task<bool> ExistsAsync(Guid fileId, CancellationToken ct = default) =>
        db.UploadedFiles.AnyAsync(f => f.Id == fileId && f.FileType == UploadedFileTypes.Image, ct);

    /// <summary>
    /// Рисунок, если сотрудник читает хотя бы один связанный с ним документ (правило открытия документа,
    /// <see cref="DocumentVisibility"/>); иначе null. Содержимое читается только у прошедшего проверку.
    /// </summary>
    public async Task<UploadedFile?> ReadAsync(Guid fileId, Guid employeeId, IReadOnlyList<string>? roles, CancellationToken ct = default)
    {
        if (!await ExistsAsync(fileId, ct)) return null;
        if ((roles ?? await directory.RolesOfActiveAsync(employeeId, ct)) is not { } known || !await CanReadAsync(fileId, employeeId, known, ct))
            return null;
        return await db.UploadedFiles.AsNoTracking().SingleAsync(f => f.Id == fileId, ct);
    }

    private async Task<bool> CanReadAsync(Guid fileId, Guid employeeId, IReadOnlyList<string> roles, CancellationToken ct)
    {
        // Удалённые документы скрыты фильтром запросов: рисунок, оставшийся только в них, не читается.
        var documents = await db.Documents.AsNoTracking()
            .Where(d => db.DocumentImages.Any(i => i.FileId == fileId && i.DocumentId == d.Id)).ToListAsync(ct);
        foreach (var document in documents)
            if (await DocumentVisibility.CanOpenAsync(approval, assignments, document, employeeId, roles, ct, links))
                return true;
        return false;
    }
}
