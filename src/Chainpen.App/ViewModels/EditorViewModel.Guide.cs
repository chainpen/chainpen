using System.ComponentModel;
using Chainpen.App.Localization;
using Chainpen.Core.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Chainpen.App.ViewModels;

/// <summary>The steps of the practice guide, in order.</summary>
public enum GuideStep
{
    Tick,
    Name,
    Sign,
    Export,
    Done,
}

/// <summary>
/// The practice guide (FR-1.8): on the practice form, one step at a time, it lights up the tool to choose and then the
/// place on the page to click. Each step is worked out from what's on the page, so it moves on when the person does
/// the thing, and Undo takes it back a step. The practice form is a scan, so the guide says why the boxes don't tick
/// themselves.
/// </summary>
public sealed partial class EditorViewModel
{
    // Where the practice form's first box, name line and signature line are, in points from the page's top left.
    // build/make-practice-form.py prints these when it makes the form.
    private static readonly PageRect s_practiceBox = new(70, 253, 12, 12);
    private static readonly PageRect s_practiceName = new(141, 359, 221, 26);
    private static readonly PageRect s_practiceSignature = new(142, 419, 221, 26);

    private const int GuideSteps = 4;

    private PageId? _guidePage;
    private bool _guideExported;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Guide))]
    public partial bool IsGuiding { get; private set; }

    /// <summary>The step the person is on, or null when there's no guide.</summary>
    public GuideStep? Guide
    {
        get
        {
            if (!IsGuiding)
            {
                return null;
            }

            if (!State.Items.Exists(item => item is MarkItem))
            {
                return GuideStep.Tick;
            }

            if (!State.Items.Exists(item => item is TextItem text && !string.IsNullOrWhiteSpace(text.Text) && text.Id != TypingItem))
            {
                return GuideStep.Name;
            }

            if (!State.Items.Exists(item => item is SignatureItem { Kind: SignatureKind.Signature }))
            {
                return GuideStep.Sign;
            }

            return _guideExported ? GuideStep.Done : GuideStep.Export;
        }
    }

    public string GuideProgress => Guide is GuideStep.Done or null ? Strings.Editor_PracticeDone : Text.Format(Strings.Editor_StepOfSteps, (int)Guide + 1, GuideSteps);

    public string GuideTitle => Guide switch
    {
        GuideStep.Tick => Strings.Editor_GuideTickABox,
        GuideStep.Name => Strings.Editor_GuideWriteYourName,
        GuideStep.Sign => Strings.Editor_GuideSignTheForm,
        GuideStep.Export => Strings.Common_ExportAFinalCopy,
        GuideStep.Done => Strings.Editor_GuideThatsIt,
        _ => string.Empty,
    };

    public string GuideText => Guide switch
    {
        GuideStep.Tick when Tool != EditorTool.Tick =>
            Strings.Editor_GuideTickChooseTool,
        GuideStep.Tick => Strings.Editor_GuideTickClickTheBox,
        GuideStep.Name when TypingItem is not null => Strings.Editor_GuideNameTypeYourName,
        GuideStep.Name when Tool != EditorTool.Text => Strings.Editor_GuideNameChooseText,
        GuideStep.Name => Strings.Editor_GuideNameClickTheLine,
        GuideStep.Sign when PlacingSignature is not null => Strings.Editor_GuideSignClickTheLine,
        GuideStep.Sign => Strings.Editor_GuideSignChooseSign,
        GuideStep.Export =>
            Strings.Editor_GuideExportChooseExport,
        GuideStep.Done =>
            Strings.Editor_GuideDone,
        _ => string.Empty,
    };

    /// <summary>What screen readers hear as the step changes: where they are, the step and what to do.</summary>
    public string GuideAnnouncement => IsGuiding ? Text.Format(Strings.Editor_GuideAnnouncement, GuideProgress, GuideTitle, GuideText) : string.Empty;

    public string GuideCloseLabel => Guide == GuideStep.Done ? Strings.Editor_CloseGuide : Strings.Editor_SkipGuide;

    // Which toolbar button the step wants, lit up until it's chosen; or, after a nudge, the tools for filling in.
    public bool GuideLightsTick => (Guide == GuideStep.Tick && Tool != EditorTool.Tick) || IsNudgingWithoutGuide;

    public bool GuideLightsText => (Guide == GuideStep.Name && Tool != EditorTool.Text && TypingItem is null) || IsNudgingWithoutGuide;

    public bool GuideLightsSign => (Guide == GuideStep.Sign && PlacingSignature is null) || IsNudgingWithoutGuide;

    public bool GuideLightsExport => Guide == GuideStep.Export;

    /// <summary>
    /// A click with Select on an empty part of the practice form: lit up Tick, Text and Sign until the next click or
    /// tool. There that click is usually someone trying to tick a printed box; on their own documents, after the
    /// practice, it's more often a click to clear the selection, so it isn't answered.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GuideLightsTick), nameof(GuideLightsText), nameof(GuideLightsSign))]
    public partial bool IsNudgingTools { get; private set; }

    private bool IsNudgingWithoutGuide => IsNudgingTools && !IsGuiding;

    private void NudgeTowardsTools()
    {
        if (!IsPracticeForm || IsViewOnly || HasFillableFields)
        {
            return;
        }

        Tell(Strings.Editor_ChooseAToolFirst);
        IsNudgingTools = true;
    }

    /// <summary>Whether this is the practice form, opened with its guide (even if the guide has since been closed).</summary>
    private bool IsPracticeForm => _guidePage is not null;

    /// <summary>The place on the practice page to click once the right tool is chosen, with a label for it.</summary>
    public (PageId Page, PageRect Area, string Label)? GuideTarget
    {
        get
        {
            if (_guidePage is not { } page || !State.Pages.Any(entry => entry.Id == page && entry.Rotation == Rotation.None))
            {
                return null;
            }

            return Guide switch
            {
                GuideStep.Tick when Tool == EditorTool.Tick => (page, s_practiceBox, Strings.Editor_ClickHere),
                GuideStep.Name when Tool == EditorTool.Text && TypingItem is null => (page, s_practiceName, Strings.Editor_ClickHere),
                GuideStep.Sign when PlacingSignature is not null => (page, s_practiceSignature, Strings.Editor_SignHere),
                _ => null,
            };
        }
    }

    /// <summary>Starts the guide on the practice form just opened.</summary>
    public void StartPracticeGuide()
    {
        _guidePage = State.Pages.IsEmpty ? null : State.Pages[0].Id;
        _guideExported = false;
        if (!IsGuiding)
        {
            PropertyChanged += OnGuideInputChanged;
        }

        IsGuiding = true;
        GuideChanged();
    }

    [RelayCommand]
    private void CloseGuide()
    {
        PropertyChanged -= OnGuideInputChanged;
        IsGuiding = false;
        GuideChanged();
    }

    /// <summary>Export was opened, so the last step is done whatever the person chose in the dialog.</summary>
    private void NoteGuideExport()
    {
        if (IsGuiding && Guide == GuideStep.Export)
        {
            _guideExported = true;
            GuideChanged();
        }
    }

    private void OnGuideInputChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(State) or nameof(Tool) or nameof(TypingItem) or nameof(PlacingSignature))
        {
            GuideChanged();
        }
    }

    private void GuideChanged()
    {
        OnPropertyChanged(nameof(Guide));
        OnPropertyChanged(nameof(GuideProgress));
        OnPropertyChanged(nameof(GuideTitle));
        OnPropertyChanged(nameof(GuideText));
        OnPropertyChanged(nameof(GuideCloseLabel));
        OnPropertyChanged(nameof(GuideAnnouncement));
        OnPropertyChanged(nameof(GuideLightsTick));
        OnPropertyChanged(nameof(GuideLightsText));
        OnPropertyChanged(nameof(GuideLightsSign));
        OnPropertyChanged(nameof(GuideLightsExport));
        OnPropertyChanged(nameof(GuideTarget));

        // The place to click may be below the fold (the signature line at the default zoom).
        if (GuideTarget is { } target && target != _guideShown)
        {
            int index = State.Pages.ToList().FindIndex(entry => entry.Id == target.Page);
            ScrollToAreaRequested?.Invoke(index, target.Area);
        }

        _guideShown = GuideTarget;
    }

    private (PageId Page, PageRect Area, string Label)? _guideShown;
}
