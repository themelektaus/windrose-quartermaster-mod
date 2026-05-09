using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Windrose.Quartermaster.Web;

namespace Windrose.Quartermaster.App;

/// <summary>
/// WPF entry point. Starts the configurator's Kestrel host in-process on a
/// dynamic port, reads the actual bound URL, then opens a single
/// <see cref="MainWindow"/> that navigates a WebView2 to that URL. Closing
/// the window stops Kestrel cleanly via <see cref="OnExit"/>.
/// </summary>
public partial class App : Application
{
    private WebApplication _webApp;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            // Port 0 = let the OS pick a free TCP port. We resolve the real
            // address after StartAsync via IServerAddressesFeature -- this
            // avoids hard-coded port collisions if 17777 is busy and lets the
            // user run multiple desktop instances side-by-side.
            //
            // Data root: defer to Program.ResolveDataRoot() so the WPF wrapper
            // and the standalone Web entry point share the exact same
            // resolution rules. Dev runs find the repo via the
            // Profiles/_builtin marker; deployed/copied EXEs land at
            // %LOCALAPPDATA%\Quartermaster\.
            var (dataRoot, _) = Program.ResolveDataRoot();
            _webApp = Program.CreateWebApp(e.Args, "http://127.0.0.1:0", dataRoot);
            await _webApp.StartAsync();

            var url = ResolveBoundUrl(_webApp);

            var win = new MainWindow(url, dataRoot);
            win.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Quartermaster failed to start.\n\n" + ex.Message,
                "Quartermaster",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_webApp is not null)
        {
            try
            {
                // Give Kestrel a moment to drain in-flight requests (e.g. the
                // tail of an SSE setup-stream) before forcing teardown.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _webApp.StopAsync(cts.Token);
                await _webApp.DisposeAsync();
            }
            catch
            {
                // Swallow shutdown errors -- the process is exiting anyway and
                // surfacing them only spawns confusing dialogs at the very end.
            }
        }
        base.OnExit(e);
    }

    private static string ResolveBoundUrl(WebApplication app)
    {
        var server = app.Services.GetRequiredService<IServer>();
        var feature = server.Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException(
                "Kestrel did not expose IServerAddressesFeature.");

        // After StartAsync, IServerAddressesFeature.Addresses contains the
        // resolved URL with the real port (e.g. "http://127.0.0.1:54123").
        var address = feature.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "Kestrel reported no bound addresses after start.");
        return address;
    }
}
