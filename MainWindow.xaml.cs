using Microsoft.WindowsAPICodePack.Dialogs;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;

namespace WizNoteExporter
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
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

        private string _dataDir;

        public ObservableCollection<string> Accounts { get; set; }

        private void SelectOutputDirButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new CommonOpenFileDialog
            {
                IsFolderPicker = true
            };

            if (dlg.ShowDialog() == CommonFileDialogResult.Ok)
                outputDirTextBox.Text = dlg.FileName;
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (accountListBox.SelectedIndex == -1)
                return;

            var accountDir = Path.Combine(_dataDir, (string)accountListBox.SelectedValue);
            var outputDir = Path.GetFullPath(outputDirTextBox.Text);
            Exporter.ExportAll(accountDir, outputDir);
        }
    }
}