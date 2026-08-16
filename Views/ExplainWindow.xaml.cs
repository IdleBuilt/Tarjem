using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Serilog;
using Tarjem.Models;
using Tarjem.Services;

namespace Tarjem.Views;

/// <summary>
/// The Alt+E result: what a name actually <em>is</em>.
///
/// A window of its own rather than a reskin of the word popup, because it is answering a different
/// question with a different shape of answer. The word popup is built around a dictionary entry -
/// phonetics, part of speech, a CEFR badge, a highlighted word inside a translated sentence - and
/// none of that applies to "NVIDIA is an American technology company". Squeezing an encyclopedia
/// answer into it meant relabelling headings and hiding half the controls, which is how a card
/// ends up looking like a form with most of its fields blank.
/// </summary>
public partial class ExplainWindow : Window
{
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int GWL_EXSTYLE = -20;
    private const int AnimationMs = 170;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction EaseIn = new CubicEase { EasingMode = EasingMode.EaseIn };

    private bool _isClosing;
    private string? _sourceUrl;

    /// <summary>Raised with the text put on the clipboard, so a clipboard watcher can tell our own
    /// copy apart from the user copying something new.</summary>
    public event EventHandler<string>? TextCopied;

    public ExplainWindow()
    {
        InitializeComponent();
        ApplyTheme();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE);
    }

    private void ApplyTheme()
    {
        var dark = ThemeService.IsDark;

        RootCard.Background = new SolidColorBrush(dark ? Color.FromRgb(0x1E, 0x1E, 0x2E) : Color.FromRgb(0xF7, 0xF7, 0xF9));
        RootCard.BorderBrush = new SolidColorBrush(dark
            ? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x14, 0x00, 0x00, 0x00));

        var primary = new SolidColorBrush(dark ? Colors.White : Color.FromRgb(0x1A, 0x1A, 0x1A));
        var body = new SolidColorBrush(dark ? Color.FromRgb(0xDD, 0xDD, 0xDD) : Color.FromRgb(0x33, 0x33, 0x33));
        var muted = new SolidColorBrush(dark ? Color.FromRgb(0x88, 0x88, 0xAA) : Color.FromRgb(0x77, 0x77, 0x88));
        var chrome = new SolidColorBrush(dark ? Color.FromRgb(0x2A, 0x2A, 0x3A) : Color.FromRgb(0xE8, 0xE8, 0xEE));

        TitleText.Foreground = primary;
        SummaryText.Foreground = body;
        TranslatedSummaryText.Foreground = primary;
        KindLabel.Foreground = muted;
        SourceChip.Foreground = muted;
        LoadingText.Foreground = muted;
        TranslationDivider.Background = new SolidColorBrush(dark ? Color.FromRgb(0x3A, 0x3A, 0x4A) : Color.FromRgb(0xE0, 0xE0, 0xE6));

        foreach (var button in new[] { CopyButton, SourceButton })
        {
            button.Background = chrome;
            button.Foreground = body;
        }
    }

    /// <summary>Shows the card in its loading state, so the window appears immediately on the
    /// keypress instead of after the network round-trip.</summary>
    public void ShowLoading(string word)
    {
        TitleText.Text = word;
        LoadingText.Visibility = Visibility.Visible;
        SummaryText.Visibility = Visibility.Collapsed;
        ActionRow.Visibility = Visibility.Collapsed;
        DescriptorText.Visibility = Visibility.Collapsed;
        TranslationDivider.Visibility = Visibility.Collapsed;
        TranslatedSummaryText.Visibility = Visibility.Collapsed;
        SourceChip.Text = "";
    }

    public void SetResult(TranslationResult result)
    {
        LoadingText.Visibility = Visibility.Collapsed;
        ActionRow.Visibility = Visibility.Visible;

        TitleText.Text = result.OriginalWord;

        var summary = result.Meanings.FirstOrDefault() ?? "";
        SummaryText.Text = summary;
        SummaryText.Visibility = string.IsNullOrWhiteSpace(summary) ? Visibility.Collapsed : Visibility.Visible;
        SummaryText.FlowDirection = FlowFor(result.SourceLanguage);
        SummaryText.FontFamily = UiFonts.For(TranslationService.IsRtlLanguage(result.SourceLanguage));

        // The short descriptor ("American technology company") sits under the name, where it reads
        // as an appositive rather than as part of the summary.
        DescriptorText.Text = result.PartOfSpeech;
        DescriptorText.Visibility = string.IsNullOrWhiteSpace(result.PartOfSpeech)
            ? Visibility.Collapsed
            : Visibility.Visible;

        var translated = result.TranslatedSentence;
        var showTranslation = !string.IsNullOrWhiteSpace(translated) && translated != summary;

        TranslationDivider.Visibility = showTranslation ? Visibility.Visible : Visibility.Collapsed;
        TranslatedSummaryText.Visibility = showTranslation ? Visibility.Visible : Visibility.Collapsed;

        if (showTranslation)
        {
            var isRtl = TranslationService.IsRtlLanguage(result.TargetLanguage);
            TranslatedSummaryText.Text = translated;
            TranslatedSummaryText.FlowDirection = isRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            TranslatedSummaryText.FontFamily = UiFonts.For(isRtl);
            TranslatedSummaryText.FontSize = UiFonts.SizeFor(13.5, isRtl);
        }

        SourceChip.Text = result.DefinitionProvider switch
        {
            "wikipedia" => "Wikipedia",
            "wikidata" => "Wikidata",
            _ => "",
        };

        _sourceUrl = result.SourceUrl;
        SourceButton.Visibility = string.IsNullOrWhiteSpace(_sourceUrl) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static FlowDirection FlowFor(string languageCode) =>
        TranslationService.IsRtlLanguage(languageCode) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    // ── Animation ──

    public void AnimateIn()
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(AnimationMs));

        RootCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = EaseOut });
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.95, 1, duration) { EasingFunction = EaseOut });
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.95, 1, duration) { EasingFunction = EaseOut });
        RootTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, duration) { EasingFunction = EaseOut });
    }

    /// <summary>Crossfades in new content once the lookup finishes, so the card doesn't visibly
    /// snap from "Looking it up…" to a paragraph.</summary>
    public void AnimateContentSwap(Action applyContent)
    {
        var fadeOut = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(70))) { EasingFunction = EaseIn };
        fadeOut.Completed += (_, _) =>
        {
            applyContent();
            RootCard.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(140))) { EasingFunction = EaseOut });
        };
        RootCard.BeginAnimation(OpacityProperty, fadeOut);
    }

    public void CloseAnimated()
    {
        if (_isClosing) return;
        _isClosing = true;

        var duration = new Duration(TimeSpan.FromMilliseconds(AnimationMs));
        var fadeOut = new DoubleAnimation(RootCard.Opacity, 0, duration) { EasingFunction = EaseIn };
        fadeOut.Completed += (_, _) => Close();

        RootCard.BeginAnimation(OpacityProperty, fadeOut);
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(RootScale.ScaleX, 0.97, duration) { EasingFunction = EaseIn });
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(RootScale.ScaleY, 0.97, duration) { EasingFunction = EaseIn });
    }

    // ── Actions ──

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var text = TranslatedSummaryText.Visibility == Visibility.Visible
            ? TranslatedSummaryText.Text
            : SummaryText.Text;

        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            TextCopied?.Invoke(this, text);
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not copy the explanation");
        }
    }

    private void Source_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_sourceUrl)) return;

        try
        {
            Process.Start(new ProcessStartInfo(_sourceUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open {Url}", _sourceUrl);
        }
    }
}
