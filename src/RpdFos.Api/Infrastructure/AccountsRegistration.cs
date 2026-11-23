using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RpdFos.Core.Accounts;

namespace RpdFos.Api.Infrastructure;

public static class AccountsRegistration
{
    /// <summary>
    /// Ссылки восстановления пароля и приглашения (этап 8, A6): сроки из настроек, провайдер токена приглашения, русские
    /// ошибки. Срок обычного провайдера — и у восстановления пароля, и у подтверждения email (A8).
    /// </summary>
    public static IdentityBuilder AddAccountLinks(this IdentityBuilder identity)
    {
        var services = identity.Services;
        services.AddOptions<AccountSettings>()
            .Validate(s => s.ResetLinkHours > 0 && s.InviteLinkHours > 0,
                "Accounts:ResetLinkHours и Accounts:InviteLinkHours — срок ссылки в часах, больше нуля.")
            .ValidateOnStart();
        services.AddOptions<DataProtectionTokenProviderOptions>()
            .Configure<IOptions<AccountSettings>>((o, s) => o.TokenLifespan = TimeSpan.FromHours(s.Value.ResetLinkHours));
        services.AddOptions<InvitationTokenProviderOptions>()
            .Configure<IOptions<AccountSettings>>((o, s) => o.TokenLifespan = TimeSpan.FromHours(s.Value.InviteLinkHours));
        services.AddScoped<PasswordLinks>();
        return identity
            .AddTokenProvider<InvitationTokenProvider>(InvitationTokenProvider.ProviderName)
            .AddErrorDescriber<RussianIdentityErrorDescriber>();
    }
}
