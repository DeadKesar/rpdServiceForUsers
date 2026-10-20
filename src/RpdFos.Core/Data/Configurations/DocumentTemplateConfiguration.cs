using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.Data.Configurations;

/// <summary>
/// Шаблоны документов document_templates (раздел 2.4 ВКР с правками по ревизии архитектуры:
/// print_template_file_id, print_mapping). Редакция — revision, связь редакций — lineage_id.
/// </summary>
internal sealed class DocumentTemplateConfiguration : IEntityTypeConfiguration<DocumentTemplate>
{
    public void Configure(EntityTypeBuilder<DocumentTemplate> e)
    {
        e.ToTable("document_templates");
        e.Property(x => x.Type).HasMaxLength(50);
        e.Property(x => x.Name).HasMaxLength(255);
        e.Property(x => x.Status).HasMaxLength(20);
        // Снимок схемы — json, а не jsonb: jsonb не хранит порядок ключей, а по порядку свойств схемы
        // редактор строит разделы и поля. Лист согласования — массив: порядок мест jsonb сохраняет,
        // порядок ключей внутри места не важен.
        e.Property(x => x.Schema).HasColumnType("json");
        e.Property(x => x.SignaturesConfig).HasColumnType("jsonb");
        e.Property(x => x.PrintMapping).HasColumnType("jsonb");

        // Оптимистичная блокировка: версия — токен конкуренции.
        // В WHERE запроса UPDATE уходит исходная версия, новую проставляет VersioningInterceptor.
        e.Property(x => x.Version).IsConcurrencyToken();

        // Мягкое удаление: удалённые записи скрыты глобальным фильтром.
        e.HasQueryFilter(x => !x.IsDeleted);

        e.HasIndex(x => new { x.Type, x.Status });
        e.HasIndex(x => x.CurriculumPlanId);
        e.HasIndex(x => x.CreatedBy);
        e.HasIndex(x => new { x.LineageId, x.Revision }).IsUnique();
        // В линии редакций опубликована не больше чем одна: новая редакция при публикации архивирует прежнюю.
        e.HasIndex(x => x.LineageId, "ux_document_templates_lineage_published").IsUnique()
            .HasFilter($"status = '{TemplateStatuses.Published}'");

        e.HasOne<CurriculumPlan>().WithMany().HasForeignKey(x => x.CurriculumPlanId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<UploadedFile>().WithMany().HasForeignKey(x => x.PrintTemplateFileId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<Employee>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
    }
}
