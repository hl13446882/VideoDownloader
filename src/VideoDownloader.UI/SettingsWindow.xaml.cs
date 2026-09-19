using System.Windows;

namespace VideoDownloader.UI;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void OpenSubtitleSettings_Click(object sender, RoutedEventArgs e)
    {
        var window = new SubtitleSettingsWindow
        {
            Owner = this,
            DataContext = DataContext
        };
        window.ShowDialog();
    }
}
