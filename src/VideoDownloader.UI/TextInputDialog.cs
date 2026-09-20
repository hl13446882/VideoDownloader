using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace VideoDownloader.UI;

/// <summary>Minimal modal text prompt (folder import name, etc.).</summary>
internal sealed class TextInputDialog : Window
{
    private readonly TextBox _box;
    private bool _accepted;

    public TextInputDialog(string title, string prompt, string defaultValue)
    {
        Title = title;
        Width = 420;
        Height = 170;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        var root = new DockPanel { Margin = new Thickness(16) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        DockPanel.SetDock(buttons, Dock.Bottom);

        var ok = new Button
        {
            Content = "确定",
            Width = 80,
            Height = 28,
            IsDefault = true,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var cancel = new Button
        {
            Content = "取消",
            Width = 80,
            Height = 28,
            IsCancel = true
        };
        ok.Click += (_, _) =>
        {
            _accepted = true;
            DialogResult = true;
            Close();
        };
        cancel.Click += (_, _) =>
        {
            DialogResult = false;
            Close();
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = prompt,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });
        _box = new TextBox
        {
            Text = defaultValue,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _box.SelectAll();
        body.Children.Add(_box);

        root.Children.Add(buttons);
        root.Children.Add(body);
        Content = root;

        Loaded += (_, _) =>
        {
            _box.Focus();
            Keyboard.Focus(_box);
        };
    }

    public string? ResultText => _accepted ? _box.Text : null;
}
