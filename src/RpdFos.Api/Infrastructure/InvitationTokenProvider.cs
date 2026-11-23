using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RpdFos.Core.Accounts;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Api.Infrastructure;

/// <summary>
/// Параметры токена приглашения. Свой тип нужен ради своего срока: у Identity срок один на провайдера, а у приглашения
/// он длиннее, чем у восстановления пароля. Своё <c>Name</c> разводит ключи защиты данных: токен одного провайдера
/// другим не принимается.
/// </summary>
public sealed class InvitationTokenProviderOptions : DataProtectionTokenProviderOptions
{
    public InvitationTokenProviderOptions() => Name = "InvitationDataProtectorTokenProvider";
}

/// <summary>Провайдер токенов приглашения («Задайте пароль»); как и прочие, привязан к штампу безопасности учётной записи.</summary>
public sealed class InvitationTokenProvider(
    IDataProtectionProvider protection, IOptions<InvitationTokenProviderOptions> options,
    ILogger<DataProtectorTokenProvider<AppUser>> logger)
    : DataProtectorTokenProvider<AppUser>(protection, options, logger)
{
    public const string ProviderName = PasswordLinks.InvitationProvider;
}
