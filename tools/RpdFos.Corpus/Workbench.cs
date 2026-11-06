using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RpdFos.Core;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.DocumentTypes;
using RpdFos.Core.Printing;
using RpdFos.Core.Templates;
using Testcontainers.PostgreSql;

namespace RpdFos.Corpus;

/// <summary>
/// Окружение прогона: одноразовая PostgreSQL 17 в контейнере (не база разработки), миграции, сервисы ядра — те же,
/// что у хоста, — запись справочника «Прогон корпуса» и файл оформления РПД v3.
/// </summary>
public sealed class Workbench : IAsyncDisposable
{
    private readonly PostgreSqlContainer _db;
    private readonly ServiceProvider _services;

    public Guid RunnerId { get; private set; }
    public Guid PrintTemplateFileId { get; private set; }
    public string PrintMapping { get; private set; } = "";
    public string StoragePath { get; }

    private Workbench(PostgreSqlContainer db, ServiceProvider services, string storage) =>
        (_db, _services, StoragePath) = (db, services, storage);

    public static async Task<Workbench> StartAsync(string? converterUrl)
    {
        var db = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await db.StartAsync();
        var storage = Path.Combine(Path.GetTempPath(), $"rpdfos-corpus-{Guid.NewGuid():N}");

        var services = new ServiceCollection();
        services.AddCore(db.GetConnectionString());
        services.Configure<GenerationSettings>(o => o.StoragePath = storage);
        services.AddSingleton<IPdfConverter>(new GotenbergPdfConverter(new HttpClient
        {
            BaseAddress = new Uri((converterUrl ?? "http://localhost:3000").TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(120)
        }));
        var bench = new Workbench(db, services.BuildServiceProvider(), storage);
        await bench.SeedAsync();
        return bench;
    }

    public AsyncServiceScope Scope() => _services.CreateAsyncScope();

    private async Task SeedAsync()
    {
        await using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();

        var runner = new Employee
        {
            ShortName = "Прогон К. К.", NameForDocument = "К. К. Прогон", Position = "доцент", CanLogin = false,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Employees.Add(runner);

        var content = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "DWP_TemplateBookmarks_v3.docx"));
        var file = AddFile(db, runner.Id, "DWP_TemplateBookmarks_v3.docx", content, UploadedFileTypes.Other);
        await db.SaveChangesAsync();

        var registry = scope.ServiceProvider.GetRequiredService<DocumentTypeRegistry>();
        (RunnerId, PrintTemplateFileId) = (runner.Id, file.Id);
        PrintMapping = TemplateCheck.DefaultMapping(registry.Get(DocumentTypeRegistry.Rpd), DocxBookmarks.Read(content));
    }

    /// <summary>Файл источника — в uploaded_files, как при загрузке через страницу импорта.</summary>
    public static UploadedFile AddFile(AppDbContext db, Guid by, string name, byte[] content, string type)
    {
        var file = new UploadedFile
        {
            UploadedBy = by, FileType = type, OriginalName = name, Content = content, MimeType = "application/octet-stream",
            SizeBytes = content.Length, Checksum = Convert.ToHexStringLower(SHA256.HashData(content)), UploadedAt = DateTimeOffset.UtcNow
        };
        db.UploadedFiles.Add(file);
        return file;
    }

    /// <summary>Опубликованный шаблон РПД для версии плана — с оформлением v3 и сопоставлением по умолчанию.</summary>
    public async Task<Guid> PublishTemplateAsync(Guid planId)
    {
        await using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var registry = scope.ServiceProvider.GetRequiredService<DocumentTypeRegistry>();
        var template = DocumentTemplate.Create(registry.Get(DocumentTypeRegistry.Rpd), "РПД (прогон корпуса)", planId, RunnerId);
        template.AttachPrintTemplate(PrintTemplateFileId, PrintMapping);
        template.Publish(DateTimeOffset.UtcNow);
        db.DocumentTemplates.Add(template);
        await db.SaveChangesAsync();
        return template.Id;
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _db.DisposeAsync();
        try
        {
            if (Directory.Exists(StoragePath)) Directory.Delete(StoragePath, recursive: true);
        }
        catch (IOException)
        {
            // Временные файлы печати — не повод падать в конце прогона.
        }
    }
}
