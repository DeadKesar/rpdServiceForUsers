using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using RpdFos.Web;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Запросы к API того же хоста: обработчик сам прикладывает токен доступа к каждому запросу.
builder.Services.AddHttpClient("api", c => c.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress))
    .AddHttpMessageHandler<BaseAddressAuthorizationMessageHandler>();
builder.Services.AddScoped(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient("api"));

// Вход через сервер авторизации OpenIddict на том же хосте: authorization code + PKCE.
builder.Services.AddOidcAuthentication(options =>
{
    options.ProviderOptions.Authority = builder.HostEnvironment.BaseAddress;
    options.ProviderOptions.ClientId = "rpdfos-web";
    options.ProviderOptions.ResponseType = "code";

    // response_mode=query: библиотека Blazor не разбирает ошибки из фрагмента URL
    // (см. комментарий в примере OpenIddict «Balosar»).
    options.ProviderOptions.ResponseMode = "query";

    // Роли — в утверждении «role» (так их выдаёт сервер).
    options.ProviderOptions.DefaultScopes.Add("roles");
    options.UserOptions.RoleClaim = "role";
});

await builder.Build().RunAsync();
