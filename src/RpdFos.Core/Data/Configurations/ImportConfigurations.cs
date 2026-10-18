using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.Data.Configurations;

// Контекст Infrastructure: загруженные файлы и журнал импортов (раздел 2.4 ВКР, правка по ревизии:
// содержимое файла — в content bytea, storage_path необязателен).

internal sealed class UploadedFileConfiguration : IEntityTypeConfiguration<UploadedFile>
{
    public void Configure(EntityTypeBuilder<UploadedFile> e)
    {
        e.ToTable("uploaded_files");
        e.Property(x => x.FileType).HasMaxLength(50);
        e.Property(x => x.OriginalName).HasMaxLength(500);
        e.Property(x => x.StoragePath).HasMaxLength(1000);
        e.Property(x => x.MimeType).HasMaxLength(100);
        e.Property(x => x.Checksum).HasMaxLength(64);
        e.HasIndex(x => x.Checksum);
        e.HasOne<Employee>().WithMany().HasForeignKey(x => x.UploadedBy).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ParserRunConfiguration : IEntityTypeConfiguration<ParserRun>
{
    public void Configure(EntityTypeBuilder<ParserRun> e)
    {
        e.ToTable("parser_runs");
        e.Property(x => x.Status).HasMaxLength(20);
        e.Property(x => x.ExtractedDisciplinesCount).HasDefaultValue(0);
        e.Property(x => x.ExtractedCompetencesCount).HasDefaultValue(0);
        e.Property(x => x.ExtractedEmployeesCount).HasDefaultValue(0);
        e.Property(x => x.ErrorsLog).HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");

        e.HasOne<Employee>().WithMany().HasForeignKey(x => x.StartedBy).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<UploadedFile>().WithMany().HasForeignKey(x => x.PlanFileId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<UploadedFile>().WithMany().HasForeignKey(x => x.CompListFileId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<UploadedFile>().WithMany().HasForeignKey(x => x.CompMatrixFileId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<UploadedFile>().WithMany().HasForeignKey(x => x.EmployeesFileId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<CurriculumPlan>().WithMany().HasForeignKey(x => x.CreatedCurriculumPlanId).OnDelete(DeleteBehavior.Restrict);
    }
}
