using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using RpdFos.Web.Models;
using RpdFos.Web.Notifications;
using Xunit;

namespace RpdFos.Api.Tests.Client;

/// <summary>
/// Лента уведомлений колокольчика (этап 6.1, C5): обновление, опрос, поведение при сбоях. Сервера и базы нет — запросы
/// принимает подставной обработчик; проверяется то, чего не увидишь на удачном пути в браузере: фоновый запрос с
/// истёкшим токеном молчит и на вход не перенаправляет, после выхода и остановки запросы прекращаются.
/// </summary>
public sealed class NotificationFeedTests
{
    private static readonly Guid DocumentId = Guid.NewGuid();

    private static NotificationItem Item(string text, bool read = false) =>
        new(Guid.NewGuid(), "document.submitted", DocumentId, text, DateTimeOffset.UtcNow, read ? DateTimeOffset.UtcNow : null);

    private static (NotificationFeed Feed, FakeServer Server, FakeNavigation Navigation, FakeAuthentication Auth) Create(
        TimeSpan? interval = null)
    {
        var server = new FakeServer();
        var navigation = new FakeNavigation();
        var auth = new FakeAuthentication();
        var feed = new NotificationFeed(new HttpClient(server) { BaseAddress = new Uri("https://localhost/") }, navigation, auth)
        {
            Interval = interval ?? TimeSpan.FromHours(1) // опрос по умолчанию не мешает: тест сам вызывает обновление
        };
        return (feed, server, navigation, auth);
    }

    [Fact]
    public async Task Refresh_loads_the_latest_list_and_the_unread_count()
    {
        var (feed, server, _, _) = Create();
        server.Items = [Item("Первое"), Item("Второе", read: true)];
        server.Unread = 37; // непрочитанных больше, чем в списке: число берётся из отдельного запроса

        await feed.RefreshAsync();

        Assert.True(feed.Loaded);
        Assert.Equal(new[] { "Первое", "Второе" }, feed.Items.Select(n => n.Text));
        Assert.Equal(37, feed.Unread);
        Assert.Contains($"GET /api/notifications?limit={NotificationFeed.ListSize}", server.Requests);
        Assert.Contains("GET /api/notifications/unread-count", server.Requests);
    }

    public static TheoryData<string> Failures =>
        ["no connection", "expired token", "401", "500", "not json"];

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Failed_background_refresh_is_silent_keeps_the_feed_and_does_not_redirect_to_login(string failure)
    {
        var (feed, server, navigation, _) = Create();
        server.Items = [Item("Первое")];
        server.Unread = 1;
        await feed.RefreshAsync();

        switch (failure)
        {
            case "no connection": server.Throw = new HttpRequestException("нет связи"); break;
            case "expired token": server.Throw = ExpiredToken(navigation); break;
            case "401": server.Status = HttpStatusCode.Unauthorized; break;
            case "500": server.Status = HttpStatusCode.InternalServerError; break;
            default: server.Body = "<html>не JSON</html>"; break;
        }
        server.Items = []; // если бы ответ дошёл, лента изменилась бы

        await feed.RefreshAsync(); // не бросает

        Assert.Equal(new[] { "Первое" }, feed.Items.Select(n => n.Text));
        Assert.Equal(1, feed.Unread);
        Assert.Empty(navigation.Navigations); // на вход не перенаправило
    }

    [Fact]
    public async Task Refresh_before_the_first_success_stays_empty_and_not_loaded()
    {
        var (feed, server, navigation, _) = Create();
        server.Throw = ExpiredToken(navigation);

        await feed.RefreshAsync();

        Assert.False(feed.Loaded); // колокольчик скажет «загрузка», а не «уведомлений нет»
        Assert.Empty(feed.Items);
        Assert.Equal(0, feed.Unread);
        Assert.Empty(navigation.Navigations);
    }

    [Fact]
    public async Task Start_polls_on_the_interval_and_Stop_ends_it()
    {
        var (feed, server, _, _) = Create(interval: TimeSpan.FromMilliseconds(30));
        server.Items = [Item("Первое")];
        server.Unread = 1;

        feed.Start();
        Assert.True(feed.IsPolling);
        await WaitUntil(() => server.Count("GET /api/notifications/unread-count") >= 3, "три опроса");
        Assert.Equal(1, feed.Unread);

        feed.Stop();
        Assert.False(feed.IsPolling);
        Assert.Equal(0, feed.Unread); // ленту выхода не помним
        Assert.False(feed.Loaded);

        await Task.Delay(150); // запрос, ушедший до остановки, успевает закончиться
        var requests = server.Requests.Count;
        await Task.Delay(300);  // десять периодов: опрос продолжался бы
        Assert.Equal(requests, server.Requests.Count);
        Assert.Equal(0, feed.Unread); // и поздний ответ ленты не вернул
    }

    [Fact]
    public async Task Start_twice_does_not_double_the_polling()
    {
        var (feed, server, _, _) = Create();

        feed.Start();
        feed.Start();
        await WaitUntil(() => feed.Loaded, "первое обновление");
        await Task.Delay(50);

        Assert.Equal(1, server.Count("GET /api/notifications/unread-count"));
        feed.Stop();
    }

    [Fact]
    public async Task Polling_ends_when_the_user_has_signed_out()
    {
        var (feed, server, _, auth) = Create(interval: TimeSpan.FromMilliseconds(30));
        server.Items = [Item("Первое")];
        server.Unread = 1;
        feed.Start();
        await WaitUntil(() => feed.Loaded, "первое обновление");

        auth.SignedIn = false; // выход

        await WaitUntil(() => !feed.IsPolling, "остановка опроса");
        Assert.Equal(0, feed.Unread);
        await Task.Delay(100);
        var requests = server.Requests.Count;
        await Task.Delay(200);
        Assert.Equal(requests, server.Requests.Count);
    }

    [Fact]
    public async Task Navigation_between_pages_refreshes_but_authentication_pages_do_not()
    {
        var (feed, server, navigation, _) = Create(); // период — час: обновляет только переход
        feed.Start();
        await WaitUntil(() => feed.Loaded, "первое обновление");
        Assert.Equal(1, server.Count("GET /api/notifications/unread-count"));

        navigation.NavigateTo("documents/" + DocumentId);
        await WaitUntil(() => server.Count("GET /api/notifications/unread-count") == 2, "обновление при переходе");

        navigation.NavigateTo("authentication/logout"); // служебная страница: токена на ней может не быть
        await Task.Delay(100);
        Assert.Equal(2, server.Count("GET /api/notifications/unread-count"));

        feed.Stop();
        navigation.NavigateTo("review"); // после остановки переходы не обновляют
        await Task.Delay(100);
        Assert.Equal(2, server.Count("GET /api/notifications/unread-count"));
    }

    [Fact]
    public async Task Mark_read_posts_and_reloads_the_feed()
    {
        var (feed, server, _, _) = Create();
        var item = Item("Первое");
        server.Items = [item];
        server.Unread = 1;
        await feed.RefreshAsync();

        server.OnRead = () => { server.Items = [item with { ReadAt = DateTimeOffset.UtcNow }]; server.Unread = 0; };
        var done = await feed.MarkReadAsync(item.Id);

        Assert.True(done);
        Assert.Contains($"POST /api/notifications/{item.Id}/read", server.Requests);
        Assert.Equal(0, feed.Unread);
        Assert.NotNull(feed.Items.Single().ReadAt);
    }

    [Fact]
    public async Task Read_all_posts_and_reloads_the_feed()
    {
        var (feed, server, _, _) = Create();
        server.Items = [Item("Первое"), Item("Второе")];
        server.Unread = 2;
        await feed.RefreshAsync();

        server.OnReadAll = () => { server.Items = server.Items.Select(n => n with { ReadAt = DateTimeOffset.UtcNow }).ToList(); server.Unread = 0; };
        var done = await feed.MarkAllReadAsync();

        Assert.True(done);
        Assert.Contains("POST /api/notifications/read-all", server.Requests);
        Assert.Equal(0, feed.Unread);
        Assert.All(feed.Items, n => Assert.NotNull(n.ReadAt));
    }

    [Fact]
    public async Task Mark_read_of_a_missing_notification_counts_as_done_and_shows_the_real_feed()
    {
        var (feed, server, _, _) = Create();
        server.Items = [Item("Первое")];
        server.Unread = 1;
        await feed.RefreshAsync();
        server.ReadStatus = HttpStatusCode.NotFound;
        server.Items = [];
        server.Unread = 0;

        var done = await feed.MarkReadAsync(Guid.NewGuid());

        Assert.True(done); // отмечать нечего
        Assert.Empty(feed.Items);
        Assert.Equal(0, feed.Unread);
    }

    [Fact]
    public async Task Mark_read_that_failed_says_so_and_does_not_throw()
    {
        var (feed, server, navigation, _) = Create();
        server.Items = [Item("Первое")];
        server.Unread = 1;
        await feed.RefreshAsync();

        server.ReadStatus = HttpStatusCode.InternalServerError;
        Assert.False(await feed.MarkReadAsync(Guid.NewGuid()));
        Assert.False(await feed.MarkAllReadAsync());

        server.Throw = new HttpRequestException("нет связи");
        Assert.False(await feed.MarkReadAsync(Guid.NewGuid()));

        Assert.Equal(1, feed.Unread); // лента прежняя
        Assert.Empty(navigation.Navigations);
    }

    [Fact]
    public async Task Mark_read_with_an_expired_token_redirects_to_login_like_the_other_pages()
    {
        var (feed, server, navigation, _) = Create();
        server.Throw = ExpiredToken(navigation);

        Assert.False(await feed.MarkReadAsync(Guid.NewGuid()));

        Assert.Single(navigation.Navigations); // действие пользователя, не фоновый опрос: вход нужен
    }

    [Fact]
    public async Task Answer_of_an_older_refresh_does_not_overwrite_a_newer_one()
    {
        var (feed, server, _, _) = Create();
        server.Items = [Item("Старое")];
        server.Unread = 1;
        var gate = server.HoldNextListResponse(); // первое обновление получит «Старое», но дойдёт позже

        var older = feed.RefreshAsync();
        await WaitUntil(() => server.Count($"GET /api/notifications?limit={NotificationFeed.ListSize}") == 1, "запрос первого обновления");

        server.Items = [Item("Новое")];
        server.Unread = 5;
        await feed.RefreshAsync(); // второе обновление идёт без задержки
        Assert.Equal(new[] { "Новое" }, feed.Items.Select(n => n.Text));

        gate.SetResult(); // и вот приходит ответ первого
        await older;

        Assert.Equal(new[] { "Новое" }, feed.Items.Select(n => n.Text));
        Assert.Equal(5, feed.Unread);
    }

    [Fact]
    public async Task Changed_is_raised_when_the_feed_changes()
    {
        var (feed, server, _, _) = Create();
        server.Items = [Item("Первое")];
        var raised = 0;
        feed.Changed += () => raised++;

        await feed.RefreshAsync();
        Assert.Equal(1, raised);

        feed.Start();
        feed.Stop(); // остановка очищает ленту — колокольчик перерисуется
        Assert.True(raised >= 2);
    }

    // Токен истёк и тихо не обновился: сам токена нет (null!), библиотека требует интерактивного входа — так же её
    // обработчик запросов прерывает любой запрос с таким токеном.
    private static AccessTokenNotAvailableException ExpiredToken(NavigationManager navigation) =>
        new(navigation, new AccessTokenResult(AccessTokenResultStatus.RequiresRedirect, null!, "authentication/login",
            new InteractiveRequestOptions { Interaction = InteractionType.GetToken, ReturnUrl = "/" }), []);

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Не дождались: {what}.");
            await Task.Delay(10);
        }
    }

    /// <summary>Сервер уведомлений: отвечает из полей и записывает запросы; может обрываться или отвечать ошибкой.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
        private readonly ConcurrentQueue<string> _requests = new();
        private TaskCompletionSource? _gate;

        public List<NotificationItem> Items { get; set; } = [];
        public int Unread { get; set; }
        public Exception? Throw { get; set; }
        public HttpStatusCode? Status { get; set; }
        public string? Body { get; set; }
        public HttpStatusCode ReadStatus { get; set; } = HttpStatusCode.NoContent;
        public Action? OnRead { get; set; }
        public Action? OnReadAll { get; set; }

        public IReadOnlyCollection<string> Requests => _requests;

        public int Count(string request) => _requests.Count(r => r == request);

        public TaskCompletionSource HoldNextListResponse() => _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            _requests.Enqueue($"{request.Method} {path}");
            if (Throw is not null) throw Throw;
            if (Status is { } status) return new HttpResponseMessage(status);

            if (request.Method == HttpMethod.Post)
            {
                if (path.EndsWith("/read-all")) OnReadAll?.Invoke();
                else if (path.EndsWith("/read")) OnRead?.Invoke();
                return new HttpResponseMessage(ReadStatus);
            }

            if (Body is not null) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body) };

            if (path.Contains("unread-count")) return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new UnreadCount(Unread), options: Web) };

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Items, options: Web) }; // снимок на момент запроса
            if (Interlocked.Exchange(ref _gate, null) is { } gate) await gate.Task;
            return response;
        }
    }

    /// <summary>Адресная строка страницы: переходы записываются, <c>NavigateToCore</c> переносит адрес и сообщает о смене.</summary>
    private sealed class FakeNavigation : NavigationManager
    {
        public FakeNavigation() => Initialize("https://localhost/", "https://localhost/");

        public List<string> Navigations { get; } = [];

        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
            Navigations.Add(uri);
            Uri = ToAbsoluteUri(uri).ToString();
            NotifyLocationChanged(isInterceptedLink: false);
        }
    }

    /// <summary>Состояние входа: вошёл или нет; меняется по ходу теста.</summary>
    private sealed class FakeAuthentication : AuthenticationStateProvider
    {
        public bool SignedIn { get; set; } = true;

        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            SignedIn ? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "Тест")], "test")) : new ClaimsPrincipal(new ClaimsIdentity())));
    }
}
