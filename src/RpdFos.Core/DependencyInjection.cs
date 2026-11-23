using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Interceptors;
using RpdFos.Core.Directory;
using RpdFos.Core.Documents;
using RpdFos.Core.DocumentTypes;
using RpdFos.Core.Import;
using RpdFos.Core.Mail;
using RpdFos.Core.Notifications;
using RpdFos.Core.Printing;
using RpdFos.Core.Reminders;
using RpdFos.Core.Templates;

namespace RpdFos.Core;

public static class DependencyInjection
{
    /// <summary>
    /// Регистрирует прикладное ядро: EF Core (PostgreSQL, snake_case), сквозные механизмы
    /// и таблицы OpenIddict. Вызывается из API, а с этапа 2 — и из фонового обработчика.
    /// </summary>
    public static IServiceCollection AddCore(this IServiceCollection services, string connectionString)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<VersioningInterceptor>();
        services.AddDbContext<AppDbContext>((sp, options) => options
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .UseOpenIddict()
            .AddInterceptors(sp.GetRequiredService<VersioningInterceptor>()));
        services.AddScoped<ReferenceImporter>();
        services.AddScoped<DirectoryAccounts>();
        services.AddScoped<ImportJob>();
        services.AddScoped<DirectoryImportJob>();
        services.AddScoped<MatrixRowBinding>();
        services.AddScoped<EmployeeDirectory>();
        services.AddSingleton<DocumentTypeRegistry>();
        services.AddScoped<TemplateLifecycle>();
        // Скомпилированные схемы и настроенный санитайзер — общие на приложение.
        services.AddSingleton<IDocumentValidator, DocumentValidator>();
        services.AddSingleton<RichTextSanitizer>();
        // Правила проверки (x-validation-rule): реестр собирает все реализации IValidationRule.
        services.AddOptions<ValidationRuleSettings>();
        services.AddSingleton<IValidationRule, TopicHoursMatchTotal>();
        services.AddSingleton<IValidationRule, SelfStudyKindsMatchPlan>();
        services.AddSingleton<IValidationRule, HoursMatchCurriculum>();
        services.AddSingleton<IValidationRule, ClassHoursMatchTotal>();
        services.AddSingleton<IValidationRule, CompetencesMatchMatrix>();
        services.AddSingleton<IValidationRule, LiteratureMainRecent>();
        services.AddSingleton<IValidationRule, LiteratureYearNotFuture>();
        services.AddSingleton<IValidationRule, GuidelineTextOrUrl>();
        services.AddSingleton<IValidationRule, SectionTitleForText>();
        // Правила ФОС и сквозной проверки РПД ↔ ФОС (этап 7, B8): одна ошибка и пять предупреждений.
        services.AddSingleton<IValidationRule, LinkedRpdMustBeApprovedOrUnderReview>();
        services.AddSingleton<IValidationRule, AllIndicatorsHaveMeans>();
        services.AddSingleton<IValidationRule, FosPassportConsistent>();
        services.AddSingleton<IValidationRule, ScaleMatchesControlType>();
        services.AddSingleton<IValidationRule, ControlFormsMatch>();
        services.AddSingleton<IValidationRule, FosRegulationThresholds>();
        services.AddSingleton<ValidationRuleSet>();
        services.AddScoped<DocumentProjections>();
        // Связь ФОС и РПД (этап 7, B3): создание ФОС, чтение связанной РПД автором ФОС, обновление снимка.
        services.AddScoped<LinkedDocuments>();
        services.AddScoped<FosCrossCheck>();
        services.AddScoped<FosRefresh>();
        // Банк заданий ФОС (этап 7, B5): «Перенумеровать» группу вместе со ссылками паспорта.
        services.AddScoped<FosBankRenumbering>();
        services.AddScoped<DocumentCreation>();
        services.AddScoped<DocumentHistory>();
        services.AddScoped<DocumentNotifications>();
        // Рисунки в форматированном тексте (этап 7, B4а): загрузка, права чтения, связи с документами.
        services.AddScoped<DocumentImages>();
        services.AddScoped<DocumentEditing>();
        services.AddScoped<DocumentReview>();
        services.AddScoped<SignatoryResolution>();
        services.AddScoped<DocumentApproval>();
        services.AddScoped<DocumentReopening>();
        services.AddScoped<DocumentArchiving>();
        services.AddScoped<DocumentTransfer>();
        // История документа для методиста и администратора (этап 8, A10).
        services.AddScoped<DocumentTimeline>();
        // Закрепление дисциплин за сотрудниками (этап 7, A10): доступ к документам дисциплины и право их создавать.
        services.AddScoped<DisciplineAssignments>();
        // Печатные формы: хранилище сформированных файлов на диске, настройки — раздел Generation.
        services.AddOptions<GenerationSettings>();
        services.AddSingleton<GeneratedFileStore>();
        services.AddScoped<DocumentPrinting>();
        services.AddScoped<GenerationJob>();
        services.AddScoped<GeneratedFilesCleanup>();
        // Почта (этап 7, A4): настройки — раздел Mail; отправитель — SMTP, в тестах подменяется; письма уходят фоновой задачей.
        // Час дайджеста проверяется при старте: неверный час иначе ломал бы задачу при каждом запуске.
        services.AddOptions<MailSettings>()
            .Validate(s => s.DigestHour is >= 0 and <= 23, "Mail:DigestHour — час от 0 до 23.")
            .Validate(s => MimeKit.MailboxAddress.TryParse(s.From, out _), "Mail:From — адрес отправителя не разобрать: «Имя <адрес>» или адрес.")
            .ValidateOnStart();
        services.AddSingleton<IMailSender, SmtpMailSender>();
        services.AddScoped<NotificationMailJob>();
        // Настройки уведомлений пользователя: профиль (A5), лента, почта.
        services.AddScoped<NotificationSettings>();
        // Напоминания о сроке доработки (этап 7, A6): настройки — раздел Reminders, задача раз в сутки. Неверные сроки
        // проверяются при старте: предупреждение, которое не раньше срока, пришло бы вместе с эскалацией.
        services.AddOptions<RemindersSettings>()
            .Validate(s => s.ReworkDays >= 2 && s.WarnDaysBefore >= 1 && s.WarnDaysBefore < s.ReworkDays,
                "Reminders:ReworkDays — не меньше 2 дней; Reminders:WarnDaysBefore — от 1 дня и меньше срока доработки.")
            .ValidateOnStart();
        // Час задачи — раньше часа дайджеста (A5, решение 4): правило смотрит и в настройки почты.
        services.AddSingleton<IValidateOptions<RemindersSettings>, RemindersSettingsValidator>();
        services.AddScoped<ReworkReminders>();
        return services;
    }
}
