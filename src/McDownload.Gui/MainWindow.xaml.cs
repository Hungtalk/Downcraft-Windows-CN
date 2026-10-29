using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using McDownload.Core.Diagnostics;

namespace McDownload.Gui;

/// <summary>Single window: settings on the left, live progress and log on the right.</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        _viewModel.LogLines.CollectionChanged += OnLogLinesChanged;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        FitToWorkArea();
        await _viewModel.InitializeAsync();
    }

    /// <summary>
    /// Keeps the window inside the usable screen area. High-DPI displays report a work area in
    /// device-independent units that can be smaller than the design size of this window.
    /// </summary>
    private void FitToWorkArea()
    {
        var work = SystemParameters.WorkArea;
        if (work.Width <= 0 || work.Height <= 0)
        {
            return;
        }

        Width = Math.Max(MinWidth, Math.Min(Width, work.Width - 24));
        Height = Math.Max(MinHeight, Math.Min(Height, work.Height - 24));
        Left = work.Left + ((work.Width - Width) / 2);
        Top = work.Top + ((work.Height - Height) / 2);
        WindowStartupLocation = WindowStartupLocation.Manual;
    }

    private void OnLogLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && LogList.Items.Count > 0)
        {
            LogList.ScrollIntoView(LogList.Items[^1]);
        }
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFolderDialog
            {
                Title = "选择 .minecraft 目录",
                Multiselect = false,
            };

            if (Directory.Exists(_viewModel.GameDirectory))
            {
                dialog.InitialDirectory = _viewModel.GameDirectory;
            }

            if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            {
                _viewModel.GameDirectory = dialog.FolderName;
            }
        }
        catch (Exception ex)
        {
            _viewModel.AppendLog(LogLevel.Error, "打开目录选择框失败：" + ex.Message);
        }
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_viewModel.GameDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_viewModel.GameDirectory}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _viewModel.AppendLog(LogLevel.Error, "无法打开目录：" + ex.Message);
        }
    }

    private async void RefreshVersionsButton_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.RefreshVersionsAsync(force: true);
    }

    private async void LoaderComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel)
        {
            await _viewModel.RefreshLoaderVersionsAsync();
        }
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.RunAsync(verifyOnly: false);
    }

    private async void VerifyButton_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.RunAsync(verifyOnly: true);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => _viewModel.Cancel();

    private void ClearLogButton_Click(object sender, RoutedEventArgs e) => _viewModel.LogLines.Clear();

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Cancel();
        base.OnClosed(e);
    }
}
