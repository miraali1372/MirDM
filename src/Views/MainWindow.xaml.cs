using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Forms;
using System.Windows.Threading;
using MirDM.Core;
using MirDM.Models;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace MirDM.Views;

public partial class MainWindow : Window
{
    private readonly DownloadManager _manager = DownloadManager.Instance;
    private readonly DispatcherTimer _uiTimer;
    private NotifyIcon? _trayIcon;
    private string _lastClipboardText = "";

    public MainWindow()
    {
        InitializeComponent();

        // Bind DataGrid to DownloadManager collection
        DownloadsDataGrid.ItemsSource = _manager.Downloads;

        // UI Refresh Timer for high-FPS graph and heatmap
        _uiTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _uiTimer.Tick += UiTimer_Tick;
        _uiTimer.Start();

        // Setup System Tray
        SetupSystemTray();

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await _manager.InitializeAsync();
        UpdateToolbarButtons();
        UpdateStatusBar();
    }

    private void SetupSystemTray()
    {
        try
        {
            var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/icon.ico"))?.Stream;
            System.Drawing.Icon? appIcon = null;

            if (iconStream != null)
            {
                appIcon = new System.Drawing.Icon(iconStream);
            }
            else if (File.Exists("icon.ico"))
            {
                appIcon = new System.Drawing.Icon("icon.ico");
            }
            else
            {
                appIcon = System.Drawing.SystemIcons.Application;
            }

            _trayIcon = new NotifyIcon
            {
                Icon = appIcon,
                Text = "MirDM — Ultra-Speed Download Manager",
                Visible = true
            };

            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("⚡ Show MirDM", null, (s, e) => RestoreFromTray());
            contextMenu.Items.Add("➕ Add Download...", null, (s, e) => AddDownload_Click(s!, null!));
            contextMenu.Items.Add("📋 Add Batch...", null, (s, e) => AddBatch_Click(s!, null!));
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add("⏸ Pause All", null, async (s, e) => await _manager.PauseAllAsync());
            contextMenu.Items.Add("▶ Resume All", null, async (s, e) => await _manager.ResumeAllAsync());
            contextMenu.Items.Add("🧹 Clear Completed", null, async (s, e) => await _manager.ClearCompletedAsync());
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add("✕ Exit", null, (s, e) => ForceExit());

            _trayIcon.ContextMenuStrip = contextMenu;
            _trayIcon.DoubleClick += (s, e) => RestoreFromTray();
        }
        catch
        {
            // Tray icon failure should not block main application
        }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Focus();
    }

    private void ForceExit()
    {
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        Application.Current.Shutdown();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_manager.Config.MinimizeToTray && _trayIcon != null)
        {
            e.Cancel = true;
            Hide();
        }
        else
        {
            ForceExit();
        }
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            MaximizeGlyph.Text = "🗗";
        }
        else
        {
            MaximizeGlyph.Text = "🗖";
        }
    }

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        UpdateStatusBar();
        UpdateToolbarButtons();

        var selected = DownloadsDataGrid.SelectedItem as DownloadItem;
        if (selected != null)
        {
            SelectedDownloadLabel.Text = $"{selected.FileName} ({selected.ProgressPercent:0.0}%)";
            HeatmapControl.DownloadItem = selected;
            HeatmapControl.RequestRedraw();
        }
        else
        {
            SelectedDownloadLabel.Text = "No download selected";
            HeatmapControl.DownloadItem = null;
            HeatmapControl.RequestRedraw();
        }

        // Plot real-time aggregate speed on the SpeedGraph
        double totalSpeedBps = _manager.TotalSpeedBps;
        SpeedGraph.AddSample(totalSpeedBps);

        // Check empty state
        EmptyStatePanel.Visibility = _manager.Downloads.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Clipboard auto-capture check if window is active
        if (IsActive && _manager.Config.ClipboardMonitoring)
        {
            CheckClipboardForUrl();
        }
    }

    private void CheckClipboardForUrl()
    {
        try
        {
            if (System.Windows.Clipboard.ContainsText())
            {
                string text = System.Windows.Clipboard.GetText().Trim();
                if (text != _lastClipboardText &&
                    (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                {
                    _lastClipboardText = text;
                    // Proactively prompt user if appropriate
                }
            }
        }
        catch { }
    }

    private void UpdateStatusBar()
    {
        int active = _manager.ActiveCount;
        int completed = _manager.CompletedCount;
        double speed = _manager.TotalSpeedBps;

        StatusBarActiveText.Text = $"{active} Active";
        StatusBarCompletedText.Text = $"• {completed} Completed";
        StatusBarSpeedText.Text = speed > 0 ? $"{DownloadItem.FormatBytes((long)speed)}/s" : "0 B/s";
    }

    private void UpdateToolbarButtons()
    {
        int totalDownloads = _manager.Downloads.Count;
        ClearListButton.IsEnabled = totalDownloads > 0;

        int selectedCount = DownloadsDataGrid.SelectedItems.Count;
        if (selectedCount == 0)
        {
            ResumeButton.IsEnabled = false;
            PauseButton.IsEnabled = false;
            RemoveButton.IsEnabled = false;
            OpenFolderButton.IsEnabled = false;
        }
        else if (selectedCount == 1)
        {
            var item = DownloadsDataGrid.SelectedItem as DownloadItem;
            ResumeButton.IsEnabled = item?.Status is DownloadStatus.Paused or DownloadStatus.Failed or DownloadStatus.Queued;
            PauseButton.IsEnabled = item?.Status is DownloadStatus.Downloading or DownloadStatus.Initializing;
            RemoveButton.IsEnabled = true;
            OpenFolderButton.IsEnabled = !string.IsNullOrEmpty(item?.DestinationPath);
        }
        else
        {
            var selectedList = DownloadsDataGrid.SelectedItems.Cast<DownloadItem>().ToList();
            ResumeButton.IsEnabled = selectedList.Any(i => i.Status is DownloadStatus.Paused or DownloadStatus.Failed or DownloadStatus.Queued);
            PauseButton.IsEnabled = selectedList.Any(i => i.Status is DownloadStatus.Downloading or DownloadStatus.Initializing);
            RemoveButton.IsEnabled = true;
            OpenFolderButton.IsEnabled = false;
        }
    }

    // Titlebar Controls
    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    // Toolbar Actions
    private void AddDownload_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddDownloadWindow { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            DownloadsDataGrid.SelectedIndex = 0;
        }
    }

    private void AddBatch_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new BatchDownloadWindow { Owner = this };
        if (dialog.ShowDialog() == true && dialog.AddedCount > 0)
        {
            DownloadsDataGrid.SelectedIndex = 0;
        }
    }

    private async void ClearList_Click(object sender, RoutedEventArgs e)
    {
        int total = _manager.Downloads.Count;
        if (total == 0) return;

        int completed = _manager.CompletedCount;
        int active = _manager.ActiveCount;

        string prompt = $"Are you sure you want to clear the download list ({total} items)?\n\n" +
                       $"• Physical files on disk will NOT be deleted (فایل‌ها روی دیسک باقی می‌مانند).\n" +
                       (active > 0 ? $"• {active} active download(s) will be stopped cleanly.\n" : "") +
                       $"• Session cache and task records will be cleared.\n\n" +
                       $"Proceed with clearing list?";

        var res = MessageBox.Show(
            prompt,
            "Clear Download List",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (res == MessageBoxResult.Yes)
        {
            await _manager.ClearAllAsync();
        }
    }

    private async void Resume_Click(object sender, RoutedEventArgs e)
    {
        var selectedList = DownloadsDataGrid.SelectedItems.Cast<DownloadItem>().ToList();
        if (selectedList.Count == 1)
        {
            await _manager.ResumeDownloadAsync(selectedList[0]);
        }
        else if (selectedList.Count > 1)
        {
            foreach (var item in selectedList)
            {
                if (item.Status is DownloadStatus.Paused or DownloadStatus.Failed or DownloadStatus.Queued)
                {
                    await _manager.ResumeDownloadAsync(item);
                }
            }
        }
    }

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        var selectedList = DownloadsDataGrid.SelectedItems.Cast<DownloadItem>().ToList();
        if (selectedList.Count == 1)
        {
            await _manager.PauseDownloadAsync(selectedList[0]);
        }
        else if (selectedList.Count > 1)
        {
            foreach (var item in selectedList)
            {
                if (item.Status is DownloadStatus.Downloading or DownloadStatus.Initializing)
                {
                    await _manager.PauseDownloadAsync(item);
                }
            }
        }
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        var selectedList = DownloadsDataGrid.SelectedItems.Cast<DownloadItem>().ToList();
        if (selectedList.Count == 0) return;

        if (selectedList.Count == 1)
        {
            var item = selectedList[0];
            var res = MessageBox.Show(
                $"Remove download '{item.FileName}' from list?\n(File on disk will NOT be deleted)",
                "Remove Download",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (res == MessageBoxResult.Yes)
            {
                await _manager.RemoveDownloadAsync(item, deleteFile: false);
            }
        }
        else
        {
            var res = MessageBox.Show(
                $"Remove {selectedList.Count} selected downloads from list?\n(Files on disk will NOT be deleted)",
                "Remove Selected Downloads",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (res == MessageBoxResult.Yes)
            {
                await _manager.RemoveMultipleAsync(selectedList, deleteFile: false);
            }
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (DownloadsDataGrid.SelectedItem is DownloadItem item && !string.IsNullOrEmpty(item.DestinationPath))
        {
            OpenInExplorer(item.DestinationPath);
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var settingsWin = new SettingsWindow { Owner = this };
        settingsWin.ShowDialog();
    }

    // Search Box Filter
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string query = SearchBox.Text.Trim();
        var view = CollectionViewSource.GetDefaultView(DownloadsDataGrid.ItemsSource);
        if (view == null) return;

        if (string.IsNullOrEmpty(query))
        {
            view.Filter = null;
        }
        else
        {
            view.Filter = obj =>
            {
                if (obj is DownloadItem item)
                {
                    return item.FileName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                           item.Url.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                           item.StatusDisplay.Contains(query, StringComparison.OrdinalIgnoreCase);
                }
                return false;
            };
        }
    }

    private void DownloadsDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateToolbarButtons();
        var selected = DownloadsDataGrid.SelectedItem as DownloadItem;
        HeatmapControl.DownloadItem = selected;
        HeatmapControl.RequestRedraw();
    }

    private async void DownloadsDataGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DownloadsDataGrid.SelectedItem is DownloadItem item)
        {
            if (item.Status == DownloadStatus.Completed && File.Exists(item.DestinationPath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(item.DestinationPath) { UseShellExecute = true });
                }
                catch { }
            }
            else if (item.Status == DownloadStatus.Paused || item.Status == DownloadStatus.Failed)
            {
                await _manager.ResumeDownloadAsync(item);
            }
            else if (item.Status == DownloadStatus.Downloading)
            {
                await _manager.PauseDownloadAsync(item);
            }
        }
    }

    // Context Menu Handlers
    private async void MenuResume_Click(object sender, RoutedEventArgs e)
    {
        if (DownloadsDataGrid.SelectedItem is DownloadItem item)
        {
            await _manager.ResumeDownloadAsync(item);
        }
    }

    private async void MenuPause_Click(object sender, RoutedEventArgs e)
    {
        if (DownloadsDataGrid.SelectedItem is DownloadItem item)
        {
            await _manager.PauseDownloadAsync(item);
        }
    }

    private void MenuOpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (DownloadsDataGrid.SelectedItem is DownloadItem item && File.Exists(item.DestinationPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo(item.DestinationPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open file: {ex.Message}", "MirDM", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void MenuOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (DownloadsDataGrid.SelectedItem is DownloadItem item && !string.IsNullOrEmpty(item.DestinationPath))
        {
            OpenInExplorer(item.DestinationPath);
        }
    }

    private void MenuCopyLink_Click(object sender, RoutedEventArgs e)
    {
        if (DownloadsDataGrid.SelectedItem is DownloadItem item && !string.IsNullOrEmpty(item.Url))
        {
            System.Windows.Clipboard.SetText(item.Url);
        }
    }

    private async void MenuRemoveTask_Click(object sender, RoutedEventArgs e)
    {
        var selectedList = DownloadsDataGrid.SelectedItems.Cast<DownloadItem>().ToList();
        if (selectedList.Count == 0) return;

        if (selectedList.Count == 1)
        {
            await _manager.RemoveDownloadAsync(selectedList[0], deleteFile: false);
        }
        else
        {
            var res = MessageBox.Show(
                $"Remove {selectedList.Count} selected downloads from list?\n(Files on disk will NOT be deleted)",
                "Remove Selected Downloads",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (res == MessageBoxResult.Yes)
            {
                await _manager.RemoveMultipleAsync(selectedList, deleteFile: false);
            }
        }
    }

    private async void MenuClearCompleted_Click(object sender, RoutedEventArgs e)
    {
        int completedCount = _manager.CompletedCount;
        if (completedCount == 0)
        {
            MessageBox.Show("There are no completed downloads to clear.", "MirDM", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var res = MessageBox.Show(
            $"Clear {completedCount} completed download(s) from list?\n(Downloaded files on disk will NOT be deleted)",
            "Clear Completed Downloads",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (res == MessageBoxResult.Yes)
        {
            await _manager.ClearCompletedAsync();
        }
    }

    private void MenuClearAll_Click(object sender, RoutedEventArgs e)
    {
        ClearList_Click(sender, e);
    }

    private async void MenuDeleteFileAndTask_Click(object sender, RoutedEventArgs e)
    {
        var selectedList = DownloadsDataGrid.SelectedItems.Cast<DownloadItem>().ToList();
        if (selectedList.Count == 0) return;

        if (selectedList.Count == 1)
        {
            var item = selectedList[0];
            var res = MessageBox.Show(
                $"Are you sure you want to permanently delete both the download task and the downloaded file:\n'{item.DestinationPath}'?",
                "Delete File and Task",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (res == MessageBoxResult.Yes)
            {
                await _manager.RemoveDownloadAsync(item, deleteFile: true);
            }
        }
        else
        {
            var res = MessageBox.Show(
                $"Are you sure you want to permanently delete {selectedList.Count} download tasks AND their files from disk?",
                "Delete Files and Tasks",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (res == MessageBoxResult.Yes)
            {
                await _manager.RemoveMultipleAsync(selectedList, deleteFile: true);
            }
        }
    }

    private static void OpenInExplorer(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                Process.Start("explorer.exe", $"/select,\"{filePath}\"");
            }
            else
            {
                string? dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    Process.Start("explorer.exe", $"\"{dir}\"");
                }
            }
        }
        catch { }
    }
}
