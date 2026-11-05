using System.Security.Cryptography;
using Carter;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using RpdFos.Api.Infrastructure;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Directory;
using RpdFos.Core.Import;

namespace RpdFos.Api.Features.Directory;

/// <summary>
/// Срез «Справочник сотрудников»: поиск записей по началу фамилии.
/// <list type="bullet">
/// <item>Администратору — записи без учётной записи: при создании учётной записи её привязывают к существующей записи
/// (заведующий или директор из файла должностей входит «самим собой», а не новой записью справочника).</item>
/// <item>Авторам документов — все активные записи, с учётной записью и без: составитель РПД может и не входить
/// в систему.</item>
/// </list>
/// </summary>
public class DirectoryModule : ICarterModule
{
    private const int MaxResults = 20;

    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/directory").WithTags("Directory").RequireAuthorization(Policies.Admin);

        admin.MapGet("/", async (string? surname, AppDbContext db, CancellationToken ct) =>
        {
            var query = BySurname(db.Employees.Where(e => e.IsActive && e.IdentitySubject == null), surname);
            return await DirectoryRecordItem.Project(query.OrderBy(e => e.ShortName).Take(MaxResults), db).ToListAsync(ct);
        });

        // Страница «Справочник сотрудников» (этап 5.2, S-51): записи, правка, деактивация, смена человека на должности,
        // загрузка файла должностей. Правила — в ядре (EmployeeDirectory, EmployeeImport).
        admin.MapGet("/records", Records);
        admin.MapGet("/units", Units);
        admin.MapPost("/records", Create);
        admin.MapPut("/records/{id:guid}", Update);
        admin.MapPost("/records/{id:guid}/deactivate", (Guid id, HttpContext http, AppDbContext db, DirectoryAccounts accounts,
            EmployeeDirectory directory, CancellationToken ct) => SetActive(id, false, http, db, accounts, directory, ct));
        admin.MapPost("/records/{id:guid}/activate", (Guid id, HttpContext http, AppDbContext db, DirectoryAccounts accounts,
            EmployeeDirectory directory, CancellationToken ct) => SetActive(id, true, http, db, accounts, directory, ct));
        admin.MapPost("/records/{id:guid}/replace", Replace);
        admin.MapPost("/imports", UploadPositions).Accepts<IFormFile>("multipart/form-data");
        admin.MapGet("/imports/last", LastImport);
        admin.MapGet("/imports/{id:guid}", Import);

        var authors = app.MapGroup("/api/directory").WithTags("Directory").RequireAuthorization(Policies.DocumentAuthors);

        // Составители документа: подпись для документа и должность копируются в лист согласования, кафедра — чтобы
        // различить однофамильцев.
        authors.MapGet("/", async (string? surname, AppDbContext db, CancellationToken ct) =>
        {
            var query = BySurname(db.Employees.Where(e => e.IsActive), surname);
            return await query.OrderBy(e => e.ShortName).Take(MaxResults)
                .Select(e => new DirectoryPersonItem(
                    e.Id, e.ShortName, e.NameForDocument, e.Position,
                    db.Departments.Where(d => d.Id == e.DepartmentId).Select(d => d.ShortName).FirstOrDefault()))
                .ToListAsync(ct);
        });
    }

    // Записи справочника: фамилия (начало), категория подписанта, неактивные — по флажку.
    private static async Task<List<DirectoryEntry>> Records(
        string? surname, string? role, bool? inactive, AppDbContext db, CancellationToken ct)
    {
        var query = BySurname(db.Employees.AsQueryable(), surname);
        if (inactive != true) query = query.Where(e => e.IsActive);
        if (!string.IsNullOrEmpty(role)) query = query.Where(e => e.SignatoryRole == role);
        return await DirectoryEntry.ListAsync(query.OrderBy(e => e.ShortName), db, ct);
    }

    // Для формы: кафедры, институты и категории подписантов с русскими названиями.
    private static async Task<DirectoryUnits> Units(AppDbContext db, CancellationToken ct) => new(
        await db.Departments.OrderBy(d => d.Name).Select(d => new UnitItem(d.Id, d.Name, d.ShortName)).ToListAsync(ct),
        await db.Institutes.OrderBy(i => i.Name).Select(i => new UnitItem(i.Id, i.Name, i.ShortName ?? i.Code)).ToListAsync(ct),
        SignatoryRoles.All.Select(r => new RoleItem(r, SignatoryRoles.Title(r),
            r is SignatoryRoles.DepartmentHead or SignatoryRoles.DepartmentHeadActing ? "department"
            : r == SignatoryRoles.InstituteDirector ? "institute" : "none")).ToList());

    private static async Task<IResult> Create(EmployeeRequest rq, HttpContext http, AppDbContext db, DirectoryAccounts accounts,
        EmployeeDirectory directory, CancellationToken ct)
    {
        if (await accounts.FindIdAsync(http.User, ct) is not Guid by) return NoDirectoryRecord();
        var (employee, problem) = await directory.CreateAsync(rq.ToData(), by, ct);
        return await ResultAsync(employee, problem, db, ct, created: true);
    }

    private static async Task<IResult> Update(Guid id, EmployeeRequest rq, HttpContext http, AppDbContext db,
        DirectoryAccounts accounts, EmployeeDirectory directory, CancellationToken ct)
    {
        if (await accounts.FindIdAsync(http.User, ct) is not Guid by) return NoDirectoryRecord();
        var (employee, problem) = await directory.UpdateAsync(id, rq.ToData(), by, ct);
        return await ResultAsync(employee, problem, db, ct);
    }

    private static async Task<IResult> SetActive(Guid id, bool active, HttpContext http, AppDbContext db,
        DirectoryAccounts accounts, EmployeeDirectory directory, CancellationToken ct)
    {
        if (await accounts.FindIdAsync(http.User, ct) is not Guid by) return NoDirectoryRecord();
        var (employee, problem) = await directory.SetActiveAsync(id, active, by, ct);
        return await ResultAsync(employee, problem, db, ct);
    }

    private static async Task<IResult> Replace(Guid id, ReplaceRequest rq, HttpContext http, AppDbContext db,
        DirectoryAccounts accounts, EmployeeDirectory directory, CancellationToken ct)
    {
        if (await accounts.FindIdAsync(http.User, ct) is not Guid by) return NoDirectoryRecord();
        var (employee, problem) = await directory.ReplaceAsync(id, rq.SuccessorId, rq.Newcomer?.ToData(), by, ct);
        return await ResultAsync(employee, problem, db, ct);
    }

    // Сохранить и вернуть запись или отказ: 404, 422 — неверные данные, 409 — конфликт с другой записью.
    private static async Task<IResult> ResultAsync(Employee? employee, DirectoryProblem? problem, AppDbContext db,
        CancellationToken ct, bool created = false)
    {
        if (problem is not null)
            return Results.Problem(statusCode: problem.Kind switch
            {
                DirectoryProblemKind.NotFound => StatusCodes.Status404NotFound,
                DirectoryProblemKind.Conflict => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status422UnprocessableEntity
            }, title: problem.Title, detail: problem.Detail);
        await db.SaveChangesAsync(ct);
        var entry = (await DirectoryEntry.ListAsync(db.Employees.Where(e => e.Id == employee!.Id), db, ct)).Single();
        return created ? Results.Created($"/api/admin/directory/records/{entry.Id}", entry) : Results.Ok(entry);
    }

    // Загрузка файла должностей (только администратор): файл — в uploaded_files, разбор — в фоне, очередь parsing.
    private static async Task<IResult> UploadPositions(HttpRequest request, AppDbContext db, DirectoryAccounts accounts,
        IBackgroundJobClient jobs, IConfiguration config, TimeProvider clock, CancellationToken ct)
    {
        if (await accounts.FindIdAsync(request.HttpContext.User, ct) is not Guid by) return NoDirectoryRecord();
        if (!request.HasFormContentType)
            return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Ожидается multipart/form-data");
        var file = (await request.ReadFormAsync(ct)).Files.GetFile("file");
        var maxBytes = config.GetValue("Imports:MaxFileBytes", 10L * 1024 * 1024);
        if (file is null)
            return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Файл не выбран",
                detail: "Нужен файл должностей .xlsx.");
        if (!string.Equals(Path.GetExtension(file.FileName), ".xlsx", StringComparison.OrdinalIgnoreCase))
            return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Нужен файл .xlsx",
                detail: $"Загружен «{file.FileName}».");
        if (file.Length == 0 || file.Length > maxBytes)
            return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Недопустимый размер файла",
                detail: $"Размер {file.Length} байт, допустимо от 1 до {maxBytes}.");

        byte[] content;
        await using (var stream = file.OpenReadStream())
        using (var buffer = new MemoryStream())
        {
            await stream.CopyToAsync(buffer, ct);
            content = buffer.ToArray();
        }
        var checksum = Convert.ToHexStringLower(SHA256.HashData(content));
        var now = clock.GetUtcNow();
        // Тот же файл уже загружался — используется сохранённый.
        var fileId = await db.UploadedFiles.Where(f => f.Checksum == checksum && f.FileType == UploadedFileTypes.Employees)
            .Select(f => (Guid?)f.Id).FirstOrDefaultAsync(ct);
        if (fileId is null)
        {
            var uploaded = new UploadedFile
            {
                UploadedBy = by, FileType = UploadedFileTypes.Employees, OriginalName = Path.GetFileName(file.FileName),
                Content = content, MimeType = file.ContentType, SizeBytes = content.Length, Checksum = checksum, UploadedAt = now
            };
            db.UploadedFiles.Add(uploaded);
            fileId = uploaded.Id;
        }
        var run = new DirectoryImport { StartedBy = by, FileId = fileId.Value, StartedAt = now };
        db.DirectoryImports.Add(run);
        await db.SaveChangesAsync(ct);

        jobs.Enqueue<DirectoryImportJob>(JobQueues.Parsing, job => job.RunAsync(run.Id, CancellationToken.None));
        return Results.Accepted($"/api/admin/directory/imports/{run.Id}", await ImportItem.QueryAsync(db, run.Id, ct));
    }

    private static async Task<IResult> LastImport(AppDbContext db, CancellationToken ct) =>
        await db.DirectoryImports.OrderByDescending(r => r.StartedAt).Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct) is Guid id
            ? Results.Ok(await ImportItem.QueryAsync(db, id, ct))
            : Results.NoContent();

    private static async Task<IResult> Import(Guid id, AppDbContext db, CancellationToken ct) =>
        await ImportItem.QueryAsync(db, id, ct) is { } item
            ? Results.Ok(item)
            : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Загрузка не найдена");

    private static IResult NoDirectoryRecord() =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Нет записи в справочнике сотрудников",
            detail: "Учётная запись не связана со справочником сотрудников.");

    // Поиск по началу фамилии («Фамилия И. О.»), без учёта регистра; пустой запрос — первые по алфавиту.
    private static IQueryable<Employee> BySurname(IQueryable<Employee> query, string? surname)
    {
        if (string.IsNullOrWhiteSpace(surname)) return query;
        var prefix = surname.Trim().ToLower();
        return query.Where(e => e.ShortName.ToLower().StartsWith(prefix));
    }
}

/// <summary>Запись справочника сотрудников: ФИО, должность и место, кафедра (сокращение).</summary>
public record DirectoryRecordItem(
    Guid Id, string ShortName, string? FullName, string? Position, string? OfficialDesignation, string? Department, bool IsActive)
{
    public static IQueryable<DirectoryRecordItem> Project(IQueryable<Employee> query, AppDbContext db) =>
        query.Select(e => new DirectoryRecordItem(
            e.Id, e.ShortName, e.FullName, e.Position, e.OfficialDesignation,
            db.Departments.Where(d => d.Id == e.DepartmentId).Select(d => d.ShortName).FirstOrDefault(),
            e.IsActive));
}

/// <summary>Сотрудник для выбора составителя: «Фамилия И. О.», подпись «И. О. Фамилия», должность, кафедра.</summary>
public record DirectoryPersonItem(Guid Id, string ShortName, string NameForDocument, string? Position, string? Department);

/// <summary>
/// Запись справочника для страницы администратора: «Фамилия И. О.», подпись, должность, официальное название,
/// подразделение, категория подписанта (код и название), учётная запись (email), «активна».
/// </summary>
public record DirectoryEntry(
    Guid Id, string ShortName, string NameForDocument, string? FullName, string? Email, string? Position, string? AcademicDegree,
    string? OfficialDesignation, string? SignatoryRole, string? SignatoryRoleTitle, Guid? DepartmentId, Guid? InstituteId,
    string? Unit, string? Account, bool IsActive)
{
    public static async Task<List<DirectoryEntry>> ListAsync(IQueryable<Employee> query, AppDbContext db, CancellationToken ct) =>
        (await query.Select(e => new DirectoryEntry(
            e.Id, e.ShortName, e.NameForDocument, e.FullName, e.Email, e.Position, e.AcademicDegree, e.OfficialDesignation,
            e.SignatoryRole, null, e.DepartmentId, e.InstituteId,
            db.Departments.Where(d => d.Id == e.DepartmentId).Select(d => d.ShortName).FirstOrDefault()
                ?? db.Institutes.Where(i => i.Id == e.InstituteId).Select(i => i.ShortName ?? i.Code).FirstOrDefault(),
            e.IdentitySubject == null ? null : e.Email, e.IsActive)).ToListAsync(ct))
        .Select(x => x with { SignatoryRoleTitle = x.SignatoryRole is { } r ? SignatoryRoles.Title(r) : null }).ToList();
}

/// <summary>Поля записи справочника в запросе страницы администратора.</summary>
public record EmployeeRequest(
    string ShortName, string? NameForDocument, string? FullName, string? Email, string? Position, string? AcademicDegree,
    string? OfficialDesignation, string? SignatoryRole, Guid? DepartmentId, Guid? InstituteId)
{
    public EmployeeData ToData() => new(ShortName ?? "", NameForDocument, FullName, Email, Position, AcademicDegree,
        OfficialDesignation, string.IsNullOrEmpty(SignatoryRole) ? null : SignatoryRole, DepartmentId, InstituteId);
}

/// <summary>Смена человека на должности: выбранная запись (<paramref name="SuccessorId"/>) или новый сотрудник.</summary>
public record ReplaceRequest(Guid? SuccessorId, EmployeeRequest? Newcomer);

public record UnitItem(Guid Id, string Name, string ShortName);

/// <summary>Категория подписанта: код, название и подразделение — кафедра, институт или нет (none).</summary>
public record RoleItem(string Code, string Title, string Unit);

public record DirectoryUnits(IReadOnlyList<UnitItem> Departments, IReadOnlyList<UnitItem> Institutes, IReadOnlyList<RoleItem> Roles);

/// <summary>Загрузка файла должностей: файл, когда, статус, записей в файле, счётчики, замечания.</summary>
public record ImportItem(
    Guid Id, string FileName, string Status, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, int Rows, int Added, int Updated,
    int Replaced, IReadOnlyList<ImportIssue> Issues)
{
    public static async Task<ImportItem?> QueryAsync(AppDbContext db, Guid id, CancellationToken ct)
    {
        var run = await db.DirectoryImports.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
        if (run is null) return null;
        var name = await db.UploadedFiles.Where(f => f.Id == run.FileId).Select(f => f.OriginalName).SingleAsync(ct);
        return new(run.Id, name, run.Status, run.StartedAt, run.CompletedAt, run.RowsCount, run.AddedCount, run.UpdatedCount,
            run.ReplacedCount, ImportIssue.Deserialize(run.ErrorsLog));
    }
}
