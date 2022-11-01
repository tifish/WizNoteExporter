using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.WindowsAPICodePack.Dialogs;

namespace WizNoteExporter;

/// <summary>
///     Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var documentDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        _dataDir = Path.Combine(documentDir, @"My Knowledge\Data");
        Accounts = new ObservableCollection<string>(Directory.GetDirectories(_dataDir).Select(Path.GetFileName)!);

        DataContext = this;
    }

    private readonly string _dataDir;

    public ObservableCollection<string> Accounts { get; set; }

    private void SelectOutputDirButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new CommonOpenFileDialog
        {
            IsFolderPicker = true,
        };

        if (dlg.ShowDialog() == CommonFileDialogResult.Ok)
            outputDirTextBox.Text = dlg.FileName;
    }

    [DllImport("Kernel32")]
    public static extern void AllocConsole();

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (accountListBox.SelectedIndex == -1)
            return;

        AllocConsole();

        var accountDir = Path.Combine(_dataDir, (string)accountListBox.SelectedValue);
        var outputDir = Path.GetFullPath(outputDirTextBox.Text);
        Exporter.ExportAll(accountDir, outputDir);
    }
}
