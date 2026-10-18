using System;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using MirDM.Core;

namespace MirDM.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        LoadCurrentConfig();
    }

    private void LoadCurrentConfig()
    {
        var config = DownloadManager.Instance.Config;
        DownloadDirTextBox.Text = config.DownloadDirectory;

        DefaultConnectionsCombo.SelectedIndex = config.DefaultConnections switch
        {
            0 => 0,
            4 => 1,
            8 => 2,
            16 => 3,
            24 => 4,
            32 => 5,
            64 => 6,
            _ => 0
        };

        DnsRacingCheck.IsChecked = config.EnableDnsEdgeRacing;
        Http3Check.IsChecked = config.EnableHttp3;
        ProportionalChunkingCheck.IsChecked = config.EnableProportionalChunking;
        PipelinedWindowCheck.IsChecked = config.EnablePipelinedWindow;
        DynamicSplittingCheck.IsChecked = config.DynamicSplitting;
        TailLatencyCheck.IsChecked = config.TailLatencyElimination;
        MinimizeToTrayCheck.IsChecked = config.MinimizeToTray;
        ClipboardCheck.IsChecked = config.ClipboardMonitoring;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select Default Download Directory",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(DownloadDirTextBox.Text) ? DownloadDirTextBox.Text : DownloadManager.Instance.Config.DownloadDirectory
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            DownloadDirTextBox.Text = dialog.SelectedPath;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var config = DownloadManager.Instance.Config;

        string dir = DownloadDirTextBox.Text.Trim();
        if (!string.IsNullOrEmpty(dir))
        {
            if (!Directory.Exists(dir))
            {
                try
                {
                    Directory.CreateDirectory(dir);
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Invalid folder path: {ex.Message}", "MirDM Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            config.DownloadDirectory = dir;
        }

        config.DefaultConnections = DefaultConnectionsCombo.SelectedIndex switch
        {
            0 => 0,  // Auto (Dynamic)
            1 => 4,
            2 => 8,
            3 => 16,
            4 => 24,
            5 => 32,
            6 => 64,
            _ => 0
        };

        config.EnableDnsEdgeRacing = DnsRacingCheck.IsChecked ?? true;
        config.EnableHttp3 = Http3Check.IsChecked ?? true;
        config.EnableProportionalChunking = ProportionalChunkingCheck.IsChecked ?? true;
        config.EnablePipelinedWindow = PipelinedWindowCheck.IsChecked ?? true;
        config.DynamicSplitting = DynamicSplittingCheck.IsChecked ?? true;
        config.TailLatencyElimination = TailLatencyCheck.IsChecked ?? true;
        config.MinimizeToTray = MinimizeToTrayCheck.IsChecked ?? true;
        config.ClipboardMonitoring = ClipboardCheck.IsChecked ?? true;

        config.Save();
        DialogResult = true;
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
