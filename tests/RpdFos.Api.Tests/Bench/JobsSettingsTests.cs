using Microsoft.Extensions.Configuration;
using RpdFos.Api.Infrastructure;
using Xunit;

namespace RpdFos.Api.Tests.Bench;

/// <summary>Настройки сервера задач (этап 8, B3) и защита обычного прогона от замера скорости печати.</summary>
public class JobsSettingsTests
{
    private static JobsSettings Read(params (string Key, string Value)[] values) =>
        JobsSettings.From(new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build());

    [Fact]
    public void Defaults_are_used_when_settings_are_absent()
    {
        var settings = Read();

        Assert.True(settings.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(5), settings.QueuePoll);
        Assert.Equal(2, settings.GenerationWorkers);
    }

    [Fact]
    public void Settings_are_read_from_the_jobs_section_including_fractions_of_a_second()
    {
        var settings = Read(("Jobs:Enabled", "false"), ("Jobs:QueuePollSeconds", "0.5"), ("Jobs:GenerationWorkers", "4"));

        Assert.False(settings.Enabled);
        Assert.Equal(TimeSpan.FromMilliseconds(500), settings.QueuePoll);
        Assert.Equal(4, settings.GenerationWorkers);
    }

    [Theory]
    [InlineData("Jobs:QueuePollSeconds", "0", "Jobs:QueuePollSeconds")]
    [InlineData("Jobs:QueuePollSeconds", "3600", "Jobs:QueuePollSeconds")]
    [InlineData("Jobs:GenerationWorkers", "0", "Jobs:GenerationWorkers")]
    [InlineData("Jobs:GenerationWorkers", "500", "Jobs:GenerationWorkers")]
    public void Out_of_range_value_stops_the_host_and_names_the_setting(string key, string value, string named)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Read((key, value)));

        Assert.Contains(named, error.Message);
    }

    [Fact]
    public void Print_speed_bench_is_skipped_in_the_ordinary_run()
    {
        // Замер долгий и ходит в настоящий конвертер: в обычный прогон он не входит, пока не задана RPDFOS_BENCH=1.
        if (Environment.GetEnvironmentVariable(PrintSpeedBench.Switch) == "1") return;

        Assert.NotNull(new BenchFactAttribute().Skip);
    }
}
