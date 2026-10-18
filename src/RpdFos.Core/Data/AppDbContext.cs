using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<DocumentTemplate> DocumentTemplates => Set<DocumentTemplate>();

    // Справочники (контекст Reference) и справочник сотрудников.
    public DbSet<Institute> Institutes => Set<Institute>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<StudyDirection> StudyDirections => Set<StudyDirection>();
    public DbSet<StudyProfile> StudyProfiles => Set<StudyProfile>();
    public DbSet<Fgos> Fgos => Set<Fgos>();
    public DbSet<Competence> Competences => Set<Competence>();
    public DbSet<CompetenceIndicator> CompetenceIndicators => Set<CompetenceIndicator>();
    public DbSet<CurriculumPlan> CurriculumPlans => Set<CurriculumPlan>();
    public DbSet<Discipline> Disciplines => Set<Discipline>();
    public DbSet<DisciplineSemesterDetails> DisciplineSemesterDetails => Set<DisciplineSemesterDetails>();
    public DbSet<CompetenceMatrixEntry> CompetenceMatrix => Set<CompetenceMatrixEntry>();
    public DbSet<Employee> Employees => Set<Employee>();

    // Импорт исходных файлов (контекст Infrastructure).
    public DbSet<UploadedFile> UploadedFiles => Set<UploadedFile>();
    public DbSet<ParserRun> ParserRuns => Set<ParserRun>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Таблицы Identity: учётные записи, роли, связи. Таблицы OpenIddict добавляет UseOpenIddict().
        base.OnModelCreating(b);

        // Справочники и импорт — конфигурации в Data/Configurations.
        b.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

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
