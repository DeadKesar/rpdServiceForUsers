using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.Data.Interceptors;

/// <summary>
/// Сквозной механизм (раздел 2.3 ВКР): при каждом сохранении увеличивает номер версии
/// и проставляет время изменения у изменённых записей. Эндпоинтам не нужно об этом помнить.
/// Позже здесь же появится запись журнала аудита.
/// </summary>
public sealed class VersioningInterceptor(TimeProvider clock) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Stamp(DbContext? db)
    {
        if (db is null) return;
        var now = clock.GetUtcNow();

        foreach (var entry in db.ChangeTracker.Entries<IVersioned>())
        {
            if (entry.State == EntityState.Modified)
            {
                // Исходная версия остаётся в WHERE (её мог подменить эндпоинт версией клиента),
                // новая — уходит в SET. Не совпало — EF бросит DbUpdateConcurrencyException.
                var original = (int)entry.Property(nameof(IVersioned.Version)).OriginalValue!;
                entry.Entity.Version = original + 1;
                entry.Entity.UpdatedAt = now;
            }
            else if (entry.State == EntityState.Added)
            {
                entry.Entity.UpdatedAt = now;
            }
        }
    }
}
