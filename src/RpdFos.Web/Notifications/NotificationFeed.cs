using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using RpdFos.Web.Models;

namespace RpdFos.Web.Notifications;

/// <summary>
/// Лента уведомлений для колокольчика (этап 6.1, C5; FR-REV-08): последние уведомления и число непрочитанных вошедшего
/// пользователя. Обновляется запросом раз в минуту и при переходах между страницами — по архитектуре (ВКР_2_Архитектура_C4)
/// уведомления в интерфейсе приходят периодическим опросом сервера, а не push-доставкой.
/// <para>
/// Опрос фоновый: ни нехватка токена, ни обрыв связи, ни ответ сервера с ошибкой не показываются пользователю и не
/// перенаправляют на вход — лента остаётся прежней, следующий опрос повторит. Перенаправление на вход — только для
/// действия, которое пользователь совершил сам («Прочитано»), как на остальных страницах.
/// </para>
/// </summary>
public sealed class NotificationFeed(HttpClient http, NavigationManager navigation, AuthenticationStateProvider authentication)
    : IDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(1);

    /// <summary>Сколько последних уведомлений показывает колокольчик. Непрочитанных может быть больше — число приходит отдельно.</summary>
    public const int ListSize = 20;

    private CancellationTokenSource? _polling;
    private int _latest; // номер последнего начатого обновления или действия: ответ более раннего отбрасывается

    /// <summary>Период опроса; меняется до <see cref="Start"/> (в тестах — короткий).</summary>
    public TimeSpan Interval { get; set; } = DefaultInterval;

    public IReadOnlyList<NotificationItem> Items { get; private set; } = [];
    public int Unread { get; private set; }

    /// <summary>Первое обновление прошло: до него колокольчик не отличает «уведомлений нет» от «ещё не загружены».</summary>
    public bool Loaded { get; private set; }

    public bool IsPolling => _polling is not null;

    /// <summary>Лента или число изменились — колокольчику перерисоваться.</summary>
    public event Action? Changed;

    /// <summary>Начинает опрос: первое обновление сразу, дальше раз в <see cref="Interval"/> и при каждом переходе между страницами.</summary>
    public void Start()
    {
        if (_polling is not null) return;
        (Items, Unread, Loaded) = ([], 0, false); // чужих остатков в ленте быть не должно
        var polling = _polling = new CancellationTokenSource();
        navigation.LocationChanged += OnLocationChanged;
        _ = PollAsync(polling.Token);
    }

    /// <summary>Прекращает опрос и забывает ленту: после выхода пользователя его уведомления не должны остаться в памяти страницы.</summary>
    public void Stop()
    {
        if (_polling is not { } polling) return;
        _polling = null;
        navigation.LocationChanged -= OnLocationChanged;
        Interlocked.Increment(ref _latest); // ответ запроса, ушедшего до остановки, уже не нужен
        polling.Cancel();
        polling.Dispose();
        (Items, Unread, Loaded) = ([], 0, false);
        Changed?.Invoke();
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Обновляет ленту и число непрочитанных. Не бросает исключений: см. описание класса. Если вошедшего пользователя
    /// уже нет (выход), опрос прекращается.
    /// </summary>
    public Task RefreshAsync() => RefreshAsync(CancellationToken.None);

    // stop — токен опроса: обновление, которое опрос начал уже после остановки, ленту не заполняет.
    private async Task RefreshAsync(CancellationToken stop)
    {
        if (stop.IsCancellationRequested) return;
        var request = Interlocked.Increment(ref _latest);
        try
        {
            var state = await authentication.GetAuthenticationStateAsync();
            if (state.User.Identity?.IsAuthenticated != true)
            {
                Stop();
                return;
            }

            var items = http.GetFromJsonAsync<List<NotificationItem>>($"api/notifications?limit={ListSize}");
            var count = http.GetFromJsonAsync<UnreadCount>("api/notifications/unread-count");
            await Task.WhenAll(items, count);

            // Позже начато другое обновление или совершено действие — свежее их ответ, этот устарел.
            if (stop.IsCancellationRequested || request != Volatile.Read(ref _latest)) return;
            (Items, Unread, Loaded) = (await items ?? [], (await count)?.Unread ?? 0, true);
            Changed?.Invoke();
        }
        catch (Exception)
        {
            // Фоновый запрос: токена нет или он истёк (AccessTokenNotAvailableException), нет связи (HttpRequestException),
            // сервер ответил ошибкой, тело не разобралось — ответ один: лента остаётся прежней, пользователь ничего не видит.
        }
    }

    /// <summary>«Прочитано» для одного. <c>false</c> — отметить не удалось, интерфейс скажет об этом.</summary>
    public Task<bool> MarkReadAsync(Guid id) => ChangeAsync(() => http.PostAsync($"api/notifications/{id}/read", null));

    /// <summary>«Прочитать все». <c>false</c> — не удалось.</summary>
    public Task<bool> MarkAllReadAsync() => ChangeAsync(() => http.PostAsync("api/notifications/read-all", null));

    // Действие пользователя: после него — свежая лента с сервера, а не правка списка на месте: число непрочитанных
    // считает сервер, и «одно прочитано» не обязательно «на единицу меньше» (уведомление могло прийти или быть прочитано
    // в другой вкладке).
    private async Task<bool> ChangeAsync(Func<Task<HttpResponseMessage>> send)
    {
        Interlocked.Increment(ref _latest); // обновление, начатое до действия, показало бы ленту без него
        bool done;
        try
        {
            using var response = await send();
            // 404 — такого уведомления нет (прочитано в другой вкладке не бывает, но чужое или удалённое — да): отмечать нечего.
            done = response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound;
        }
        catch (AccessTokenNotAvailableException e)
        {
            e.Redirect();
            return false;
        }
        catch (HttpRequestException)
        {
            done = false;
        }

        await RefreshAsync();
        return done;
    }

    private async Task PollAsync(CancellationToken stop)
    {
        await RefreshAsync(stop);
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stop)) await RefreshAsync(stop);
        }
        catch (OperationCanceledException)
        {
            // Stop(): опрос закончен.
        }
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        // Страницы входа и выхода служебные: токен на них может быть уже не выдан или ещё не получен.
        if (e.Location.StartsWith(navigation.BaseUri + "authentication/", StringComparison.OrdinalIgnoreCase)) return;
        _ = RefreshAsync();
    }
}
