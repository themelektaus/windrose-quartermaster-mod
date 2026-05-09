using System;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Windrose.Quartermaster.App;

public partial class MainWindow : Window
{
    private readonly string _url;
    private readonly string _dataRoot;

    public MainWindow(string url, string dataRoot)
    {
        InitializeComponent();
        _url = url;
        _dataRoot = dataRoot;
        ApplyStartupBounds();
        Loaded += OnLoadedAsync;
    }

    // 5% padding around the primary work area (taskbar excluded), centered.
    // WorkArea is in DIPs, matching Window.Left/Top/Width/Height units.
    private void ApplyStartupBounds()
    {
        var wa = SystemParameters.WorkArea;
        const double pad = 0.05;
        Width = wa.Width * (1.0 - 2.0 * pad);
        Height = wa.Height * (1.0 - 2.0 * pad);
        Left = wa.Left + wa.Width * pad;
        Top = wa.Top + wa.Height * pad;
    }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            // Keep the WebView2 user-data folder inside the DataRoot so that
            // (a) cookies/cache survive across runs without needing write
            // access to %ProgramFiles%, (b) the portable EXE doesn't litter
            // its drop folder with hidden state -- everything mutable lives
            // inside QuartermasterData/ next to or under the EXE depending
            // on dev-vs-deployed mode.
            var userDataFolder = Path.Combine(_dataRoot, ".webview2");
            Directory.CreateDirectory(userDataFolder);

            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder);
            await Web.EnsureCoreWebView2Async(env);

            Web.CoreWebView2.Navigate(_url);
        }
        catch (Exception ex)
        {
            // Most common failure: WebView2 Runtime not installed. The error
            // text from the SDK is already specific enough -- surface it.
            MessageBox.Show(
                "Failed to initialize WebView2.\n\n" + ex.Message +
                "\n\nMake sure the Microsoft Edge WebView2 Runtime is installed.\n" +
                "Download: https://developer.microsoft.com/microsoft-edge/webview2/",
                "Quartermaster",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Close();
        }
    }
}
