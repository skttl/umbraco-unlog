using System.Globalization;
using Microsoft.Extensions.Configuration;
using Umbraco.Cms.Infrastructure.BackgroundJobs;

namespace Umbraco.Community.Unlog.Integration;

/// <summary>Runs on Umbraco's scheduling server; manual health check actions run locally.</summary>
public sealed class UnlogJob : IRecurringBackgroundJob
{
    private readonly UnlogRunner _runner;

    public UnlogJob(IConfiguration configuration, UnlogRunner runner)
    {
        _runner = runner;
        Period = ReadSchedule(configuration, "Period", TimeSpan.FromDays(1), allowZero: false);
        Delay = ReadSchedule(configuration, "Delay", TimeSpan.FromMinutes(15), allowZero: true);
    }

    public TimeSpan Period { get; }

    public TimeSpan Delay { get; }

    public event EventHandler? PeriodChanged
    {
        add { }
        remove { }
    }

    public Task RunJobAsync() => RunJobAsync(CancellationToken.None);

    public async Task RunJobAsync(CancellationToken cancellationToken)
    {
        await _runner.RunAsync(cancellationToken);
    }

    private static TimeSpan ReadSchedule(
        IConfiguration configuration,
        string key,
        TimeSpan fallback,
        bool allowZero)
    {
        string? raw = configuration[$"Umbraco:Community:Unlog:{key}"];
        return TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out TimeSpan value) &&
               (allowZero ? value >= TimeSpan.Zero : value > TimeSpan.Zero)
            ? value
            : fallback;
    }
}
