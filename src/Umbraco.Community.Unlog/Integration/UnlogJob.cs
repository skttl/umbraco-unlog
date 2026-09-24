using System.Globalization;
using Microsoft.Extensions.Configuration;
using Umbraco.Cms.Infrastructure.BackgroundJobs;

namespace Umbraco.Community.Unlog.Integration;

/// <summary>Runs on Umbraco's scheduling server; manual health check actions run locally.</summary>
public sealed class UnlogJob : RecurringBackgroundJobBase
{
    private readonly UnlogRunner _runner;
    private readonly TimeSpan _delay;

    public UnlogJob(IConfiguration configuration, UnlogRunner runner)
        : base(ReadSchedule(configuration, "Period", TimeSpan.FromDays(1), allowZero: false))
    {
        _runner = runner;
        _delay = ReadSchedule(configuration, "Delay", TimeSpan.FromMinutes(15), allowZero: true);
    }

    public override TimeSpan Delay => _delay;

    public override async Task RunJobAsync(CancellationToken cancellationToken)
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
