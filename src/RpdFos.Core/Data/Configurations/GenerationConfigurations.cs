using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.Data.Configurations;

// Контекст Infrastructure: задачи формирования файлов и сформированные файлы (раздел 2.4 ВКР). Дополнения: отчёт
// о формировании (report) у задачи, имя файла и время создания у файла.

internal sealed class GenerationTaskConfiguration : IEntityTypeConfiguration<GenerationTask>
{
    public void Configure(EntityTypeBuilder<GenerationTask> e)
    {
        string List(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"'{v}'"));
        e.ToTable("generation_tasks", t =>
        {
            t.HasCheckConstraint("ck_generation_tasks_format", $"format IN ({List(GenerationFormats.All)})");
            t.HasCheckConstraint("ck_generation_tasks_status", $"status IN ({List(GenerationStatuses.All)})");
        });
        e.Property(x => x.Format).HasMaxLength(20);
        e.Property(x => x.Status).HasMaxLength(20);
        e.Property(x => x.HangfireJobId).HasMaxLength(100);
        e.Property(x => x.Report).HasColumnType("jsonb");
        e.HasIndex(x => new { x.DocumentId, x.CreatedAt }).IsDescending(false, true);
        e.HasOne<Document>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne<Employee>().WithMany().HasForeignKey(x => x.RequestedBy).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class GeneratedFileConfiguration : IEntityTypeConfiguration<GeneratedFile>
{
    public void Configure(EntityTypeBuilder<GeneratedFile> e)
    {
        e.ToTable("generated_files");
        e.Property(x => x.StoragePath).HasMaxLength(1000);
        e.Property(x => x.FileName).HasMaxLength(255);
        e.Property(x => x.Format).HasMaxLength(20);
        // Для фоновой очистки по сроку хранения.
        e.HasIndex(x => x.ExpiresAt);
        e.HasIndex(x => x.GenerationTaskId);
        e.HasOne<GenerationTask>().WithMany().HasForeignKey(x => x.GenerationTaskId).OnDelete(DeleteBehavior.Cascade);
    }
}
