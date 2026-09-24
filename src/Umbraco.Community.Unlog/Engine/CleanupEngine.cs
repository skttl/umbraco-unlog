using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Umbraco.Community.Unlog.Engine;

/// <summary>Applies retention rules only to Umbraco's rolling CLEF JSON files.</summary>
public sealed class CleanupEngine
{
    private const string RunLockFileName = ".umbraco-unlog.lock";
    private static readonly string[] Levels = ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];

    public static string? ValidateFileNameFormat(string? fileNameFormat) =>
        TryCreateFilePattern(fileNameFormat, out _, out var error) ? null : error;

    public static IReadOnlyList<string> ValidateRules(IReadOnlyList<RetentionRule>? rules)
    {
        if (rules is null)
        {
            return ["Rules must be an array."];
        }

        var errors = new List<string>();
        for (var index = 0; index < rules.Count; index++)
        {
            var rule = rules[index];
            if (rule is null)
            {
                errors.Add($"Rule {index + 1} is null.");
                continue;
            }

            if (!TryGetLevel(rule.Level, out _))
            {
                errors.Add($"Rule {index + 1} has an invalid Level. Use Verbose, Debug, Information, Warning, Error, Fatal, or All.");
            }

            if (rule.Days <= 0)
            {
                errors.Add($"Rule {index + 1} must have Days greater than zero.");
            }
        }

        return errors;
    }

    public async Task<CleanupResult> RunAsync(
        string directory,
        string fileNameFormat,
        IReadOnlyList<RetentionRule> rules,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var result = new CleanupResult();
        foreach (var error in ValidateRules(rules))
        {
            result.Errors.Add(new CleanupError(string.Empty, error));
        }

        if (!TryCreateFilePattern(fileNameFormat, out var filePattern, out var patternError))
        {
            result.Errors.Add(new CleanupError(string.Empty, patternError));
        }

        if (result.Errors.Count > 0 || rules.Count == 0)
        {
            return result;
        }

        if (!Directory.Exists(directory))
        {
            result.Errors.Add(new CleanupError(string.Empty, $"Log directory does not exist: {directory}"));
            return result;
        }

        // The lock is shared by all Unlog instances pointed at this directory. It is intentionally
        // kept as a separate, non-JSON file, so an interrupted process releases it automatically.
        FileStream? runLock;
        try
        {
            runLock = new FileStream(Path.Combine(directory, RunLockFileName), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception) when (IsSharingViolation(exception))
        {
            result.SkippedDueToConcurrentRun = true;
            return result;
        }
        catch (IOException exception)
        {
            result.Errors.Add(new CleanupError(RunLockFileName, exception.Message));
            return result;
        }
        catch (UnauthorizedAccessException exception)
        {
            result.Errors.Add(new CleanupError(RunLockFileName, exception.Message));
            return result;
        }

        await using (runLock)
        {
            var cutoffs = BuildCutoffs(rules, nowUtc.ToUniversalTime());
            try
            {
                foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fileName = Path.GetFileName(path);
                    var match = filePattern!.Match(fileName);
                    if (!match.Success)
                    {
                        continue;
                    }

                    result.ScannedFiles++;
                    if (!DateOnly.TryParseExact(match.Groups["date"].Value, "yyyyMMdd",
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    {
                        result.Errors.Add(new CleanupError(fileName, "The rolling file name contains an invalid date."));
                        continue;
                    }
                    if (date >= DateOnly.FromDateTime(nowUtc.UtcDateTime))
                    {
                        result.SkippedTodayFiles++;
                        continue;
                    }

                    await ProcessFileAsync(path, fileName, cutoffs, result, cancellationToken);
                }
            }
            catch (DirectoryNotFoundException exception)
            {
                result.Errors.Add(new CleanupError(string.Empty, exception.Message));
            }
            catch (UnauthorizedAccessException exception)
            {
                result.Errors.Add(new CleanupError(string.Empty, exception.Message));
            }
            catch (IOException exception)
            {
                result.Errors.Add(new CleanupError(string.Empty, exception.Message));
            }
        }

        return result;
    }

    private static async Task ProcessFileAsync(
        string path,
        string fileName,
        DateTimeOffset?[] cutoffs,
        CleanupResult result,
        CancellationToken cancellationToken)
    {
        FileStream source;
        try
        {
            source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (IOException exception) when (IsSharingViolation(exception))
        {
            result.SkippedLockedFiles++;
            return;
        }
        catch (IOException exception)
        {
            result.Errors.Add(new CleanupError(fileName, exception.Message));
            return;
        }
        catch (UnauthorizedAccessException exception)
        {
            result.Errors.Add(new CleanupError(fileName, exception.Message));
            return;
        }

        var tempPath = Path.Combine(Path.GetDirectoryName(path)!, $".{fileName}.{Guid.NewGuid():N}.unlog.tmp");
        long deletedEntries = 0;
        long retainedEntries = 0;
        long originalLength = 0;
        DateTime originalWriteTime;
        try
        {
            await using (source)
            {
                originalLength = source.Length;
                originalWriteTime = File.GetLastWriteTimeUtc(path);
                await using var destination = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

                var readBuffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                var lineBuffer = new ArrayBufferWriter<byte>();
                try
                {
                    var lineNumber = 0;
                    int read;
                    while ((read = await source.ReadAsync(readBuffer, cancellationToken)) > 0)
                    {
                        var offset = 0;
                        while (offset < read)
                        {
                            var remainder = readBuffer.AsSpan(offset, read - offset);
                            var newlineIndex = remainder.IndexOf((byte)'\n');
                            var count = newlineIndex < 0 ? remainder.Length : newlineIndex + 1;
                            lineBuffer.Write(remainder[..count]);
                            offset += count;

                            if (newlineIndex < 0)
                            {
                                continue;
                            }

                            lineNumber++;
                            bool retained;
                            try
                            {
                                retained = await ProcessLineAsync(lineBuffer.WrittenMemory, destination, cutoffs,
                                    cancellationToken);
                            }
                            catch (InvalidDataException exception)
                            {
                                throw new InvalidDataException($"Line {lineNumber}: {exception.Message}", exception);
                            }

                            if (!retained)
                            {
                                deletedEntries++;
                            }
                            else
                            {
                                retainedEntries++;
                            }

                            lineBuffer.Clear();
                        }
                    }

                    if (lineBuffer.WrittenCount > 0)
                    {
                        lineNumber++;
                        bool retained;
                        try
                        {
                            retained = await ProcessLineAsync(lineBuffer.WrittenMemory, destination, cutoffs,
                                cancellationToken);
                        }
                        catch (InvalidDataException exception)
                        {
                            throw new InvalidDataException($"Line {lineNumber}: {exception.Message}", exception);
                        }

                        if (!retained)
                        {
                            deletedEntries++;
                        }
                        else
                        {
                            retainedEntries++;
                        }
                    }
                }
                catch (InvalidDataException exception)
                {
                    result.ParseErrors++;
                    result.Errors.Add(new CleanupError(fileName, exception.Message));
                    return;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(readBuffer);
                }

                await destination.FlushAsync(cancellationToken);
            }

            if (deletedEntries == 0 && originalLength > 0)
            {
                return;
            }

            try
            {
                // Reacquire the original after staging. Sharing deletion, but neither reads nor
                // writes, lets us atomically replace/delete it while preventing a new writer.
                using var commitGuard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete);
                if (commitGuard.Length != originalLength || File.GetLastWriteTimeUtc(path) != originalWriteTime)
                {
                    result.Errors.Add(new CleanupError(fileName, "File changed while it was being processed; it was left untouched."));
                    return;
                }

                if (retainedEntries == 0)
                {
                    File.Delete(path);
                    result.DeletedFiles++;
                }
                else
                {
                    File.Replace(tempPath, path, null);
                    result.ChangedFiles++;
                }

                result.DeletedEntries += deletedEntries;
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                result.SkippedLockedFiles++;
            }
            catch (IOException exception)
            {
                result.Errors.Add(new CleanupError(fileName, exception.Message));
            }
            catch (UnauthorizedAccessException exception)
            {
                result.Errors.Add(new CleanupError(fileName, exception.Message));
            }
        }
        catch (IOException exception)
        {
            result.Errors.Add(new CleanupError(fileName, exception.Message));
        }
        catch (UnauthorizedAccessException exception)
        {
            result.Errors.Add(new CleanupError(fileName, exception.Message));
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (IOException)
            {
                // The original file remains intact; orphaned temp files can be removed separately.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task<bool> ProcessLineAsync(
        ReadOnlyMemory<byte> line,
        FileStream destination,
        DateTimeOffset?[] cutoffs,
        CancellationToken cancellationToken)
    {
        var json = line;
        if (json.Length > 0 && json.Span[^1] == '\n')
        {
            json = json[..^1];
        }

        if (json.Length > 0 && json.Span[^1] == '\r')
        {
            json = json[..^1];
        }

        if (json.Length >= 3 && json.Span[0] == 0xEF && json.Span[1] == 0xBB && json.Span[2] == 0xBF)
        {
            json = json[3..];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("@t", out var timestampElement) ||
                timestampElement.ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParse(timestampElement.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var timestamp))
            {
                throw new InvalidDataException("A CLEF entry has no valid @t timestamp.");
            }

            var level = 2; // Serilog omits @l for Information events.
            if (root.TryGetProperty("@l", out var levelElement))
            {
                if (levelElement.ValueKind != JsonValueKind.String ||
                    !TryGetLevel(levelElement.GetString(), out level) || level == Levels.Length)
                {
                    throw new InvalidDataException("A CLEF entry has an unknown @l level.");
                }
            }

            if (cutoffs[level] is { } cutoff && timestamp.ToUniversalTime() < cutoff)
            {
                return false;
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("A CLEF entry contains invalid JSON.", exception);
        }

        await destination.WriteAsync(line, cancellationToken);
        return true;
    }

    private static DateTimeOffset?[] BuildCutoffs(IReadOnlyList<RetentionRule> rules, DateTimeOffset nowUtc)
    {
        var cutoffs = new DateTimeOffset?[Levels.Length];
        foreach (var rule in rules)
        {
            TryGetLevel(rule.Level, out var threshold);
            var cutoff = nowUtc.AddDays(-rule.Days);
            for (var level = 0; level < Levels.Length && level <= threshold; level++)
            {
                if (cutoffs[level] is null || cutoff > cutoffs[level])
                {
                    cutoffs[level] = cutoff;
                }
            }
        }

        return cutoffs;
    }

    private static bool TryGetLevel(string? level, out int value)
    {
        if (string.Equals(level, "All", StringComparison.OrdinalIgnoreCase))
        {
            value = Levels.Length;
            return true;
        }

        for (var index = 0; index < Levels.Length; index++)
        {
            if (string.Equals(level, Levels[index], StringComparison.OrdinalIgnoreCase))
            {
                value = index;
                return true;
            }
        }

        value = -1;
        return false;
    }

    private static bool IsSharingViolation(IOException exception)
    {
        var win32Code = exception.HResult & 0xFFFF;
        return win32Code is 32 or 33; // ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION
    }

    private static bool TryCreateFilePattern(string? fileNameFormat, out Regex? pattern, out string error)
    {
        pattern = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(fileNameFormat) || fileNameFormat.Contains('/') || fileNameFormat.Contains('\\'))
        {
            error = "FileNameFormat must be a file name, not a path.";
            return false;
        }

        const string marker = "..json";
        if (!fileNameFormat.EndsWith(marker, StringComparison.OrdinalIgnoreCase))
        {
            error = "FileNameFormat must end in '..json' for Umbraco rolling CLEF files.";
            return false;
        }

        var prefix = fileNameFormat[..^marker.Length];
        if (prefix.Length == 0 || !Regex.IsMatch(prefix, @"^(?:[^{}]|\{\d+\})+$"))
        {
            error = "FileNameFormat contains an unsupported placeholder.";
            return false;
        }

        var escapedPrefix = new StringBuilder();
        for (var index = 0; index < prefix.Length; index++)
        {
            if (prefix[index] == '{')
            {
                var end = prefix.IndexOf('}', index + 1);
                escapedPrefix.Append("[^.]+");
                index = end;
            }
            else
            {
                escapedPrefix.Append(Regex.Escape(prefix[index].ToString()));
            }
        }

        pattern = new Regex($@"^{escapedPrefix}\.(?<date>\d{{8}})(?:_\d{{3}})?\.json$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
        return true;
    }
}
