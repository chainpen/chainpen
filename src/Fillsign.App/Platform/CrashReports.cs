using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Fillsign.App.Platform;

/// <summary>
/// Opt-in crash reports (FS-902, NFR-2, FS-016). Off until the person says yes. When on, an unexpected error is written
/// to a small text file on this computer; nothing is sent by the app. On the next start the person can open a
/// pre-filled GitHub issue with it, see exactly what it says, and send it or not.
/// A report holds the app version, the system, and which code failed: exception types and method names only. Error
/// messages are left out, since they can name files; so is anything from the document.
/// </summary>
internal static class CrashReports
{
    /// <summary>Where reports go; GitHub opens a new issue form with the title and text filled in.</summary>
    public const string IssueUrl = "https://github.com/fillsign/fillsign/issues/new";

    /// <summary>Browsers and GitHub cope with links up to about 8,000 characters; the report is cut to fit.</summary>
    private const int MaxReportLength = 6_000;

    private const int MaxReportsKept = 5;

    public static string Folder => Path.Combine(AppSettings.Folder, "Crash reports");

    /// <summary>Whether the person chose to send reports (Settings, or when asked on the start screen).</summary>
    public static bool IsOn => AppSettings.Load().SendCrashReports == true;

    /// <summary>Writes a report for an unexpected error, if reports are on. Never throws.</summary>
    public static void Record(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        try
        {
            if (!IsOn)
            {
                return;
            }

            Directory.CreateDirectory(Folder);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string name = $"crash-{now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)}.txt";
            File.WriteAllText(Path.Combine(Folder, name), Describe(exception, now));

            // A crash loop shouldn't fill the disk: keep the newest few.
            foreach (string old in Pending().Skip(MaxReportsKept))
            {
                File.Delete(old);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"A crash report could not be written. {ex.Message}");
        }
    }

    /// <summary>Reports not yet sent or deleted, newest first.</summary>
    public static IReadOnlyList<string> Pending()
    {
        try
        {
            return Directory.Exists(Folder)
                ? [.. Directory.GetFiles(Folder, "crash-*.txt").OrderDescending(StringComparer.Ordinal)]
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Crash reports could not be listed. {ex.Message}");
            return [];
        }
    }

    /// <summary>The newest report as a link to a pre-filled GitHub issue. Null when there's none.</summary>
    public static Uri? IssueLinkForNewest()
    {
        if (Pending() is not [string newest, ..])
        {
            return null;
        }

        try
        {
            return IssueLink(File.ReadAllText(newest));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"A crash report could not be read. {ex.Message}");
            return null;
        }
    }

    public static Uri IssueLink(string report)
    {
        ArgumentNullException.ThrowIfNull(report);
        string body = report.Length > MaxReportLength ? report[..MaxReportLength] + "\n…" : report;
        return new Uri($"{IssueUrl}?title={Uri.EscapeDataString("Crash report")}&labels=crash&body={Uri.EscapeDataString(body)}");
    }

    /// <summary>Deletes every waiting report (after it's been opened to send, or the person said no).</summary>
    public static void DeleteAll()
    {
        foreach (string report in Pending())
        {
            try
            {
                File.Delete(report);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning($"A crash report could not be deleted. {ex.Message}");
            }
        }
    }

    /// <summary>The report's text: version, system and the failing code, with no messages, paths or document data.</summary>
    public static string Describe(Exception exception, DateTimeOffset when)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Fillsign {App.DisplayVersion}");
        text.AppendLine(CultureInfo.InvariantCulture, $"System: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        text.AppendLine(CultureInfo.InvariantCulture, $"Runtime: {RuntimeInformation.FrameworkDescription}");
        text.AppendLine(CultureInfo.InvariantCulture, $"When: {when.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)}");
        text.AppendLine();

        Exception? current = exception;
        for (int depth = 0; current is not null && depth < 5; depth++, current = current.InnerException)
        {
            text.AppendLine(depth == 0 ? current.GetType().FullName : $"Caused by {current.GetType().FullName}");
            foreach (StackFrame frame in new StackTrace(current, fNeedFileInfo: false).GetFrames())
            {
                if (frame.GetMethod() is { } method)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"  at {method.DeclaringType?.FullName}.{method.Name}");
                }
            }
        }

        return text.ToString();
    }
}
