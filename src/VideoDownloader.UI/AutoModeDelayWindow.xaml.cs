using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using VideoDownloader.UI.Localization;

namespace VideoDownloader.UI;

public partial class AutoModeDelayWindow : Window, INotifyPropertyChanged
{
    private string _minutesText;
    private string _countText;
    private string _errorText = string.Empty;

    public AutoModeDelayWindow(LocalizationService loc, int initialMinutes, int initialCount)
    {
        InitializeComponent();
        DataContext = this;
        Title = loc.T("auto.delayTitle");
        TitleText = Title;
        PromptText = loc.T("auto.delayPrompt");
        MinutesLabel = loc.T("auto.delayMinutesLabel");
        CountLabel = loc.T("auto.delayCountLabel");
        HintText = loc.T("auto.delayHint");
        OkText = loc.T("auto.ok");
        CancelText = loc.T("auto.cancel");
        InvalidText = loc.T("auto.delayInvalid");
        _minutesText = Math.Max(0, initialMinutes).ToString(CultureInfo.InvariantCulture);
        _countText = Math.Max(0, initialCount).ToString(CultureInfo.InvariantCulture);
        Loaded += (_, _) =>
        {
            MinutesBox.Focus();
            MinutesBox.SelectAll();
        };
    }

    public string TitleText { get; }
    public string PromptText { get; }
    public string MinutesLabel { get; }
    public string CountLabel { get; }
    public string HintText { get; }
    public string OkText { get; }
    public string CancelText { get; }
    private string InvalidText { get; }

    public string MinutesText
    {
        get => _minutesText;
        set
        {
            if (_minutesText == value)
                return;
            _minutesText = value;
            OnPropertyChanged();
            if (!string.IsNullOrEmpty(ErrorText))
                ErrorText = string.Empty;
        }
    }

    public string CountText
    {
        get => _countText;
        set
        {
            if (_countText == value)
                return;
            _countText = value;
            OnPropertyChanged();
            if (!string.IsNullOrEmpty(ErrorText))
                ErrorText = string.Empty;
        }
    }

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (_errorText == value)
                return;
            _errorText = value;
            OnPropertyChanged();
        }
    }

    public int AcceptedMinutes { get; private set; }
    public int AcceptedCount { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!TryParseNonNegative(MinutesText, out var minutes))
        {
            ErrorText = InvalidText;
            MinutesBox.Focus();
            MinutesBox.SelectAll();
            return;
        }

        if (!TryParseNonNegative(CountText, out var count))
        {
            ErrorText = InvalidText;
            CountBox.Focus();
            CountBox.SelectAll();
            return;
        }

        AcceptedMinutes = minutes;
        AcceptedCount = count;
        DialogResult = true;
    }

    private static bool TryParseNonNegative(string? raw, out int value)
    {
        value = 0;
        var text = (raw ?? string.Empty).Trim();
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) &&
               value >= 0;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
