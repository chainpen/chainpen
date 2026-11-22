using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Fillsign.App.Platform;
using Fillsign.App.ViewModels;
using Fillsign.App.Views;
using Fillsign.Core.Model;
using static Fillsign.App.Tests.UiTest;

namespace Fillsign.App.Tests;

/// <summary>The date format setting: the system's to start with, changeable in Settings, used by the Date tool.</summary>
public sealed class DateFormatTests
{
    [Theory]
    [InlineData("en-GB", DateFormatChoice.DayMonthYear)]
    [InlineData("en-AU", DateFormatChoice.DayMonthYear)]
    [InlineData("fr-FR", DateFormatChoice.DayMonthYear)]
    [InlineData("en-US", DateFormatChoice.MonthDayYear)]
    [InlineData("sv-SE", DateFormatChoice.YearMonthDay)]
    [InlineData("ja-JP", DateFormatChoice.YearMonthDaySlashes)]
    [InlineData("de-DE", DateFormatChoice.DayMonthYearDots)]
    [InlineData("", DateFormatChoice.MonthDayYear)]
    public void The_starting_format_follows_the_system_order_and_separator(string culture, DateFormatChoice expected) =>
        Assert.Equal(expected, DateFormats.FromCulture(CultureInfo.GetCultureInfo(culture)));

    [Fact]
    public void Dates_always_have_two_digit_days_and_months_and_a_four_digit_year()
    {
        var date = new DateTime(2026, 3, 7);
        Assert.Equal("07/03/2026", DateFormats.Format(date, DateFormatChoice.DayMonthYear));
        Assert.Equal("03/07/2026", DateFormats.Format(date, DateFormatChoice.MonthDayYear));
        Assert.Equal("2026-03-07", DateFormats.Format(date, DateFormatChoice.YearMonthDay));
        Assert.Equal("07.03.2026", DateFormats.Format(date, DateFormatChoice.DayMonthYearDots));
        Assert.Equal("2026/03/07", DateFormats.Format(date, DateFormatChoice.YearMonthDaySlashes));
    }

    [AvaloniaFact]
    public async Task A_format_chosen_in_Settings_by_keyboard_is_what_the_Date_tool_writes()
    {
        AppSettings.Update(settings => settings with { DateFormat = DateFormatChoice.DayMonthYear });
        var settingsWindow = new SettingsWindow();
        settingsWindow.Show();
        try
        {
            var choices = settingsWindow.FindControl<StackPanel>("DateFormatChoices")!.Children.OfType<RadioButton>().ToList();
            Assert.Equal(["DD/MM/YYYY", "MM/DD/YYYY", "YYYY-MM-DD", "YYYY/MM/DD", "DD.MM.YYYY"], choices.Select(choice => (string)choice.Content!));
            Assert.True(choices[0].IsChecked);

            RadioButton iso = choices[2];
            Assert.True(iso.Focus(NavigationMethod.Tab));
            await Pump(() => iso.IsFocused);
            settingsWindow.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            settingsWindow.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            await Pump(() => AppSettings.Load().DateFormat == DateFormatChoice.YearMonthDay);
            Assert.Equal($"Today is {DateTime.Today:yyyy-MM-dd} in this format.", settingsWindow.ViewModel.DateExample);
        }
        finally
        {
            settingsWindow.Close();
        }

        var window = new MainWindow(isFirstLaunch: false, []) { Width = 1280, Height = 860 };
        window.Show();
        try
        {
            await window.OpenPathsAsync([TestFiles.Sample("sample-form.pdf")]);
            await Pump(() => window.DataContext is MainWindowViewModel { Editor: { IsLoading: false } });
            EditorViewModel editor = Editor(window);
            editor.SelectToolCommand.Execute(EditorTool.Date);
            Assert.Contains("(YYYY-MM-DD)", editor.ToolHint, StringComparison.Ordinal);
            editor.PointerPressed(0, new PagePoint(200, 150), isShift: false, isCommand: false, clickCount: 1, tolerance: 4);
            editor.PointerReleased(new PagePoint(200, 150));
            TextItem date = Assert.Single(editor.State.Items.OfType<TextItem>());
            Assert.Equal(DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), date.Text);
        }
        finally
        {
            window.CloseWithoutAsking();
            AppSettings.Update(settings => settings with { DateFormat = null });
        }
    }
}
