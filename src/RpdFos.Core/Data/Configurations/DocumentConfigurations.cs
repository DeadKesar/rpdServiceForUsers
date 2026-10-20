using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;

namespace RpdFos.Core.Data.Configurations;

// Контекст Documents: документы, снимки версий, нормализованные часы и компетенции, история статусов
// (раздел 2.4 ВКР). Отступления: template_revision вместо template_version, версия блокировки — version
// (IVersioned) вместо current_version; search_vector (поиск, FR-SEA-01) — не на этом этапе.

internal sealed class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    public void Configure(EntityTypeBuilder<Document> e)
    {
        string List(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"'{v}'"));
        string[] statuses =
        [
            DocumentStatuses.Draft, DocumentStatuses.SavedDraft, DocumentStatuses.UnderReview, DocumentStatuses.Returned,
            DocumentStatuses.Accepted, DocumentStatuses.Approval, DocumentStatuses.Approved, DocumentStatuses.Archived,
            DocumentStatuses.Deleted
        ];

        e.ToTable("documents", t =>
        {
            t.HasCheckConstraint("ck_documents_status", $"status IN ({List(statuses)})");
            // Мягкое удаление: статус и флаг — вместе.
            t.HasCheckConstraint("ck_documents_deleted", $"is_deleted = (status = '{DocumentStatuses.Deleted}')");
        });
        e.Property(x => x.Type).HasMaxLength(50);
        e.Property(x => x.Status).HasMaxLength(30);
        e.Property(x => x.Title).HasMaxLength(500);
        e.Property(x => x.Payload).HasColumnType("jsonb");
        e.Property(x => x.IsOutdated).HasDefaultValue(false);
        e.Property(x => x.Version).IsConcurrencyToken();
        e.HasQueryFilter(x => !x.IsDeleted);

        e.HasIndex(x => new { x.AuthorId, x.Status });
        e.HasIndex(x => x.TemplateId);
        e.HasIndex(x => x.DisciplineId);
        e.HasIndex(x => new { x.Status, x.UpdatedAt }).IsDescending(false, true);
        e.HasIndex(x => x.IsOutdated).HasFilter("is_outdated");
        e.HasIndex(x => x.Payload).HasMethod("gin");

        e.HasOne<DocumentTemplate>().WithMany().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<Employee>().WithMany().HasForeignKey(x => x.AuthorId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<Discipline>().WithMany().HasForeignKey(x => x.DisciplineId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class DocumentVersionConfiguration : IEntityTypeConfiguration<DocumentVersion>
{
    public void Configure(EntityTypeBuilder<DocumentVersion> e)
    {
        e.ToTable("document_versions");
        e.Property(x => x.Snapshot).HasColumnType("jsonb");
        e.Property(x => x.HoursSnapshot).HasColumnType("jsonb");
        e.Property(x => x.CompetencesSnapshot).HasColumnType("jsonb");
        e.Property(x => x.TriggerEvent).HasMaxLength(50);
        e.HasIndex(x => new { x.DocumentId, x.VersionNumber }).IsUnique();
        e.HasOne<Document>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne<Employee>().WithMany().HasForeignKey(x => x.SavedBy).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class DocumentHoursConfiguration : IEntityTypeConfiguration<DocumentHours>
{
    public void Configure(EntityTypeBuilder<DocumentHours> e)
    {
        e.ToTable("document_hours", t => t.HasCheckConstraint("ck_document_hours_non_negative",
            "lectures >= 0 AND practice >= 0 AND lab_hours >= 0 AND self_study >= 0 AND control >= 0"));
        e.HasIndex(x => new { x.DocumentId, x.Semester }).IsUnique();
        e.HasOne<Document>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class DocumentCompetenceLinkConfiguration : IEntityTypeConfiguration<DocumentCompetenceLink>
{
    public void Configure(EntityTypeBuilder<DocumentCompetenceLink> e)
    {
        e.ToTable("document_competence_links", t => t.HasCheckConstraint("ck_document_competence_links_target",
            "competence_id IS NOT NULL OR indicator_id IS NOT NULL"));
        // Пустой ключ — тоже значение: (документ, компетенция, без индикатора) не повторяется.
        e.HasIndex(x => new { x.DocumentId, x.CompetenceId, x.IndicatorId }).IsUnique().AreNullsDistinct(false);
        e.HasIndex(x => x.CompetenceId);
        e.HasIndex(x => x.IndicatorId);
        e.HasOne<Document>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne<Competence>().WithMany().HasForeignKey(x => x.CompetenceId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<CompetenceIndicator>().WithMany().HasForeignKey(x => x.IndicatorId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class DocumentStatusChangeConfiguration : IEntityTypeConfiguration<DocumentStatusChange>
{
    public void Configure(EntityTypeBuilder<DocumentStatusChange> e)
    {
        e.ToTable("document_status_history");
        e.Property(x => x.FromStatus).HasMaxLength(30);
        e.Property(x => x.ToStatus).HasMaxLength(30);
        e.HasIndex(x => new { x.DocumentId, x.ChangedAt }).IsDescending(false, true);
        e.HasOne<Document>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne<Employee>().WithMany().HasForeignKey(x => x.ChangedBy).OnDelete(DeleteBehavior.Restrict);
    }
}
