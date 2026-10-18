using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<DocumentTemplate> DocumentTemplates => Set<DocumentTemplate>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Таблицы Identity: учётные записи, роли, связи. Таблицы OpenIddict добавляет UseOpenIddict().
        base.OnModelCreating(b);

        b.Entity<DocumentTemplate>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasMaxLength(32);
            e.Property(x => x.Name).HasMaxLength(512);
            e.Property(x => x.Status).HasMaxLength(32);

            // Оптимистичная блокировка: версия — токен конкуренции.
            // В WHERE запроса UPDATE уходит исходная версия, новую проставляет VersioningInterceptor.
            e.Property(x => x.Version).IsConcurrencyToken();

            // Мягкое удаление: удалённые записи скрыты глобальным фильтром.
            e.HasQueryFilter(x => !x.IsDeleted);
        });
    }
}
