using System.Security.Cryptography;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Api.Tests.Integration;

/// <summary>Реальные файлы 09.04.04 (tests/fixtures) и подготовка запуска импорта в базе.</summary>
public static class ImportFiles
{
    public const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string Docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    /// <summary>Файлы в порядке: план, список компетенций, матрица, должности.</summary>
    public static readonly (string Name, string Type, string Mime)[] All =
    [
        ("plan-09.04.04-2023.xlsx", UploadedFileTypes.CurriculumPlan, Xlsx),
        ("competences-09.04.04.docx", UploadedFileTypes.CompetenceReference, Docx),
        ("matrix-09.04.04.docx", UploadedFileTypes.CompetenceMatrix, Docx),
        ("positions-anonymized.xlsx", UploadedFileTypes.Employees, Xlsx)
    ];

    public static List<(string Name, byte[] Content)> Read() =>
        All.Select(f => (f.Name, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", f.Name)))).ToList();

    /// <summary>Методист, от имени которого идёт загрузка: запись справочника, связанная с учётной записью.</summary>
    public static async Task<Employee> AddMethodistAsync(AppDbContext db)
    {
        var subject = Guid.NewGuid().ToString();
        var methodist = new Employee
        {
            ShortName = "Методист М. М.", NameForDocument = "М. М. Методист", Email = $"methodist-{subject}@test.local",
            IdentitySubject = subject, CanLogin = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Employees.Add(methodist);
        await db.SaveChangesAsync();
        return methodist;
    }

    /// <summary>Файлы — в uploaded_files, запуск — в parser_runs со статусом running.</summary>
    public static async Task<(ParserRun Run, List<UploadedFile> Files)> AddRunAsync(
        AppDbContext db, Guid methodist, IReadOnlyList<(string Name, byte[] Content)> files)
    {
        var uploaded = files.Select((f, i) => new UploadedFile
        {
            UploadedBy = methodist, FileType = All[i].Type, OriginalName = f.Name, Content = f.Content,
            MimeType = All[i].Mime, SizeBytes = f.Content.Length,
            Checksum = Convert.ToHexStringLower(SHA256.HashData(f.Content)), UploadedAt = DateTimeOffset.UtcNow
        }).ToList();
        db.UploadedFiles.AddRange(uploaded);
        var run = new ParserRun
        {
            StartedBy = methodist, PlanFileId = uploaded[0].Id, CompListFileId = uploaded[1].Id,
            CompMatrixFileId = uploaded[2].Id, EmployeesFileId = uploaded[3].Id, StartedAt = DateTimeOffset.UtcNow
        };
        db.ParserRuns.Add(run);
        await db.SaveChangesAsync();
        return (run, uploaded);
    }
}
