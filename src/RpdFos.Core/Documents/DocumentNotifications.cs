using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Directory;

namespace RpdFos.Core.Documents;

/// <summary>
/// Уведомления о событиях документа в интерфейсе (FR-REV-08, этап 6.1, C3). У каждого события свои получатели: автор,
/// все действующие методисты или согласующие. Тому, кто совершил действие, уведомление не создаётся — кроме итога
/// печати (этап 7, A3): он идёт тому, кто запустил формирование.
/// <para>
/// Уведомления только добавляются в контекст: сохраняет вызывающий — тем же сохранением, что и событие, поэтому
/// событие и уведомление о нём появляются вместе или не появляются совсем. Время уведомления — время события (для смены
/// статуса это время записи в истории), и несколько уведомлений одного получателя из одного сохранения стоят в порядке
/// событий.
/// </para>
/// Готовая фраза — в <c>payload.text</c>, рядом параметры события для интерфейса: <c>title</c> (название документа),
/// <c>discipline_code</c>, <c>discipline</c>, <c>by</c> (кто совершил действие), <c>comment</c>, <c>remarks</c>
/// (число замечаний), <c>seats</c> (места согласования), <c>approval</c> (чем кончился приём); у печати — <c>format</c>,
/// <c>task_id</c> и <c>reason</c> (причина сбоя), у замены согласующего — <c>signatory</c> (новый согласующий). В фразах
/// нет родовых форм («Принят», «Согласующий: …»): пол автора и методиста системе неизвестен.
/// </summary>
public sealed class DocumentNotifications(AppDbContext db, DirectoryAccounts directory)
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Исход приёма для автора (<c>payload.approval</c>).</summary>
    public const string ApprovalStarted = "started", ApprovalAwaitingAssignment = "awaiting_assignment",
        ApprovalNotRequired = "not_required";

    /// <summary>Методистам: автор отправил документ на проверку; <paramref name="again"/> — повторно, с доработки.</summary>
    public async Task SubmittedAsync(Document document, Guid by, bool again, DateTimeOffset at, CancellationToken ct = default)
    {
        var to = Others(await directory.ActiveHoldersOfAsync(AppRoles.Methodist, ct), by);
        if (to.Count == 0) return;
        var who = await NameAsync(by, ct);
        await AddAsync(NotificationEvents.Submitted, document, to, at,
            Text($"{(again ? "Повторно отправлен" : "Отправлен")} на проверку: {document.Title}", $"Автор: {who}"),
            new JsonObject { ["by"] = who }, ct);
    }

    /// <summary>Автору: методист взял документ на проверку из его статуса, <paramref name="reason"/> — причина.</summary>
    public async Task TakenForReviewAsync(Document document, Guid by, string reason, DateTimeOffset at, CancellationToken ct = default)
    {
        var to = Others([document.AuthorId], by);
        if (to.Count == 0) return;
        var who = await NameAsync(by, ct);
        await AddAsync(NotificationEvents.TakenForReview, document, to, at,
            Text($"Взят на проверку методистом {who}: {document.Title}", $"Причина: {reason}"),
            new JsonObject { ["by"] = who, ["comment"] = reason }, ct);
    }

    /// <summary>Автору: документ возвращён на доработку; <paramref name="comment"/> — общий комментарий методиста, если он есть.</summary>
    public async Task ReturnedForReworkAsync(
        Document document, Guid by, int remarks, string? comment, DateTimeOffset at, CancellationToken ct = default)
    {
        var to = Others([document.AuthorId], by);
        if (to.Count == 0) return;
        var who = await NameAsync(by, ct);
        await AddAsync(NotificationEvents.ReturnedForRework, document, to, at,
            Text($"Возвращён на доработку: {document.Title}", $"Замечаний: {remarks}", $"Методист: {who}",
                comment is null ? null : $"Комментарий: {comment}"),
            new JsonObject { ["by"] = who, ["remarks"] = remarks, ["comment"] = comment }, ct);
    }

    /// <summary>Автору: принятый документ вернулся после исправления данных плана; замечания оставлены к разделам.</summary>
    public async Task ReturnedAfterPlanCorrectionAsync(
        Document document, Guid by, int remarks, DateTimeOffset at, CancellationToken ct = default)
    {
        var to = Others([document.AuthorId], by);
        if (to.Count == 0) return;
        var who = await NameAsync(by, ct);
        await AddAsync(NotificationEvents.ReturnedAfterPlanCorrection, document, to, at,
            Text($"Возвращён на доработку после исправления данных учебного плана: {document.Title}", $"Замечаний: {remarks}",
                $"Исправление данных плана: {who}"),
            new JsonObject { ["by"] = who, ["remarks"] = remarks }, ct);
    }

    /// <summary>
    /// Автору: документ принят. В тексте — чем кончился приём: согласование начато, ждёт назначения согласующих или не
    /// нужно (в листе согласования шаблона нет электронных мест, документ уже согласован).
    /// </summary>
    public async Task AcceptedAsync(Document document, Guid by, ApprovalStart start, DateTimeOffset at, CancellationToken ct = default)
    {
        var to = Others([document.AuthorId], by);
        if (to.Count == 0) return;
        var who = await NameAsync(by, ct);
        var (outcome, state) = document.Status == DocumentStatuses.Approved
            ? ("Электронных согласующих в листе согласования нет", ApprovalNotRequired)
            : start.Started
                ? ("Согласование начато", ApprovalStarted)
                : ($"Согласование ждёт назначения согласующих: {string.Join("; ", start.Unassigned)}", ApprovalAwaitingAssignment);
        await AddAsync(NotificationEvents.Accepted, document, to, at,
            Text($"Принят: {document.Title}", outcome, $"Методист: {who}"),
            new JsonObject
            {
                ["by"] = who, ["approval"] = state,
                ["seats"] = state == ApprovalAwaitingAssignment ? JsonSerializer.SerializeToNode(start.Unassigned) : null
            }, ct);
    }

    /// <summary>Автору и методистам: все отметили согласие, документ согласован. <paramref name="by"/> — последний отметивший.</summary>
    public async Task ApprovedAsync(Document document, Guid by, DateTimeOffset at, CancellationToken ct = default)
    {
        var to = Others((await directory.ActiveHoldersOfAsync(AppRoles.Methodist, ct)).Append(document.AuthorId), by);
        if (to.Count == 0) return;
        await AddAsync(NotificationEvents.Approved, document, to, at,
            Text($"Согласован: {document.Title}"), new JsonObject { ["by"] = await NameAsync(by, ct) }, ct);
    }

    /// <summary>
    /// Методистам: согласующий вернул документ с согласования; в тексте — кто, сколько замечаний к полям он оставил
    /// (этап 7, A2; без замечаний — не упоминается) и комментарий.
    /// </summary>
    public async Task ReturnedFromApprovalAsync(
        Document document, Guid by, string comment, int remarks, DateTimeOffset at, CancellationToken ct = default)
    {
        var to = Others(await directory.ActiveHoldersOfAsync(AppRoles.Methodist, ct), by);
        if (to.Count == 0) return;
        var who = await NameAsync(by, ct);
        await AddAsync(NotificationEvents.ReturnedFromApproval, document, to, at,
            Text($"Возвращён с согласования: {document.Title}", $"Согласующий: {who}",
                remarks > 0 ? $"Замечаний к полям: {remarks}" : null, $"Комментарий: {comment}"),
            new JsonObject { ["by"] = who, ["remarks"] = remarks, ["comment"] = comment }, ct);
    }

    /// <summary>
    /// Согласующим: документ ждёт их отметки. <paramref name="seatsBySignatory"/> — места каждого в листе согласования:
    /// заведующий кафедрой может занимать два, и ему идёт одно уведомление. <paramref name="by"/> запустил круг или
    /// назначил согласующего.
    /// </summary>
    public async Task AwaitingMarkAsync(
        Document document, Guid by, IReadOnlyDictionary<Guid, IReadOnlyList<string>> seatsBySignatory, DateTimeOffset at,
        CancellationToken ct = default)
    {
        var to = Others(seatsBySignatory.Keys, by);
        if (to.Count == 0) return;
        var who = await NameAsync(by, ct);
        foreach (var signatory in to)
        {
            var seats = seatsBySignatory[signatory];
            await AddAsync(NotificationEvents.AwaitingMark, document, [signatory], at,
                Text($"Ждёт вашего согласования: {document.Title}",
                    $"{(seats.Count == 1 ? "Место" : "Места")} в листе согласования: {string.Join("; ", seats)}"),
                new JsonObject { ["by"] = who, ["seats"] = JsonSerializer.SerializeToNode(seats) }, ct);
        }
    }

    /// <summary>
    /// Автору: методист запустил согласование принятого документа вручную — после назначения согласующих (этап 7, A3;
    /// идея этапа 6.1, C3). При приёме, который начинает круг сразу, об этом сообщает «Принят» (<see cref="AcceptedAsync"/>).
    /// </summary>
    public async Task ApprovalStartedAsync(Document document, Guid by, DateTimeOffset at, CancellationToken ct = default)
    {
        var to = Others([document.AuthorId], by);
        if (to.Count == 0) return;
        var who = await NameAsync(by, ct);
        await AddAsync(NotificationEvents.ApprovalStarted, document, to, at,
            Text($"Согласование начато: {document.Title}", $"Методист: {who}"), new JsonObject { ["by"] = who }, ct);
    }

    /// <summary>
    /// Прежнему согласующему: на идущем круге его место занял <paramref name="replacement"/> (FR-REV-13; этап 7, A3, идея
    /// этапа 6.1, C3). Документ ушёл из его очереди и, если других мест в нём у него нет, стал недоступен: непрочитанное
    /// «ждёт вашего согласования» по этому документу отмечается прочитанным, а «Согласующий заменён» идёт без ссылки —
    /// переход по ней дал бы 403. Осталось другое место (<paramref name="keepsAccess"/>) — документ ему доступен и всё ещё
    /// ждёт его отметки: прежнее уведомление остаётся, ссылка есть.
    /// </summary>
    /// <param name="seat">Название места в листе согласования, которое он потерял.</param>
    public async Task SignatoryReplacedAsync(
        Document document, Guid replaced, Guid replacement, string seat, bool keepsAccess, Guid by, DateTimeOffset at,
        CancellationToken ct = default)
    {
        if (!keepsAccess)
            foreach (var waiting in await db.Notifications.Where(n => n.RecipientId == replaced && n.DocumentId == document.Id &&
                             n.EventType == NotificationEvents.AwaitingMark && n.ReadAt == null).ToListAsync(ct))
                waiting.ReadAt = at;

        // Сам себя заменивший о замене знает — отмечать прочитанным нужно в любом случае, а сообщать ему не о чем.
        var to = Others([replaced], by);
        if (to.Count == 0) return;
        var next = await NameAsync(replacement, ct);
        await AddAsync(NotificationEvents.SignatoryReplaced, document, to, at,
            Text($"Согласующий заменён: {document.Title}", $"Место в листе согласования: {seat}", $"Новый согласующий: {next}"),
            new JsonObject { ["by"] = await NameAsync(by, ct), ["seats"] = new JsonArray(seat), ["signatory"] = next }, ct,
            linked: keepsAccess);
    }

    /// <summary>
    /// Тому, кто запустил формирование печатной формы: файл готов или не сформирован — тогда с причиной (этап 7, A3,
    /// FR-GEN-04, UC-10, шаг 4). Это итог его собственного запроса, поэтому фильтра «кроме совершившего» нет.
    /// Вызывается, когда задача уже завершена; уведомление ложится в то же сохранение, что и её завершение.
    /// </summary>
    public async Task PrintFinishedAsync(GenerationTask task, DateTimeOffset at, CancellationToken ct = default)
    {
        // Удалённый после постановки в очередь документ из выборки не исчезает: задача завершилась ошибкой, и причину
        // нужно показать, а не оборвать завершение исключением.
        var document = await db.Documents.IgnoreQueryFilters().AsNoTracking().SingleAsync(d => d.Id == task.DocumentId, ct);
        var format = task.Format.ToUpperInvariant();
        if (task.Status == GenerationStatuses.Succeeded)
        {
            await AddAsync(NotificationEvents.PrintReady, document, [task.RequestedBy], at,
                Text($"Печатная форма готова: {document.Title}", $"Формат: {format}"),
                new JsonObject { ["format"] = task.Format, ["task_id"] = task.Id }, ct);
            return;
        }

        // Сообщение исключения бывает многострочным: в ленте оно идёт одной фразой.
        var reason = string.Join(' ', (task.ErrorMessage ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (reason.Length == 0) reason = "причина не названа";
        await AddAsync(NotificationEvents.PrintFailed, document, [task.RequestedBy], at,
            Text($"Печатная форма не сформирована: {document.Title}", $"Формат: {format}", $"Причина: {reason}"),
            new JsonObject { ["format"] = task.Format, ["task_id"] = task.Id, ["reason"] = reason }, ct);
    }

    // linked: false — уведомление без ссылки на документ (document_id пуст), когда документ получателю недоступен;
    // название документа остаётся в тексте и параметрах.
    private async Task AddAsync(
        string eventType, Document document, IReadOnlyList<Guid> recipients, DateTimeOffset at, string text, JsonObject parameters,
        CancellationToken ct, bool linked = true)
    {
        var discipline = await db.Disciplines.AsNoTracking().Where(d => d.Id == document.DisciplineId)
            .Select(d => new { d.Code, d.Name }).SingleAsync(ct);
        var payload = new JsonObject
        {
            ["text"] = text, ["title"] = document.Title, ["discipline_code"] = discipline.Code, ["discipline"] = discipline.Name
        };
        // Параметр без значения (нет комментария) в payload не пишется.
        foreach (var (key, value) in parameters)
            if (value is not null) payload[key] = value.DeepClone();
        var json = payload.ToJsonString(Json);

        foreach (var recipient in recipients)
            db.Notifications.Add(new Notification
            {
                RecipientId = recipient, EventType = eventType, DocumentId = linked ? document.Id : null, Payload = json,
                CreatedAt = at
            });
    }

    // Совершившему действие уведомление не нужно; одному получателю — не больше одного уведомления о событии.
    private static List<Guid> Others(IEnumerable<Guid> recipients, Guid by) => recipients.Where(id => id != by).Distinct().ToList();

    private async Task<string> NameAsync(Guid employeeId, CancellationToken ct) =>
        await db.Employees.AsNoTracking().Where(e => e.Id == employeeId).Select(e => e.ShortName).SingleOrDefaultAsync(ct) ?? "";

    // Предложения через пробел; точка в конце ставится, если её нет: «Фамилия И. О.» уже кончается точкой.
    private static string Text(params string?[] sentences) => string.Join(' ',
        sentences.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).Select(s => s[^1] is '.' or '!' or '?' ? s : s + "."));
}
