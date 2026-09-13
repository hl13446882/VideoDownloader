using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using VideoDownloader.UI.ViewModels;

namespace VideoDownloader.UI;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<Guid, WebView2> _webViews = new();
    private bool _isClosing;
    private bool _allowClose;
    private bool _suppressQueueSelectionSync;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        _viewModel.RestoreQueueSelection = RestoreDownloadQueueSelection;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _timer.Tick += (_, _) => _viewModel.TickDownloads();
        _timer.Start();

        Loaded += OnLoadedAsync;
        Closing += OnClosing;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnDownloadQueueSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressQueueSelectionSync || sender is not ListBox list)
            return;

        SyncQueueSelectionFromList(list);
    }

    private void SyncQueueSelectionFromList(ListBox list)
    {
        var selected = list.SelectedItems.OfType<DownloadJobViewModel>().ToList();
        var primary = list.SelectedItem as DownloadJobViewModel
                      ?? selected.LastOrDefault();
        _viewModel.SetSelectedDownloadJobs(selected, primary);
    }

    private void RestoreDownloadQueueSelection(IReadOnlyList<Guid> ids)
    {
        _suppressQueueSelectionSync = true;
        try
        {
            DownloadQueueList.SelectedItems.Clear();
            if (ids.Count == 0)
            {
                DownloadQueueList.SelectedItem = null;
                return;
            }

            DownloadJobViewModel? primary = null;
            foreach (var row in _viewModel.QueueRows)
            {
                if (row is not DownloadJobViewModel vm || !ids.Contains(vm.Job.Id))
                    continue;
                DownloadQueueList.SelectedItems.Add(vm);
                primary = vm;
            }

            if (primary is not null)
                DownloadQueueList.SelectedItem = primary;
        }
        finally
        {
            _suppressQueueSelectionSync = false;
        }

        SyncQueueSelectionFromList(DownloadQueueList);
    }

    private void OnDownloadQueueContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (QueueRowFromSource(e.OriginalSource) is not DownloadJobViewModel)
        {
            e.Handled = true;
            return;
        }

        SyncQueueSelectionFromList(DownloadQueueList);
        _viewModel.NotifyQueueCommandsPublic();
    }

    private void OnDownloadQueueContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
            menu.DataContext = _viewModel;
        _viewModel.NotifyQueueCommandsPublic();
    }

    private void OnDownloadQueueDoubleClick(object sender, MouseButtonEventArgs e)
    {
        switch (QueueRowFromSource(e.OriginalSource))
        {
            case QueueGroupViewModel:
                e.Handled = true;
                return;
            case DownloadJobViewModel job:
                _viewModel.ActivateQueueJob(job);
                e.Handled = true;
                return;
        }
    }

    private void OnDownloadQueuePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (QueueRowFromSource(e.OriginalSource) is QueueGroupViewModel group)
        {
            if (e.ClickCount >= 2)
                _viewModel.ToggleQueueGroup(group.Domain);
            e.Handled = true;
            return;
        }

        if (QueueRowFromSource(e.OriginalSource) is not null)
            return;

        if (FindAncestor<ScrollBar>(e.OriginalSource as DependencyObject) is not null)
            return;

        ClearQueueListSelection();
    }

    private void OnDownloadQueuePreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list)
            return;

        if (QueueRowFromSource(e.OriginalSource) is not DownloadJobViewModel job)
        {
            ClearQueueListSelection();
            e.Handled = true;
            return;
        }

        SelectOnlyQueueRow(list, job);
        _viewModel.NotifyQueueCommandsPublic();
    }

    private void OnDownloadQueuePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.F2 or Key.Delete or Key.Enter or Key.Return)
            DownloadQueueList.Focus();
    }

    private void OnWindowPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return;
        if (FindAncestor(source, DownloadQueueList) is not null)
            return;
        if (FindAncestor(source, QueueActionBar) is not null)
            return;
        if (FindAncestor<ContextMenu>(source) is not null || FindAncestor<MenuItem>(source) is not null)
            return;

        ClearQueueListSelection();
    }

    private void SelectOnlyQueueRow(ListBox list, DownloadJobViewModel job)
    {
        _suppressQueueSelectionSync = true;
        try
        {
            list.SelectedItems.Clear();
            list.SelectedItems.Add(job);
            list.SelectedItem = job;
        }
        finally
        {
            _suppressQueueSelectionSync = false;
        }

        _viewModel.SetSelectedDownloadJobs([job], job);
        list.Focus();
    }

    private void ClearQueueListSelection()
    {
        if (DownloadQueueList.SelectedItems.Count == 0 && _viewModel.SelectedDownloadJobs.Count == 0)
            return;

        _suppressQueueSelectionSync = true;
        try
        {
            DownloadQueueList.SelectedItems.Clear();
            DownloadQueueList.SelectedItem = null;
        }
        finally
        {
            _suppressQueueSelectionSync = false;
        }

        _viewModel.ClearQueueSelection();
    }

    private object? QueueRowFromSource(object source)
    {
        if (source is not DependencyObject dep)
            return null;
        return ItemsControl.ContainerFromElement(DownloadQueueList, dep) is ListBoxItem item
            ? item.DataContext
            : null;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
                return match;
            current = current is Visual
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    private static DependencyObject? FindAncestor(DependencyObject? current, DependencyObject ancestor)
    {
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
                return current;
            current = current is Visual
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    private void OnRenameTextBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box)
            return;

        box.IsVisibleChanged -= OnRenameTextBoxIsVisibleChanged;
        box.IsVisibleChanged += OnRenameTextBoxIsVisibleChanged;
        FocusRenameBox(box);
    }

    private void OnRenameTextBoxIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox box)
            FocusRenameBox(box);
    }

    private static void FocusRenameBox(TextBox box)
    {
        if (!box.IsVisible)
            return;
        box.Focus();
        box.SelectAll();
    }

    private async void OnRenameTextBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: DownloadJobViewModel vm })
            return;
        await _viewModel.CommitRenameAsync(vm);
    }

    private async void OnRenameTextBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: DownloadJobViewModel vm })
            return;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await _viewModel.CommitRenameAsync(vm);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _viewModel.CancelRename(vm);
        }
    }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedAsync;

        try
        {
            _viewModel.Tabs.CollectionChanged += OnTabsChanged;
            await _viewModel.InitializeAsync();
            foreach (var tab in _viewModel.Tabs.ToArray())
                await EnsureWebViewAsync(tab);
            UpdateWebViewVisibility();
        }
        catch (Exception ex)
        {
            _viewModel.SetStatusKey("status.webviewInitFailed", ex.Message);
        }
    }

    private async void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (BrowserTabViewModel tab in e.NewItems)
                await EnsureWebViewAsync(tab);
        }

        if (e.OldItems is not null)
        {
            foreach (BrowserTabViewModel tab in e.OldItems)
                RemoveWebView(tab);
        }

        UpdateWebViewVisibility();
        _viewModel.RecalculateTabWidths();
    }

    private void BrowserPane_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0)
            _viewModel.NotifyBrowserPaneWidth(e.NewSize.Width);
    }

    private WindowState _savedWindowState = WindowState.Normal;
    private WindowStyle _savedWindowStyle = WindowStyle.SingleBorderWindow;
    private ResizeMode _savedResizeMode = ResizeMode.CanResize;
    private double _savedMinWidth;
    private double _savedMinHeight;
    private bool _appFullscreenApplied;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedTab))
            UpdateWebViewVisibility();
        else if (e.PropertyName == nameof(MainViewModel.IsAppFullscreen))
            ApplyAppFullscreen(_viewModel.IsAppFullscreen);
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _viewModel.IsAppFullscreen)
        {
            _viewModel.SetAppFullscreen(false);
            e.Handled = true;
        }
    }

    private void ApplyAppFullscreen(bool active)
    {
        if (active == _appFullscreenApplied)
            return;

        if (active)
        {
            _savedWindowState = WindowState;
            _savedWindowStyle = WindowStyle;
            _savedResizeMode = ResizeMode;
            _savedMinWidth = MinWidth;
            _savedMinHeight = MinHeight;
            MinWidth = 0;
            MinHeight = 0;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            // Ensure a style change takes effect before maximizing borderless.
            if (WindowState == WindowState.Maximized)
                WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
            _appFullscreenApplied = true;
        }
        else
        {
            WindowStyle = _savedWindowStyle;
            ResizeMode = _savedResizeMode;
            MinWidth = _savedMinWidth > 0 ? _savedMinWidth : 1000;
            MinHeight = _savedMinHeight > 0 ? _savedMinHeight : 700;
            WindowState = _savedWindowState == WindowState.Minimized
                ? WindowState.Normal
                : _savedWindowState;
            _appFullscreenApplied = false;
        }
    }

    private async Task EnsureWebViewAsync(BrowserTabViewModel tab)
    {
        if (_webViews.ContainsKey(tab.Id))
            return;

        var webView = new WebView2
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _webViews[tab.Id] = webView;
        WebViewHostPanel.Children.Add(webView);
        await _viewModel.AttachWebViewAsync(tab, webView);
        UpdateWebViewVisibility();
    }

    private void RemoveWebView(BrowserTabViewModel tab)
    {
        if (!_webViews.TryGetValue(tab.Id, out var webView))
            return;

        _webViews.Remove(tab.Id);
        WebViewHostPanel.Children.Remove(webView);
        try
        {
            webView.Dispose();
        }
        catch
        {
            // ignore
        }
    }

    private void UpdateWebViewVisibility()
    {
        var selectedId = _viewModel.SelectedTab?.Id;
        foreach (var (id, webView) in _webViews)
            webView.Visibility = id == selectedId ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        // Second pass after Shutdown(): allow the window to destroy.
        if (_allowClose)
            return;

        // Keep the window alive until WebView2/hosts are disposed on the UI thread.
        // Fire-and-forget Task.Run dispose left a living process (mutex held, no HWND).
        e.Cancel = true;
        if (_isClosing)
            return;

        _isClosing = true;
        _timer.Stop();
        _viewModel.Tabs.CollectionChanged -= OnTabsChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        try
        {
            foreach (var tab in _viewModel.Tabs.ToArray())
                RemoveWebView(tab);
            await _viewModel.DisposeHostsAsync();
        }
        catch
        {
            // ignore shutdown errors
        }
        finally
        {
            _allowClose = true;
            if (Application.Current is { } app)
                app.Shutdown(0);
            else
                Close();
        }
    }

    private void AddressBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        AddressBox.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();

        if (_viewModel.NavigateCommand.CanExecute(null))
        {
            _viewModel.NavigateCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void AddressBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AddressBox.SelectedItem is not AddressPreset preset)
            return;

        _viewModel.AddressBar = preset.Url;
        // Clear selection so typing later is not stuck on the preset object.
        AddressBox.SelectedItem = null;
        AddressBox.Text = preset.Url;

        if (_viewModel.NavigateCommand.CanExecute(null))
            _viewModel.NavigateCommand.Execute(null);
    }
}
