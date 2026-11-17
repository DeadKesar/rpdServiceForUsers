namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// Подменяемые часы для тестов (этап 7, A5): время задаётся и переводится вперёд вручную, часовой пояс сервера — любой,
/// какой нужен тесту. Отдельный пакет часов для тестов не нужен: достаточно переопределить два члена <see cref="TimeProvider"/>.
/// </summary>
public sealed class TestClock(DateTimeOffset now, TimeZoneInfo? zone = null) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override TimeZoneInfo LocalTimeZone { get; } = zone ?? TimeZoneInfo.Utc;

    public override DateTimeOffset GetUtcNow() => Now;

    /// <summary>Пояс с постоянным смещением от UTC — без переходов на летнее время, одинаковый на любой системе.</summary>
    public static TimeZoneInfo ZoneWithOffset(int hours) =>
        TimeZoneInfo.CreateCustomTimeZone($"test{hours:+0;-0}", TimeSpan.FromHours(hours), $"UTC{hours:+0;-0}", $"UTC{hours:+0;-0}");
}
