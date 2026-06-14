using System;
using System.Text;
using System.Windows;

namespace WizNoteExporter
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            Console.OutputEncoding = Encoding.UTF8;
            base.OnStartup(e);
        }
    }
}
