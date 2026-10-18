using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.Data.Configurations;

// Контекст Reference (раздел 2.4 ВКР, docs/design/ВКР_2_Структура_данных.md).
// Внешние ключи — RESTRICT; CASCADE только для строго подчинённых таблиц (дисциплины плана, их детали,
// матрица, индикаторы компетенции). Уникальные индексы на institutes.name и study_profiles(direction_id, name)
// добавлены как естественные ключи импорта.

internal sealed class InstituteConfiguration : IEntityTypeConfiguration<Institute>
{
    public void Configure(EntityTypeBuilder<Institute> e)
    {
        e.ToTable("institutes");
        e.Property(x => x.Code).HasMaxLength(50);
        e.Property(x => x.Name).HasMaxLength(255);
        e.Property(x => x.ShortName).HasMaxLength(100);
        e.HasIndex(x => x.Code).IsUnique();
        e.HasIndex(x => x.Name).IsUnique();
    }
}

internal sealed class DepartmentConfiguration : IEntityTypeConfiguration<Department>
{
    public void Configure(EntityTypeBuilder<Department> e)
    {
        e.ToTable("departments");
        e.Property(x => x.Code).HasMaxLength(50);
        e.Property(x => x.Name).HasMaxLength(255);
        e.Property(x => x.ShortName).HasMaxLength(50);
        e.HasIndex(x => x.Code).IsUnique();
        e.HasIndex(x => x.Name).IsUnique();
        e.HasIndex(x => x.ShortName).IsUnique();
        e.HasOne(x => x.Institute).WithMany().HasForeignKey(x => x.InstituteId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class StudyDirectionConfiguration : IEntityTypeConfiguration<StudyDirection>
{
    public void Configure(EntityTypeBuilder<StudyDirection> e)
    {
        e.ToTable("study_directions");
        e.Property(x => x.Code).HasMaxLength(20);
        e.Property(x => x.Name).HasMaxLength(255);
        e.Property(x => x.EducationLevel).HasMaxLength(50);
        e.HasIndex(x => x.Code).IsUnique();
        e.HasOne<Department>().WithMany().HasForeignKey(x => x.ResponsibleDepartmentId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class StudyProfileConfiguration : IEntityTypeConfiguration<StudyProfile>
{
    public void Configure(EntityTypeBuilder<StudyProfile> e)
    {
        e.ToTable("study_profiles");
        e.Property(x => x.Name).HasMaxLength(255);
        e.HasIndex(x => new { x.DirectionId, x.Name }).IsUnique();
        e.HasOne<StudyDirection>().WithMany().HasForeignKey(x => x.DirectionId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class FgosConfiguration : IEntityTypeConfiguration<Fgos>
{
    public void Configure(EntityTypeBuilder<Fgos> e)
    {
        e.ToTable("fgos");
        e.Property(x => x.Version).HasMaxLength(20);
        e.Property(x => x.OrderNumber).HasMaxLength(100);
        e.Property(x => x.DocumentUrl).HasMaxLength(500);
        e.Property(x => x.IsCurrent).HasDefaultValue(true).HasSentinel(true);
        e.HasIndex(x => new { x.DirectionId, x.IsCurrent });
        // Естественный ключ ФГОС — направление, номер и дата приказа; номер может отсутствовать.
        e.HasIndex(x => new { x.DirectionId, x.OrderNumber, x.ApprovedOn }).IsUnique().AreNullsDistinct(false);
        e.HasOne<StudyDirection>().WithMany().HasForeignKey(x => x.DirectionId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CompetenceConfiguration : IEntityTypeConfiguration<Competence>
{
    public void Configure(EntityTypeBuilder<Competence> e)
    {
        e.ToTable("competences");
        e.Property(x => x.Code).HasMaxLength(20);
        e.Property(x => x.Category).HasMaxLength(10);
        e.Property(x => x.CategoryName).HasMaxLength(255);
        // УК и ОПК уникальны в ФГОС, ПК — в профиле: у разных профилей одного направления свои ПК-1…ПК-N.
        e.HasIndex(x => new { x.FgosId, x.Code }).IsUnique().HasFilter("profile_id IS NULL");
        e.HasIndex(x => new { x.ProfileId, x.Code }).IsUnique().HasFilter("profile_id IS NOT NULL");
        e.HasIndex(x => x.Category);
        e.HasOne<Fgos>().WithMany().HasForeignKey(x => x.FgosId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<StudyProfile>().WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Restrict);
        e.HasMany(x => x.Indicators).WithOne().HasForeignKey(x => x.CompetenceId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class CompetenceIndicatorConfiguration : IEntityTypeConfiguration<CompetenceIndicator>
{
    public void Configure(EntityTypeBuilder<CompetenceIndicator> e)
    {
        e.ToTable("competence_indicators");
        e.Property(x => x.Code).HasMaxLength(30);
        e.Property(x => x.IndicatorType).HasMaxLength(20);
        e.HasIndex(x => new { x.CompetenceId, x.Code }).IsUnique();
    }
}

internal sealed class CurriculumPlanConfiguration : IEntityTypeConfiguration<CurriculumPlan>
{
    public void Configure(EntityTypeBuilder<CurriculumPlan> e)
    {
        e.ToTable("curriculum_plans");
        e.Property(x => x.StudyForm).HasMaxLength(50);
        e.Property(x => x.Version).HasDefaultValue(1).HasSentinel(1);
        e.HasIndex(x => new { x.DirectionId, x.ProfileId, x.Year });
        e.HasOne<StudyDirection>().WithMany().HasForeignKey(x => x.DirectionId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<StudyProfile>().WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<UploadedFile>().WithMany().HasForeignKey(x => x.SourceFileId).OnDelete(DeleteBehavior.Restrict);
        e.HasMany(x => x.Disciplines).WithOne().HasForeignKey(x => x.CurriculumPlanId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class DisciplineConfiguration : IEntityTypeConfiguration<Discipline>
{
    public void Configure(EntityTypeBuilder<Discipline> e)
    {
        e.ToTable("disciplines");
        e.Property(x => x.Code).HasMaxLength(50);
        e.Property(x => x.Name).HasMaxLength(500);
        e.Property(x => x.Block).HasMaxLength(10);
        e.Property(x => x.PartType).HasMaxLength(20);
        e.Property(x => x.EntryKind).HasMaxLength(20);
        e.HasIndex(x => new { x.CurriculumPlanId, x.Code }).IsUnique();
        e.HasIndex(x => x.DepartmentId);
        e.HasIndex(x => x.ParentId);
        e.HasIndex(x => new { x.Block, x.PartType });
        // Полнотекстовый поиск по названию дисциплины.
        e.HasIndex(x => x.Name).HasMethod("gin").IsTsVectorExpressionIndex("russian");
        e.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Cascade);
        e.HasMany(x => x.Semesters).WithOne().HasForeignKey(x => x.DisciplineId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class DisciplineSemesterDetailsConfiguration : IEntityTypeConfiguration<DisciplineSemesterDetails>
{
    public void Configure(EntityTypeBuilder<DisciplineSemesterDetails> e)
    {
        var allowed = string.Join(", ", ControlTypeCodes.All.Select(c => $"'{c}'"));
        e.ToTable("discipline_semester_details", t =>
        {
            t.HasCheckConstraint("ck_discipline_semester_details_hours",
                "lectures >= 0 AND practice >= 0 AND lab_hours >= 0 AND self_study >= 0 AND control >= 0 AND credits >= 0");
            t.HasCheckConstraint("ck_discipline_semester_details_control_types",
                $"control_types <@ ARRAY[{allowed}]::text[]");
        });
        foreach (var p in new[] { "ContactHours", "Lectures", "Practice", "LabHours", "SelfStudy", "Control", "Credits" })
            e.Property<int>(p).HasDefaultValue(0);
        e.Property(x => x.ControlTypes).HasDefaultValueSql("'{}'::text[]");
        e.HasIndex(x => new { x.DisciplineId, x.Semester }).IsUnique();
    }
}

internal sealed class CompetenceMatrixEntryConfiguration : IEntityTypeConfiguration<CompetenceMatrixEntry>
{
    public void Configure(EntityTypeBuilder<CompetenceMatrixEntry> e)
    {
        e.ToTable("competence_matrix");
        e.HasIndex(x => new { x.DisciplineId, x.CompetenceId }).IsUnique();
        e.HasOne<Discipline>().WithMany().HasForeignKey(x => x.DisciplineId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne<Competence>().WithMany().HasForeignKey(x => x.CompetenceId).OnDelete(DeleteBehavior.Restrict);
    }
}
