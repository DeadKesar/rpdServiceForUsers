namespace RpdFos.Api.Infrastructure;

/// <summary>
/// Настройки сервера фоновых задач (раздел <c>Jobs</c>). Вынесены из <c>Program.cs</c>, чтобы значения по умолчанию и
/// проверка границ читались тестом: сам сервер задач в тестах не запускается.
/// </summary>
/// <param name="Enabled">Сервер задач и панель <c>/hangfire</c>.</param>
/// <param name="QueuePoll">
/// Как часто сервер сам заглядывает в очередь. Печать от опроса не зависит: задачу ставит тот же процесс, где работает
/// исполнитель, и хранилище будит его сразу (замер этапа 8, B3). Опрос — страховка для задач, вернувшихся в очередь без
/// сигнала (после сбоя исполнителя, после перезапуска), поэтому секунды, а не доли секунды: каждый опрос — запрос к базе.
/// </param>
/// <param name="GenerationWorkers">
/// Сколько документов формируется одновременно. Конвертер (LibreOffice) обрабатывает файлы по очереди, поэтому больше двух
/// исполнителей время не сокращают (замер этапа 8, B3).
/// </param>
public sealed record JobsSettings(bool Enabled, TimeSpan QueuePoll, int GenerationWorkers)
{
    public const double DefaultQueuePollSeconds = 5;
    public const int DefaultGenerationWorkers = 2;

    /// <summary>Меньше полусекунды опрос — это нагрузка на базу без выигрыша; больше пяти минут очередь «засыпает».</summary>
    public static readonly (double Min, double Max) QueuePollRangeSeconds = (0.5, 300);

    public const int MaxGenerationWorkers = 20;

    public static JobsSettings From(IConfiguration configuration)
    {
        var section = configuration.GetSection("Jobs");
        var poll = section.GetValue("QueuePollSeconds", DefaultQueuePollSeconds);
        if (poll < QueuePollRangeSeconds.Min || poll > QueuePollRangeSeconds.Max)
            throw new InvalidOperationException(
                $"Настройка Jobs:QueuePollSeconds = {poll}: допустимы значения от {QueuePollRangeSeconds.Min} до {QueuePollRangeSeconds.Max} секунд.");
        var workers = section.GetValue("GenerationWorkers", DefaultGenerationWorkers);
        if (workers < 1 || workers > MaxGenerationWorkers)
            throw new InvalidOperationException(
                $"Настройка Jobs:GenerationWorkers = {workers}: допустимы значения от 1 до {MaxGenerationWorkers}.");
        return new JobsSettings(section.GetValue("Enabled", true), TimeSpan.FromSeconds(poll), workers);
    }
}
