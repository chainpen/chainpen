using Avalonia.Headless.XUnit;
using Chainpen.App.Platform;
using Chainpen.App.ViewModels;

namespace Chainpen.App.Tests;

/// <summary>FS-902: opt-in crash reports, kept locally and sent only by the person.</summary>
public sealed class CrashReportTests
{
    [Fact]
    public void A_report_names_the_failing_code_but_not_the_message_which_can_name_files()
    {
        Exception thrown;
        try
        {
            throw new InvalidOperationException("Couldn't read /Users/someone/Documents/tax-return.pdf", new FileNotFoundException("tax-return.pdf"));
        }
        catch (InvalidOperationException ex)
        {
            thrown = ex;
        }

        string report = CrashReports.Describe(thrown, new DateTimeOffset(2026, 11, 19, 9, 30, 0, TimeSpan.Zero));
        Assert.Contains("System.InvalidOperationException", report, StringComparison.Ordinal);
        Assert.Contains("Caused by System.IO.FileNotFoundException", report, StringComparison.Ordinal);
        Assert.Contains(nameof(A_report_names_the_failing_code_but_not_the_message_which_can_name_files), report, StringComparison.Ordinal);
        Assert.Contains("2026-11-19 09:30 UTC", report, StringComparison.Ordinal);
        Assert.DoesNotContain("tax-return", report, StringComparison.Ordinal);
        Assert.DoesNotContain("/Users/", report, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Reports_are_kept_only_when_chosen_and_open_as_a_prefilled_issue()
    {
        CrashReports.DeleteAll();
        try
        {
            AppSettings.Update(settings => settings with { SendCrashReports = null });
            CrashReports.Record(new InvalidOperationException("boom"));
            Assert.Empty(CrashReports.Pending());

            AppSettings.Update(settings => settings with { SendCrashReports = true });
            CrashReports.Record(new InvalidOperationException("boom"));
            Assert.Single(CrashReports.Pending());

            Uri link = Assert.IsType<Uri>(CrashReports.IssueLinkForNewest());
            Assert.StartsWith(CrashReports.IssueUrl + "?template=4-crash.yml&title=Crash%20report&report=", link.AbsoluteUri, StringComparison.Ordinal);
            Assert.Contains(Uri.EscapeDataString("System.InvalidOperationException"), link.AbsoluteUri, StringComparison.Ordinal);
            Assert.True(CrashReports.IssueLink(new string('x', 50_000)).AbsoluteUri.Length < 20_000);

            // The start screen offers it; taking the link clears it.
            var start = new StartViewModel(isFirstLaunch: false);
            Assert.True(start.HasCrashReport);
            Assert.NotNull(start.TakeCrashReportLink());
            Assert.False(start.HasCrashReport);
            Assert.Empty(CrashReports.Pending());
        }
        finally
        {
            CrashReports.DeleteAll();
            AppSettings.Update(settings => settings with { SendCrashReports = null });
        }
    }

    [AvaloniaFact]
    public void The_start_screen_asks_once_from_the_second_start()
    {
        AppSettings saved = AppSettings.Load();
        try
        {
            AppSettings.Update(settings => settings with { LaunchCount = 1, SendCrashReports = null });
            Assert.False(new StartViewModel(isFirstLaunch: true).AsksAboutCrashReports);

            AppSettings.Update(settings => settings with { LaunchCount = 2 });
            var start = new StartViewModel(isFirstLaunch: false);
            Assert.True(start.AsksAboutCrashReports);
            start.AnswerCrashReports(isYes: false);
            Assert.False(start.AsksAboutCrashReports);
            Assert.False(AppSettings.Load().SendCrashReports);
            Assert.False(new StartViewModel(isFirstLaunch: false).AsksAboutCrashReports);
        }
        finally
        {
            AppSettings.Update(_ => saved);
        }
    }
}
