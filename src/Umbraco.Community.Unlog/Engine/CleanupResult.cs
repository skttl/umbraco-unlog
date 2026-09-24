namespace Umbraco.Community.Unlog.Engine;

public sealed record CleanupError(string FileName, string Message);

public sealed class CleanupResult
{
    public int ScannedFiles { get; internal set; }

    public int ChangedFiles { get; internal set; }

    public int DeletedFiles { get; internal set; }

    public long DeletedEntries { get; internal set; }

    public int SkippedTodayFiles { get; internal set; }

    public int SkippedLockedFiles { get; internal set; }

    public int ParseErrors { get; internal set; }

    public bool SkippedDueToConcurrentRun { get; internal set; }

    public List<CleanupError> Errors { get; } = [];
}
