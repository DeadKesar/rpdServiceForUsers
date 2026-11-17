namespace RpdFos.Core.Mail;

/// <summary>
/// Расписание дайджеста (этап 7, A5): час <see cref="MailSettings.DigestHour"/> по времени сервера. Отдельной задачи на
/// этот час нет — минутная задача отправки (<see cref="NotificationMailJob"/>) для каждого уведомления «раз в день» считает
/// срок его письма и отправляет, когда срок пришёл. Поэтому дайджест, который не ушёл в свой час (почтовый сервер или сам
/// хост не работали), уходит при первом же запуске после, а запуска «ровно в час» ждать не нужно.
/// </summary>
public static class DigestSchedule
{
    /// <summary>
    /// Ближайший час дайджеста строго после <paramref name="after"/>: сегодня в <paramref name="hour"/>:00 по времени
    /// <paramref name="zone"/>, если он ещё не наступил, иначе завтра. Уведомление, созданное ровно в этот час, уходит
    /// уже следующим дайджестом.
    /// </summary>
    public static DateTimeOffset NextAfter(DateTimeOffset after, int hour, TimeZoneInfo zone)
    {
        var day = TimeZoneInfo.ConvertTime(after, zone).Date;
        var today = At(day, hour, zone);
        return today > after ? today : At(day.AddDays(1), hour, zone);
    }

    private static DateTimeOffset At(DateTime day, int hour, TimeZoneInfo zone)
    {
        var moment = new DateTime(day.Year, day.Month, day.Day, hour, 0, 0, DateTimeKind.Unspecified);
        // В сутки перехода на летнее время нужного часа нет: дайджест уходит на час позже.
        if (zone.IsInvalidTime(moment)) moment = moment.AddHours(1);
        return new DateTimeOffset(moment, zone.GetUtcOffset(moment));
    }
}
