using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
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
                // Признак «устарел» ортогонален содержимому и версию не меняет (FR-DOC-12): его ставят и публикация новой
                // редакции шаблона, и согласование документа, созданного на основе этого (этап 7, A11), — правка в открытом
                // редакторе или «В архив» не должны упасть с конфликтом версий из-за чужого события. Проверка версии остаётся
                // в WHERE: если документ уже изменили, согласование получит обычный конфликт (409).
                if (OnlyOutdatedChanged(entry)) continue;

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

    // Изменён только признак «устарел» документа — и ничего больше.
    private static bool OnlyOutdatedChanged(EntityEntry<IVersioned> entry) =>
        entry.Entity is Document &&
        entry.Properties.Where(p => p.IsModified).Select(p => p.Metadata.Name).SequenceEqual([nameof(Document.IsOutdated)]);
}
