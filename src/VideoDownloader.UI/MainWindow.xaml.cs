using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private bool _suppressQueueSelectionSync;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        _viewModel.RestoreQueueSelection = RestoreDownloadQueueSelection;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
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
        var selected = list.SelectedItems.Cast<DownloadJobViewModel>().ToList();
        var primary = list.SelectedItem as DownloadJobViewModel ?? selected.LastOrDefault();
        _viewModel.SetSelectedDownloadJobs(selected, primary);
    }

    private void RestoreDownloadQueueSelection(IReadOnlyList<Guid> ids)
    {
        if (ids.Count == 0)
            return;

        _suppressQueueSelectionSync = true;
        try
        {
            DownloadQueueList.SelectedItems.Clear();
            DownloadJobViewModel? primary = null;
            foreach (var vm in _viewModel.DownloadJobs)
            {
                if (!ids.Contains(vm.Job.Id))
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
        // Re-sync before CanExecute is queried for menu items.
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
        if (sender is not ListBox list ||
            e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(list, source) is not ListBoxItem)
            return;

        PlaySelectedDownloadLikeDoubleClick();
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

    private void PlaySelectedDownloadLikeDoubleClick()
    {
        // Same entry as double-click: system default player via ViewModel.
        if (_viewModel.PlaySelectedDownloadCommand.CanExecute(null))
            _viewModel.PlaySelectedDownloadCommand.Execute(null);
        else
            _viewModel.OpenSelectedDownloadCommand.Execute(null);
    }

    private void OnDownloadQueuePreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list ||
            e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(list, source) is not ListBoxItem item)
            return;

        // Explorer-like: right-click unselected row → select only it; already selected → keep multi-select.
        if (!item.IsSelected)
        {
            list.SelectedItems.Clear();
            item.IsSelected = true;
        }

        item.Focus();
        SyncQueueSelectionFromList(list);
        _viewModel.NotifyQueueCommandsPublic();
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
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedTab))
            UpdateWebViewVisibility();
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

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isClosing)
            return;

        _isClosing = true;
        _timer.Stop();
        _viewModel.Tabs.CollectionChanged -= OnTabsChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _ = Task.Run(async () =>
        {
            try
            {
                await _viewModel.DisposeHostsAsync();
            }
            catch
            {
                // ignore shutdown errors
            }
        });
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
