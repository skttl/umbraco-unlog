using Umbraco.Cms.Core.HealthChecks;
using Umbraco.Community.Unlog.Engine;

namespace Umbraco.Community.Unlog.Integration;

[HealthCheck("E418DA40-D822-419E-B011-E7C84220DC17", "Unlog retention",
    Description = "Shows the active file log retention rules and can run cleanup on this instance.",
    Group = "Configuration")]
public sealed class UnlogHealthCheck : HealthCheck
{
    private const string RunNowAlias = "unlog-run-now";
    private readonly UnlogRunner _runner;

    public UnlogHealthCheck(UnlogRunner runner) => _runner = runner;

    public override Task<IEnumerable<HealthCheckStatus>> GetStatusAsync()
    {
        UnlogSnapshot snapshot;
        try
        {
            snapshot = _runner.GetSnapshot();
        }
        catch (Exception exception)
        {
            return Task.FromResult<IEnumerable<HealthCheckStatus>>(
                [new HealthCheckStatus($"Cannot read Unlog configuration: {exception.Message}")
                {
                    ResultType = StatusResultType.Error
                }]);
        }

        var statuses = new List<HealthCheckStatus>();
        if (snapshot.Errors.Count > 0)
        {
            statuses.Add(new HealthCheckStatus("Unlog configuration is invalid: " + string.Join("; ", snapshot.Errors))
            {
                ResultType = StatusResultType.Error
            });
        }

        if (!snapshot.SinkEnabled)
        {
            statuses.Add(new HealthCheckStatus(snapshot.SinkMessage ?? "UmbracoFile is not enabled. No cleanup will run.")
            {
                ResultType = StatusResultType.Info
            });
        }

        if (snapshot.Options.Rules.Count == 0)
        {
            statuses.Add(new HealthCheckStatus("No retention rules are configured. Unlog will not delete anything.")
            {
                ResultType = StatusResultType.Info
            });
        }
        else
        {
            string rules = string.Join("; ", snapshot.Options.Rules.Select((rule, index) =>
                rule is null ? $"Rule {index + 1}: invalid" : $"{rule.Level} and lower: {rule.Days} days"));
            var status = new HealthCheckStatus(
                $"Rules: {rules}. Delay: {snapshot.Options.Delay}; repeat: {snapshot.Options.Period}. Directory: {snapshot.Directory}.")
            {
                ResultType = snapshot.Errors.Count == 0 && snapshot.SinkEnabled
                    ? StatusResultType.Success
                    : StatusResultType.Info
            };

            if (snapshot.Errors.Count == 0 && snapshot.SinkEnabled)
            {
                status.Actions =
                [
                    new HealthCheckAction(RunNowAlias, Id)
                    {
                        Name = "Run now",
                        Description = "Run cleanup on this instance using the current rules."
                    }
                ];
            }

            statuses.Add(status);
        }

        if (snapshot.RetentionWarning is not null)
        {
            statuses.Add(new HealthCheckStatus(snapshot.RetentionWarning)
            {
                ResultType = StatusResultType.Warning
            });
        }

        return Task.FromResult<IEnumerable<HealthCheckStatus>>(statuses);
    }

    public override async Task<HealthCheckStatus> ExecuteActionAsync(HealthCheckAction action)
    {
        if (!string.Equals(action.Alias, RunNowAlias, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Unknown Unlog health check action.");
        }

        CleanupResult? result = await _runner.RunAsync();
        return result is null
            ? new HealthCheckStatus("Unlog did not run. Check the current configuration and application log.")
            {
                ResultType = StatusResultType.Warning
            }
            : new HealthCheckStatus(
                result.SkippedDueToConcurrentRun
                    ? "Another instance is running Unlog. This request made no changes."
                    : $"Deleted {result.DeletedEntries} entries and {result.DeletedFiles} empty files; " +
                      $"skipped {result.SkippedLockedFiles} locked files; {result.Errors.Count} errors.")
            {
                ResultType = result.Errors.Count > 0 ? StatusResultType.Error : StatusResultType.Success
            };
    }

    public override HealthCheckStatus ExecuteAction(HealthCheckAction action) =>
        ExecuteActionAsync(action).GetAwaiter().GetResult();
}
