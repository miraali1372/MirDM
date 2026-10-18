using System;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Forms;
using MirDM.Core;
using Application = System.Windows.Application;

namespace MirDM.Views;

public partial class AddDownloadWindow : Window
{
    public bool WasStarted { get; private set; }

    public AddDownloadWindow(string? initialUrl = null)
    {
        InitializeComponent();

        var config = DownloadManager.Instance.Config;
        SaveDirTextBox.Text = config.DownloadDirectory;

        ConnectionsComboBox.SelectedIndex = config.DefaultConnections switch
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

        // Auto-detect clipboard URL if initialUrl not provided
        if (!string.IsNullOrWhiteSpace(initialUrl))
        {
            UrlTextBox.Text = initialUrl;
        }
        else
        {
            try
            {
                if (System.Windows.Clipboard.ContainsText())
                {
                    string clip = System.Windows.Clipboard.GetText().Trim();
                    if (clip.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        clip.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        UrlTextBox.Text = clip;
                    }
                }
            }
            catch { }
        }

        UrlTextBox.Focus();
        UrlTextBox.SelectAll();
    }

    private void UrlTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        string url = UrlTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(FileNameTextBox.Text) &&
            (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var uri = new Uri(url);
                string fileName = Path.GetFileName(uri.AbsolutePath);
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    FileNameTextBox.Text = WebUtility.UrlDecode(fileName);
                }
            }
            catch { }
        }
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select Destination Folder",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(SaveDirTextBox.Text) ? SaveDirTextBox.Text : DownloadManager.Instance.Config.DownloadDirectory
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            SaveDirTextBox.Text = dialog.SelectedPath;
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        string url = UrlTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(url) ||
            (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            System.Windows.MessageBox.Show("Please enter a valid HTTP or HTTPS URL.", "MirDM", MessageBoxButton.OK, MessageBoxImage.Warning);
            UrlTextBox.Focus();
            return;
        }

        string saveDir = SaveDirTextBox.Text.Trim();
        if (!Directory.Exists(saveDir))
        {
            try
            {
                Directory.CreateDirectory(saveDir);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Could not create directory:\n{ex.Message}", "MirDM Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        string destPath = "";
        if (!string.IsNullOrWhiteSpace(FileNameTextBox.Text))
        {
            destPath = Path.Combine(saveDir, FileNameTextBox.Text.Trim());
        }

        bool isAuto = ConnectionsComboBox.SelectedIndex == 0;
        int connections = ConnectionsComboBox.SelectedIndex switch
        {
            0 => 16, // Initial auto seed count (scales dynamically)
            1 => 4,
            2 => 8,
            3 => 16,
            4 => 24,
            5 => 32,
            6 => 64,
            _ => 16
        };

        bool immediate = StartImmediatelyCheck.IsChecked ?? true;

        var mirrorsList = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrWhiteSpace(MirrorsTextBox.Text))
        {
            var rawMirrors = MirrorsTextBox.Text.Split(new[] { ',', '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var m in rawMirrors)
            {
                string trimmed = m.Trim();
                if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    mirrorsList.Add(trimmed);
                }
            }
        }

        StartButton.IsEnabled = false;
        try
        {
            await DownloadManager.Instance.AddDownloadAsync(
                url: url,
                destinationPath: string.IsNullOrEmpty(destPath) ? null : destPath,
                connections: connections,
                isAutoConnections: isAuto,
                startImmediately: immediate,
                mirrors: mirrorsList);

            WasStarted = true;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to queue download: {ex.Message}", "MirDM", MessageBoxButton.OK, MessageBoxImage.Error);
            StartButton.IsEnabled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OpenBatch_Click(object sender, RoutedEventArgs e)
    {
        string currentText = UrlTextBox.Text.Trim();
        var batchWindow = new BatchDownloadWindow(string.IsNullOrEmpty(currentText) ? null : currentText)
        {
            Owner = this
        };

        if (batchWindow.ShowDialog() == true)
        {
            WasStarted = true;
            DialogResult = true;
            Close();
        }
    }
}
