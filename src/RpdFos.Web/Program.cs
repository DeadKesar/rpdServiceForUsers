using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Logging;
using Radzen;
using RpdFos.Web;
using RpdFos.Web.Auth;
using RpdFos.Web.Notifications;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Консоль браузера — только предупреждения и ошибки: иначе в ней по восемь info-строк на каждый запрос HttpClient
// (опрос колокольчика — два запроса в минуту) и «Authorization failed» от каждой проверки ролей в меню.
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// Запросы к API того же хоста: обработчик сам прикладывает токен доступа к каждому запросу.
builder.Services.AddHttpClient("api", c => c.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress))
    .AddHttpMessageHandler<BaseAddressAuthorizationMessageHandler>();
builder.Services.AddScoped(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient("api"));

// Лента уведомлений колокольчика: одна на вкладку, опрос ведёт компонент в шапке (этап 6.1, C5).
builder.Services.AddScoped<NotificationFeed>();

// Компоненты Radzen: редактор форматированного текста документа.
builder.Services.AddRadzenComponents();

// Вход через сервер авторизации OpenIddict на том же хосте: authorization code + PKCE.
builder.Services.AddOidcAuthentication(options =>
{
    options.ProviderOptions.Authority = builder.HostEnvironment.BaseAddress;
    options.ProviderOptions.ClientId = "rpdfos-web";
    options.ProviderOptions.ResponseType = "code";

    // response_mode=query: библиотека Blazor не разбирает ошибки из фрагмента URL
    // (см. комментарий в примере OpenIddict «Balosar»).
    options.ProviderOptions.ResponseMode = "query";

    // Роли — в утверждении «role» (так их выдаёт сервер); несколько ролей приходят массивом — его разбирает фабрика.
    options.ProviderOptions.DefaultScopes.Add("roles");
    options.UserOptions.RoleClaim = "role";
}).AddAccountClaimsPrincipalFactory<RolesClaimsPrincipalFactory>();

await builder.Build().RunAsync();
