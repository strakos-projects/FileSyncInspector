using System.Configuration;
using System.Data;
using System.Windows;
using System.Globalization;
namespace FileSyncInspector
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Vynucení angličtiny (pokud to zakomentuješ, vezme se jazyk Windows)
            Thread.CurrentThread.CurrentCulture = new CultureInfo("en");
            Thread.CurrentThread.CurrentUICulture = new CultureInfo("en");

            base.OnStartup(e);
        }
    }

}
