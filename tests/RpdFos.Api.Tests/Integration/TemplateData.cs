using System.Security.Cryptography;
using RpdFos.Api.Tests.DocumentTypes;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.DocumentTypes;
using RpdFos.Core.Templates;

namespace RpdFos.Api.Tests.Integration;

/// <summary>Данные для тестов шаблонов: учебный план, методист и шаблоны в обход API.</summary>
public static class TemplateData
{
    private static readonly DocumentTypeRegistry Registry = new();

    /// <summary>
    /// Версия учебного плана с направлением и профилем. Код направления уникален: в той же базе
    /// тесты импорта загружают настоящее 09.04.04.
    /// </summary>
    public static async Task<CurriculumPlan> AddPlanAsync(AppDbContext db)
    {
        var n = Random.Shared.Next(10, 99);
        var direction = new StudyDirection
        {
            Code = $"99.04.{n}-{Guid.NewGuid().ToString()[..4]}", Name = $"Тестовое направление {Guid.NewGuid()}",
            EducationLevel = EducationLevels.Master
        };
        var profile = new StudyProfile { DirectionId = direction.Id, Name = "Тестовый профиль" };
        var plan = new CurriculumPlan
        {
            DirectionId = direction.Id, ProfileId = profile.Id, Year = 2026, CreatedAt = DateTimeOffset.UtcNow
        };
        db.AddRange(direction, profile, plan);
        await db.SaveChangesAsync();
        return plan;
    }

    /// <summary>DOCX-шаблон оформления в uploaded_files.</summary>
    public static async Task<UploadedFile> AddPrintTemplateAsync(AppDbContext db, Guid uploadedBy, string file)
    {
        var content = DocxBookmarksTests.Template(file);
        var uploaded = new UploadedFile
        {
            UploadedBy = uploadedBy, FileType = UploadedFileTypes.Other, OriginalName = file, Content = content,
            MimeType = ImportFiles.Docx, SizeBytes = content.Length,
            Checksum = Convert.ToHexStringLower(SHA256.HashData(content)), UploadedAt = DateTimeOffset.UtcNow
        };
        db.UploadedFiles.Add(uploaded);
        await db.SaveChangesAsync();
        return uploaded;
    }

    /// <summary>Шаблон в статусе «Черновик» с реальным шаблоном оформления — проходит проверку перед публикацией.</summary>
    public static async Task<DocumentTemplate> AddDraftAsync(AppDbContext db, string type = DocumentTypeRegistry.Rpd)
    {
        var methodist = await ImportFiles.AddMethodistAsync(db);
        var plan = await AddPlanAsync(db);
        var file = await AddPrintTemplateAsync(db, methodist.Id,
            type == DocumentTypeRegistry.Rpd ? DocxBookmarksTests.Rpd : DocxBookmarksTests.Fos);
        var definition = Registry.Get(type);
        var template = DocumentTemplate.Create(definition, $"Шаблон {Guid.NewGuid()}", plan.Id, methodist.Id);
        template.AttachPrintTemplate(file.Id, TemplateCheck.DefaultMapping(definition, DocxBookmarks.Read(file.Content!)));
        db.DocumentTemplates.Add(template);
        await db.SaveChangesAsync();
        return template;
    }
}
