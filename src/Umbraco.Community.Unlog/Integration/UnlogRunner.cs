using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Infrastructure.Logging.Serilog;
using Umbraco.Community.Unlog.Engine;

namespace Umbraco.Community.Unlog.Integration;

/// <summary>Checks the active Umbraco file sink before allowing the cleanup engine to touch disk.</summary>
public sealed class UnlogRunner
{
    private readonly IHostEnvironment _hostEnvironment;
    private readonly ILoggingConfiguration _loggingConfiguration;
    private readonly UmbracoFileConfiguration _fileSinkConfiguration;
    private readonly IOptionsMonitor<UnlogOptions> _options;
    private readonly CleanupEngine _engine;
    private readonly ILogger<UnlogRunner> _logger;

    public UnlogRunner(
        IHostEnvironment hostEnvironment,
        ILoggingConfiguration loggingConfiguration,
        UmbracoFileConfiguration fileSinkConfiguration,
        IOptionsMonitor<UnlogOptions> options,
        CleanupEngine engine,
        ILogger<UnlogRunner> logger)
    {
        _hostEnvironment = hostEnvironment;
        _loggingConfiguration = loggingConfiguration;
        _fileSinkConfiguration = fileSinkConfiguration;
        _options = options;
        _engine = engine;
        _logger = logger;
    }

    public UnlogSnapshot GetSnapshot()
    {
        UnlogOptions configured = _options.CurrentValue;
        UnlogOptions options = new()
        {
            Delay = configured.Delay,
            Period = configured.Period,
            Rules = configured.Rules ?? []
        };
        List<string> errors = CleanupEngine.ValidateRules(configured.Rules).ToList();
        if (options.Period <= TimeSpan.Zero)
        {
            errors.Add("Period must be greater than zero.");
        }

        if (options.Delay < TimeSpan.Zero)
        {
            errors.Add("Delay cannot be negative.");
        }

        // Umbraco's own configuration reflects the sink added by MinimalConfiguration.
        // Inspecting Serilog:WriteTo misses that sink when it is not in appsettings.
        bool sinkEnabled = _fileSinkConfiguration.Enabled;
        string? sinkMessage = sinkEnabled ? null : "Serilog's UmbracoFile sink is not enabled.";
        if (sinkEnabled && _fileSinkConfiguration.RollingInterval != RollingInterval.Day)
        {
            errors.Add("Unlog supports UmbracoFile's daily rolling interval only.");
        }

        string configuredDirectory = _loggingConfiguration.LogDirectory;
        if (string.IsNullOrWhiteSpace(configuredDirectory))
        {
            errors.Add("Umbraco has no file log directory.");
        }

        string fileNameFormat = _loggingConfiguration.LogFileNameFormat;
        if (CleanupEngine.ValidateFileNameFormat(fileNameFormat) is { } formatError)
        {
            errors.Add(formatError);
        }

        string directory = ResolveDirectory(configuredDirectory);
        string? retentionWarning = sinkEnabled && options.Rules.Count > 0 &&
                                   _fileSinkConfiguration.RetainedFileCountLimit > 0
            ? $"Serilog retains at most {_fileSinkConfiguration.RetainedFileCountLimit} files per machine name; " +
              "it may remove files before Unlog's age rules."
            : null;

        return new UnlogSnapshot(options, directory, fileNameFormat, sinkEnabled, sinkMessage, retentionWarning, errors);
    }

    public async Task<CleanupResult?> RunAsync(CancellationToken cancellationToken = default)
    {
        UnlogSnapshot snapshot;
        try
        {
            snapshot = GetSnapshot();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unlog cannot read its configuration. No files were changed.");
            return null;
        }

        if (snapshot.Errors.Count > 0)
        {
            _logger.LogError("Unlog configuration is invalid: {Errors}. No files were changed.",
                string.Join("; ", snapshot.Errors));
            return null;
        }

        if (!snapshot.SinkEnabled)
        {
            _logger.LogInformation("Unlog skipped cleanup because UmbracoFile is disabled.");
            return null;
        }

        if (snapshot.Options.Rules.Count == 0)
        {
            _logger.LogInformation("Unlog skipped cleanup because no retention rules are configured.");
            return null;
        }

        try
        {
            CleanupResult result = await _engine.RunAsync(
                snapshot.Directory,
                snapshot.FileNameFormat,
                snapshot.Options.Rules,
                DateTimeOffset.UtcNow,
                cancellationToken);

            foreach (CleanupError error in result.Errors)
            {
                _logger.LogError("Unlog could not process {FileName}: {Error}", error.FileName, error.Message);
            }

            _logger.LogInformation(
                "Unlog cleanup: scanned {ScannedFiles} files, changed {ChangedFiles}, deleted {DeletedFiles} empty files and {DeletedEntries} entries; skipped {SkippedTodayFiles} current-day files and {SkippedLockedFiles} locked files; encountered {ParseErrors} parse errors; concurrent run skipped: {SkippedDueToConcurrentRun}.",
                result.ScannedFiles, result.ChangedFiles, result.DeletedFiles, result.DeletedEntries,
                result.SkippedTodayFiles, result.SkippedLockedFiles, result.ParseErrors,
                result.SkippedDueToConcurrentRun);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unlog cleanup failed.");
            return null;
        }
    }

    private string ResolveDirectory(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return string.Empty;
        }

        string path = configured.StartsWith("~/", StringComparison.Ordinal) ||
                      configured.StartsWith("~\\", StringComparison.Ordinal)
            ? configured[2..]
            : configured;
        return Path.GetFullPath(Path.IsPathRooted(path)
            ? path
            : Path.Combine(_hostEnvironment.ContentRootPath, path));
    }
}

public sealed record UnlogSnapshot(
    UnlogOptions Options,
    string Directory,
    string FileNameFormat,
    bool SinkEnabled,
    string? SinkMessage,
    string? RetentionWarning,
    IReadOnlyList<string> Errors);
