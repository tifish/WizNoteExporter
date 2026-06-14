using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

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
        Accounts = new ObservableCollection<string>(
            Directory.GetDirectories(_dataDir).Select(Path.GetFileName)!
        );

        DataContext = this;
    }

    private readonly string _dataDir;

    public ObservableCollection<string> Accounts { get; set; }

    private void SelectOutputDirButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "选择输出目录",
            InitialDirectory = Directory.Exists(outputDirTextBox.Text)
                ? Path.GetFullPath(outputDirTextBox.Text)
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };

        if (dlg.ShowDialog(this) == true)
            outputDirTextBox.Text = dlg.FolderName;
    }

    [DllImport("Kernel32")]
    public static extern void AllocConsole();

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (accountListBox.SelectedIndex == -1)
            return;

        var accountDir = Path.Combine(_dataDir, (string)accountListBox.SelectedValue);
        var outputDir = Path.GetFullPath(outputDirTextBox.Text);

        if (clearOutputDirCheckBox.IsChecked == true && Directory.Exists(outputDir))
        {
            var confirm = MessageBox.Show(
                this,
                $"将清空目录下所有文件和子目录：\n{outputDir}\n\n确定继续吗？",
                "确认清空",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning,
                MessageBoxResult.Cancel
            );
            if (confirm != MessageBoxResult.OK)
                return;

            ClearDirectory(outputDir);
        }

        AllocConsole();

        Exporter.ExportAll(accountDir, outputDir);
    }

    private static void ClearDirectory(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }
        foreach (var sub in Directory.EnumerateDirectories(dir))
            Directory.Delete(sub, recursive: true);
    }
}
