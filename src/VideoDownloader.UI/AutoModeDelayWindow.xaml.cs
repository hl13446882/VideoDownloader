using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using VideoDownloader.UI.Localization;

namespace VideoDownloader.UI;

public partial class AutoModeDelayWindow : Window, INotifyPropertyChanged
{
    private string _minutesText;
    private string _errorText = string.Empty;

    public AutoModeDelayWindow(LocalizationService loc, int initialMinutes)
    {
        InitializeComponent();
        DataContext = this;
        Title = loc.T("auto.delayTitle");
        TitleText = Title;
        PromptText = loc.T("auto.delayPrompt");
        HintText = loc.T("auto.delayHint");
        OkText = loc.T("auto.ok");
        CancelText = loc.T("auto.cancel");
        InvalidText = loc.T("auto.delayInvalid");
        _minutesText = Math.Max(1, initialMinutes).ToString(CultureInfo.InvariantCulture);
        Loaded += (_, _) =>
        {
            MinutesBox.Focus();
            MinutesBox.SelectAll();
        };
    }

    public string TitleText { get; }
    public string PromptText { get; }
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

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var raw = (MinutesText ?? string.Empty).Trim();
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            minutes < 1)
        {
            ErrorText = InvalidText;
            MinutesBox.Focus();
            MinutesBox.SelectAll();
            return;
        }

        AcceptedMinutes = minutes;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
