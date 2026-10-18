using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using MirDM.Core;
using MirDM.Models;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace MirDM.Views;

public partial class BatchDownloadWindow : Window
{
    public ObservableCollection<BatchItemModel> Items { get; } = new();
    public int AddedCount { get; private set; }

    public BatchDownloadWindow(string? initialPatternOrUrl = null)
    {
        InitializeComponent();

        PreviewDataGrid.ItemsSource = Items;

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

        // If an initial pattern or URL is provided, detect whether it is a pattern
        if (!string.IsNullOrWhiteSpace(initialPatternOrUrl))
        {
            if (initialPatternOrUrl.Contains('*'))
            {
                ModeTabControl.SelectedIndex = 1;
                PatternUrlTextBox.Text = initialPatternOrUrl;
                GeneratePreviewFromPattern();
            }
            else
            {
                ModeTabControl.SelectedIndex = 0;
                UrlsTextBox.Text = initialPatternOrUrl;
            }
        }
        else
        {
            // Auto check clipboard for multi-line or wildcard URL
            try
            {
                if (System.Windows.Clipboard.ContainsText())
                {
                    string clip = System.Windows.Clipboard.GetText().Trim();
                    if (clip.Contains('*') && (clip.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || clip.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                    {
                        ModeTabControl.SelectedIndex = 1;
                        PatternUrlTextBox.Text = clip;
                        GeneratePreviewFromPattern();
                    }
                    else if (clip.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || clip.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        UrlsTextBox.Text = clip;
                    }
                }
            }
            catch { }
        }

        StateChanged += (s, e) =>
        {
            if (WindowState == WindowState.Maximized)
                MaximizeGlyph.Text = "🗗";
            else
                MaximizeGlyph.Text = "🗖";
        };

        UpdateSelectionSummary();
    }

    private void ModeTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != ModeTabControl) return;

        if (ModeTabControl.SelectedIndex == 0)
        {
            GeneratePreviewFromTextList();
        }
        else if (ModeTabControl.SelectedIndex == 1)
        {
            GeneratePreviewFromPattern();
        }
    }

    // --- Mode A: Multi-URL Text List ---

    private void UrlsTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ModeTabControl.SelectedIndex == 0)
        {
            GeneratePreviewFromTextList();
        }
    }

    private void PasteClipboard_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (System.Windows.Clipboard.ContainsText())
            {
                string clip = System.Windows.Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(UrlsTextBox.Text))
                {
                    UrlsTextBox.Text = clip;
                }
                else
                {
                    UrlsTextBox.Text = UrlsTextBox.Text.TrimEnd() + Environment.NewLine + clip;
                }
            }
        }
        catch { }
    }

    private void ClearText_Click(object sender, RoutedEventArgs e)
    {
        UrlsTextBox.Clear();
        Items.Clear();
        UpdateSelectionSummary();
    }

    private void GeneratePreviewFromTextList()
    {
        var rawLines = UrlsTextBox.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var validUrls = new List<string>();

        foreach (var line in rawLines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                validUrls.Add(trimmed);
            }
        }

        DetectedCountTextBlock.Text = $"✔ Detected: {validUrls.Count} valid links (from {rawLines.Length} lines)";

        Items.Clear();
        int idx = 1;
        foreach (var url in validUrls)
        {
            Items.Add(new BatchItemModel
            {
                Index = idx++,
                Url = url,
                FileName = ExtractFileNameFromUrl(url, idx - 1),
                IsSelected = true
            });
        }

        UpdateSelectionSummary();
    }

    // --- Mode B: Asterisk / Wildcard Pattern Generator ---

    private void PatternInputs_Changed(object sender, TextChangedEventArgs e)
    {
        if (ModeTabControl?.SelectedIndex == 1)
        {
            GeneratePreviewFromPattern();
        }
    }

    private void DigitsComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModeTabControl?.SelectedIndex == 1)
        {
            GeneratePreviewFromPattern();
        }
    }

    private void GeneratePattern_Click(object sender, RoutedEventArgs e)
    {
        GeneratePreviewFromPattern();
    }

    private void GeneratePreviewFromPattern()
    {
        if (PatternUrlTextBox == null || FromTextBox == null || ToTextBox == null || DigitsComboBox == null) return;

        string pattern = PatternUrlTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(pattern))
        {
            Items.Clear();
            UpdateSelectionSummary();
            return;
        }

        // Support both * and (*) as wildcard
        bool hasWildcard = pattern.Contains("(*)") || pattern.Contains('*');
        if (!hasWildcard)
        {
            Items.Clear();
            if (pattern.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                pattern.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                Items.Add(new BatchItemModel
                {
                    Index = 1,
                    Url = pattern,
                    FileName = ExtractFileNameFromUrl(pattern, 1),
                    IsSelected = true
                });
            }
            UpdateSelectionSummary();
            return;
        }

        if (!long.TryParse(FromTextBox.Text.Trim(), out long fromNum) ||
            !long.TryParse(ToTextBox.Text.Trim(), out long toNum))
        {
            return;
        }

        if (fromNum > toNum)
        {
            // Swap or return
            (fromNum, toNum) = (toNum, fromNum);
        }

        // Limit maximum batch preview generation to prevent freezing
        long count = toNum - fromNum + 1;
        if (count > 2000)
        {
            toNum = fromNum + 1999;
        }

        int digits = DigitsComboBox.SelectedIndex + 1; // 1..5

        Items.Clear();
        int index = 1;
        string formatSpecifier = new string('0', digits);

        for (long num = fromNum; num <= toNum; num++)
        {
            string formattedNum = num.ToString(formatSpecifier);
            string generatedUrl;

            if (pattern.Contains("(*)"))
            {
                generatedUrl = pattern.Replace("(*)", formattedNum);
            }
            else
            {
                // Replace * with formattedNum
                generatedUrl = pattern.Replace("*", formattedNum);
            }

            Items.Add(new BatchItemModel
            {
                Index = index++,
                Url = generatedUrl,
                FileName = ExtractFileNameFromUrl(generatedUrl, index - 1),
                IsSelected = true
            });
        }

        UpdateSelectionSummary();
    }

    // --- Helpers & UI Sync ---

    private static string ExtractFileNameFromUrl(string url, int fallbackIndex)
    {
        try
        {
            var uri = new Uri(url);
            string name = Path.GetFileName(uri.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return WebUtility.UrlDecode(name);
            }
        }
        catch { }
        return $"file_{fallbackIndex}";
    }

    private void UpdateSelectionSummary()
    {
        int total = Items.Count;
        int selected = Items.Count(i => i.IsSelected);

        PreviewCountTextBlock.Text = $"({selected} selected of {total} total)";
        EmptyPreviewPanel.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (selected == total && total > 0)
        {
            AddQueueButton.Content = $"➕ Add All to Queue ({total})";
        }
        else
        {
            AddQueueButton.Content = $"➕ Add Selected to Queue ({selected})";
        }

        AddQueueButton.IsEnabled = selected > 0;
    }

    private void HeaderCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.CheckBox cb)
        {
            bool check = cb.IsChecked ?? true;
            foreach (var item in Items)
            {
                item.IsSelected = check;
            }
            UpdateSelectionSummary();
        }
    }

    private void ItemCheckBox_Click(object sender, RoutedEventArgs e)
    {
        UpdateSelectionSummary();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Items)
        {
            item.IsSelected = true;
        }
        UpdateSelectionSummary();
    }

    private void DeselectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Items)
        {
            item.IsSelected = false;
        }
        UpdateSelectionSummary();
    }

    private void InvertSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Items)
        {
            item.IsSelected = !item.IsSelected;
        }
        UpdateSelectionSummary();
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select Destination Folder for Batch Downloads",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(SaveDirTextBox.Text) ? SaveDirTextBox.Text : DownloadManager.Instance.Config.DownloadDirectory
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            SaveDirTextBox.Text = dialog.SelectedPath;
        }
    }

    private async void AddQueueButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedUrls = Items.Where(i => i.IsSelected).Select(i => i.Url).ToList();
        if (selectedUrls.Count == 0)
        {
            MessageBox.Show("Please select at least one URL to download.", "MirDM", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                MessageBox.Show($"Could not create directory:\n{ex.Message}", "MirDM Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        bool isAuto = ConnectionsComboBox.SelectedIndex == 0;
        int connections = ConnectionsComboBox.SelectedIndex switch
        {
            0 => 16,
            1 => 4,
            2 => 8,
            3 => 16,
            4 => 24,
            5 => 32,
            6 => 64,
            _ => 16
        };

        bool immediate = StartImmediatelyCheck.IsChecked ?? false;

        AddQueueButton.IsEnabled = false;
        try
        {
            var added = await DownloadManager.Instance.AddBatchDownloadsAsync(
                urls: selectedUrls,
                saveDirectory: saveDir,
                connections: connections,
                isAutoConnections: isAuto,
                startImmediately: immediate);

            AddedCount = added.Count;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to queue batch downloads: {ex.Message}", "MirDM", MessageBoxButton.OK, MessageBoxImage.Error);
            AddQueueButton.IsEnabled = true;
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            MaximizeGlyph.Text = "🗖";
        }
        else
        {
            WindowState = WindowState.Maximized;
            MaximizeGlyph.Text = "🗗";
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
}
