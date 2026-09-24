using System.Text;
using Umbraco.Community.Unlog.Engine;
using Xunit;

namespace Umbraco.Community.Unlog.Tests.Engine;

public sealed class CleanupEngineTests
{
    private const string Format = "UmbracoTraceLog.{0}..json";
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AppliesOverlappingRulesByEntryTimestampAcrossMachineNames()
    {
        using var folder = new TempFolder();
        var first = folder.Write("UmbracoTraceLog.OLDWORKER.20260701.json", Entries(
            Event("2026-09-01T00:00:00Z", null, "old information"),
            Event("2026-09-01T00:00:00Z", "Warning", "recent warning"),
            Event("2026-08-01T00:00:00Z", "Warning", "old warning"),
            Event("2026-08-01T00:00:00Z", "Error", "recent error"),
            Event("2026-05-01T00:00:00Z", "Fatal", "old fatal")));
        var second = folder.Write("UmbracoTraceLog.NEWWORKER.20260701.json", Entries(
            Event("2026-05-01T00:00:00Z", "Fatal", "old fatal")));
        var otherSink = folder.Write("Custom.20260701.json", Entries(
            Event("2026-05-01T00:00:00Z", "Fatal", "unrelated")));

        var result = await new CleanupEngine().RunAsync(folder.Path, Format,
            [new() { Level = "Information", Days = 14 },
             new() { Level = "Warning", Days = 30 },
             new() { Level = "All", Days = 90 }], Now);

        Assert.True(result.ScannedFiles == 2, $"Scanned {result.ScannedFiles}; errors: {string.Join("; ", result.Errors)}");
        Assert.Equal(1, result.ChangedFiles);
        Assert.Equal(1, result.DeletedFiles);
        Assert.Equal(4, result.DeletedEntries);
        Assert.Empty(result.Errors);
        Assert.False(File.Exists(second));
        Assert.Contains("recent warning", File.ReadAllText(first));
        Assert.Contains("recent error", File.ReadAllText(first));
        Assert.DoesNotContain("old information", File.ReadAllText(first));
        Assert.DoesNotContain("old warning", File.ReadAllText(first));
        Assert.Contains("unrelated", File.ReadAllText(otherSink));
    }

    [Fact]
    public async Task MalformedEntryLeavesWholeFileUntouched()
    {
        using var folder = new TempFolder();
        var content = Entries(Event("2026-01-01T00:00:00Z", "Error", "delete me"), "not json");
        var path = folder.Write("UmbracoTraceLog.WORKER.20260101.json", content);

        var result = await new CleanupEngine().RunAsync(folder.Path, Format,
            [new() { Level = "All", Days = 1 }], Now);

        Assert.Equal(content, File.ReadAllText(path));
        Assert.Equal(0, result.DeletedEntries);
        Assert.Equal(1, result.ParseErrors);
        Assert.Contains(result.Errors, error => error.FileName == Path.GetFileName(path) &&
            error.Message.Contains("Line 2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SkipsLockedAndTodayFiles()
    {
        using var folder = new TempFolder();
        var old = folder.Write("UmbracoTraceLog.WORKER.20260101.json", Entries(
            Event("2026-01-01T00:00:00Z", "Fatal", "locked")));
        var today = folder.Write("UmbracoTraceLog.WORKER.20260924.json", Entries(
            Event("2026-01-01T00:00:00Z", "Fatal", "today")));
        using var heldOpen = new FileStream(old, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = await new CleanupEngine().RunAsync(folder.Path, Format,
            [new() { Level = "All", Days = 1 }], Now);

        Assert.Equal(1, result.SkippedLockedFiles);
        Assert.Equal(1, result.SkippedTodayFiles);
        Assert.Equal(0, result.DeletedEntries);
        Assert.True(File.Exists(old));
        Assert.True(File.Exists(today));
    }

    [Fact]
    public async Task NoRulesOrInvalidRulesNeverDelete()
    {
        using var folder = new TempFolder();
        var path = folder.Write("UmbracoTraceLog.WORKER.20260101.json", Entries(
            Event("2026-01-01T00:00:00Z", "Fatal", "keep")));
        var engine = new CleanupEngine();

        var unconfigured = await engine.RunAsync(folder.Path, Format, [], Now);
        var invalid = await engine.RunAsync(folder.Path, Format,
            [new() { Level = "Bogus", Days = 0 }], Now);

        Assert.Empty(unconfigured.Errors);
        Assert.Equal(0, unconfigured.ScannedFiles);
        Assert.Equal(0, invalid.ScannedFiles);
        Assert.NotEmpty(invalid.Errors);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task RemovesPreexistingEmptyOldLogFile()
    {
        using var folder = new TempFolder();
        var path = folder.Write("UmbracoTraceLog.WORKER.20260101.json", string.Empty);

        var result = await new CleanupEngine().RunAsync(folder.Path, Format,
            [new() { Level = "All", Days = 90 }], Now);

        Assert.False(File.Exists(path));
        Assert.Equal(1, result.DeletedFiles);
        Assert.Equal(0, result.DeletedEntries);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task DeletesOnlyEntriesStrictlyOlderThanConfiguredAge()
    {
        using var folder = new TempFolder();
        var path = folder.Write("UmbracoTraceLog.WORKER.20260801.json", Entries(
            Event("2026-09-10T12:00:00Z", "Information", "exact cutoff"),
            Event("2026-09-10T11:59:59Z", "Information", "older"),
            Event("2026-01-01T00:00:00Z", "Error", "unmatched level")));

        var result = await new CleanupEngine().RunAsync(folder.Path, Format,
            [new() { Level = "Information", Days = 14 }], Now);

        Assert.Equal(1, result.DeletedEntries);
        Assert.Contains("exact cutoff", File.ReadAllText(path));
        Assert.Contains("unmatched level", File.ReadAllText(path));
        Assert.DoesNotContain("older", File.ReadAllText(path));
    }

    private static string Event(string timestamp, string? level, string message) =>
        $"{{\"@t\":\"{timestamp}\"{(level is null ? string.Empty : $",\"@l\":\"{level}\"")},\"@m\":\"{message}\"}}";

    private static string Entries(params string[] entries) => string.Join("\n", entries) + "\n";

    private sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "unlog-engine-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Write(string name, string content)
        {
            var path = System.IO.Path.Combine(Path, name);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
