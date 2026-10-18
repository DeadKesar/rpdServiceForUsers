# Идеи за пределами заданий

*Замечено по ходу работы, в текущее задание не входит. Решение — за автором.*

- **Русские сообщения входа и выхода.** `RemoteAuthenticatorView` (`Pages/Authentication.razor`) показывает стандартные английские статусы («Checking login state…», «You are logged out.»). Их можно задать фрагментами `LoggingIn`, `CompletingLoggingIn`, `LogOut`, `LogOutSucceeded` и др.
- **Роль первого администратора.** В `AuthSeed` результат `AddToRoleAsync` не проверяется — та же проблема, что закрыта в `UsersModule` (шаг A3).
- **Комментарий «зачем» у явных ссылок** на `System.Security.Cryptography.Pkcs` и `.Xml` в `tests/RpdFos.Api.Tests/RpdFos.Api.Tests.csproj`: соглашение CLAUDE.md требует его. Причина — уязвимые 10.0.11 транзитивно от `OpenIddict.AspNetCore` 7.7.2 (NU1902/NU1903).
- **`Templates.razor`:** при создании тестового шаблона не перехватывается `AccessTokenNotAvailableException` — при истёкшем токене страница падает вместо перехода ко входу.
