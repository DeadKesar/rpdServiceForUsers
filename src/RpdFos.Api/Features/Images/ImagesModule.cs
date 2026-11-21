using System.Security.Claims;
using Carter;
using Microsoft.AspNetCore.Http.Features;
using RpdFos.Api.Features.Documents;
using RpdFos.Api.Infrastructure;
using RpdFos.Core.Data;
using RpdFos.Core.Directory;
using RpdFos.Core.Documents;

namespace RpdFos.Api.Features.Images;

/// <summary>
/// Срез «Рисунки в тексте документа» (этап 7, B4а; FR-DOC-21 в части изображений). Загрузка — кто может править документ
/// (автор; методист на проверке): PNG и JPEG по сигнатуре, до 2 МБ, до 100 на документ, метаданные снимаются, отказ — 422.
/// Ответ — постоянный адрес <c>/api/images/{id}</c> без номера документа и без токена. Чтение — тому, кто читает хотя бы один
/// документ с этим рисунком. Браузер грузит <c>img</c> без заголовка с токеном, поэтому чтение разрешено и по cookie входа
/// того же хоста (схема, на которой стоит страница входа), и по токену.
/// </summary>
public sealed class ImagesModule : ICarterModule
{
    // Запас на границы и заголовки формы сверх предельного размера рисунка.
    private const long FormOverhead = 64 * 1024;

    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/documents/{id:guid}/images", Upload)
            .RequireAuthorization(Policies.DocumentAuthors)
            .WithTags("Images")
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(ImageFiles.MaxBytes + FormOverhead))
            .DisableAntiforgery();
        app.MapGet("/api/images/{id:guid}", Read)
            .RequireAuthorization(Policies.ImageReaders)
            .WithTags("Images");
    }

    private static async Task<IResult> Upload(
        Guid id, HttpContext http, AppDbContext db, DirectoryAccounts directory, DocumentReview review, DocumentImages images,
        CancellationToken ct)
    {
        var (document, editorId, _, refusal) = await DocumentsModule.SectionEditorAsync(id, http, db, directory, review, ct);
        if (refusal is not null) return refusal;

        if (!http.Request.HasFormContentType)
            return Refuse("Рисунок присылается файлом в форме (multipart/form-data, поле file).");
        // Пустая или оборванная форма не разбирается — это неверные данные, а не ошибка сервера.
        IFormCollection form;
        try
        {
            form = await http.Request.ReadFormAsync(ct);
        }
        catch (Exception e) when (e is InvalidDataException or BadHttpRequestException)
        {
            return Refuse("Форму с рисунком не удалось разобрать.");
        }
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null) return Refuse("В запросе нет файла рисунка.");
        if (file.Length > ImageFiles.MaxBytes) return Refuse($"Рисунок больше {ImageFiles.MaxBytes / 1024 / 1024} МБ.");

        byte[] content;
        await using (var stream = file.OpenReadStream())
        using (var buffer = new MemoryStream())
        {
            await stream.CopyToAsync(buffer, ct);
            content = buffer.ToArray();
        }
        var result = await images.AddAsync(document!, editorId, file.FileName, content, ct);
        return result.FileId is { } fileId
            ? Results.Ok(new ImageUploaded(DocumentImages.Address(fileId), fileId))
            : Refuse(result.Detail!);
    }

    private static IResult Refuse(string detail) =>
        Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Рисунок не принят", detail: detail);

    private static async Task<IResult> Read(
        Guid id, HttpContext http, DirectoryAccounts directory, DocumentImages images, CancellationToken ct)
    {
        // Токен несёт утверждение sub, cookie входа — идентификатор учётной записи: это одно и то же значение.
        var subject = http.User.FindFirst(DirectoryAccounts.SubjectClaim)?.Value ?? http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (await directory.FindIdBySubjectAsync(subject, ct) is not Guid me)
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Нет записи в справочнике сотрудников",
                detail: "Учётная запись не связана со справочником сотрудников — обратитесь к администратору.");
        if (!await images.ExistsAsync(id, ct))
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Рисунок не найден",
                detail: "Рисунка нет или он удалён вместе с документом.");
        if (await images.ReadAsync(id, me, DocumentViewer.RolesOf(http.User), ct) is not { Content: { } content } file)
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Чужой рисунок",
                detail: "Рисунок читает тот, кто читает документ, в котором он вставлен.");

        // Содержимое по номеру не меняется: браузер хранит копию у себя (private — общий кэш прокси её не берёт).
        http.Response.Headers.CacheControl = "private, max-age=3600";
        return Results.File(content, file.MimeType);
    }
}

public sealed record ImageUploaded(string Url, Guid Id);
