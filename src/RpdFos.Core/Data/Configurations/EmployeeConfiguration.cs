using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.Data.Configurations;

/// <summary>
/// Справочник сотрудников users (раздел 2.4 ВКР с правкой по ревизии архитектуры: вместо password_hash —
/// identity_subject). Ограничения целостности — как в проекте: вход только с email и учётной записью,
/// категория подписанта согласована с уровнем в оргструктуре.
/// </summary>
internal sealed class EmployeeConfiguration : IEntityTypeConfiguration<Employee>
{
    public void Configure(EntityTypeBuilder<Employee> e)
    {
        string List(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"'{v}'"));
        var heads = List([SignatoryRoles.DepartmentHead, SignatoryRoles.DepartmentHeadActing]);

        e.ToTable("users", t =>
        {
            t.HasCheckConstraint("ck_users_can_login",
                "can_login = false OR (email IS NOT NULL AND identity_subject IS NOT NULL)");
            t.HasCheckConstraint("ck_users_signatory_role",
                $"signatory_role IS NULL OR signatory_role IN ({List(SignatoryRoles.All)})");
            t.HasCheckConstraint("ck_users_signatory_scope",
                $"(signatory_role IN ({heads}) AND department_id IS NOT NULL)" +
                $" OR (signatory_role = '{SignatoryRoles.InstituteDirector}' AND institute_id IS NOT NULL AND department_id IS NULL)" +
                $" OR (signatory_role IN ({List(SignatoryRoles.WithoutUnit)}) AND department_id IS NULL AND institute_id IS NULL)" +
                " OR (signatory_role IS NULL)");
            // Направление бывает только у председателя УМК. Само оно не обязательно: записи без направления
            // (из файла должностей) таблицу не ломают — правило определения подписанта их просто не находит.
            // Проверка категории на NOT NULL нужна: при пустой категории «signatory_role = …» даёт NULL, а CHECK
            // пропускает и NULL — запись «не подписант» с направлением прошла бы.
            t.HasCheckConstraint("ck_users_direction_scope",
                $"direction_id IS NULL OR (signatory_role IS NOT NULL AND signatory_role = '{SignatoryRoles.UmkChair}')");
        });

        e.Property(x => x.Email).HasMaxLength(255);
        e.Property(x => x.FullName).HasMaxLength(255);
        e.Property(x => x.ShortName).HasMaxLength(255);
        e.Property(x => x.NameForDocument).HasMaxLength(255);
        e.Property(x => x.OfficialDesignation).HasMaxLength(255);
        e.Property(x => x.Position).HasMaxLength(255);
        e.Property(x => x.AcademicDegree).HasMaxLength(100);
        e.Property(x => x.SignatoryRole).HasMaxLength(50);
        e.Property(x => x.IdentitySubject).HasMaxLength(200);
        e.Property(x => x.CanLogin).HasDefaultValue(true).HasSentinel(true);
        e.Property(x => x.IsActive).HasDefaultValue(true).HasSentinel(true);

        e.HasIndex(x => x.Email).IsUnique().HasFilter("email IS NOT NULL");
        e.HasIndex(x => x.IdentitySubject).IsUnique();
        // Должность и место подписанта уникальны только среди активных записей: при смене человека
        // старая запись остаётся неактивной — на неё ссылаются прошлые согласования.
        e.HasIndex(x => x.OfficialDesignation).IsUnique().HasFilter("official_designation IS NOT NULL AND is_active");
        e.HasIndex(x => new { x.SignatoryRole, x.DepartmentId }, "ux_users_department_signatory").IsUnique()
            .HasFilter($"signatory_role IN ({heads}) AND department_id IS NOT NULL AND is_active");
        e.HasIndex(x => new { x.SignatoryRole, x.InstituteId }, "ux_users_institute_signatory").IsUnique()
            .HasFilter($"signatory_role = '{SignatoryRoles.InstituteDirector}' AND is_active");
        // На направление — один действующий председатель УМК.
        e.HasIndex(x => new { x.SignatoryRole, x.DirectionId }, "ux_users_direction_signatory").IsUnique()
            .HasFilter($"signatory_role = '{SignatoryRoles.UmkChair}' AND direction_id IS NOT NULL AND is_active");
        e.HasIndex(x => x.DepartmentId);
        e.HasIndex(x => x.InstituteId);
        e.HasIndex(x => x.DirectionId);
        e.HasIndex(x => new { x.CanLogin, x.IsActive });

        e.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<Institute>().WithMany().HasForeignKey(x => x.InstituteId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<StudyDirection>().WithMany().HasForeignKey(x => x.DirectionId).OnDelete(DeleteBehavior.Restrict);
    }
}
