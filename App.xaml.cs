using System.Windows;

namespace ClaudeUsageWidget;

public partial class App : Application
{
    private Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // "--hello": send one hello through the same path the widget uses, then
        // exit (0 = sent). Works alongside a running instance.
        if (e.Args.Contains("--hello"))
        {
            SendHelloAndExit();
            return;
        }

        _single = new Mutex(true, "ClaudeUsageWidget.SingleInstance", out var isNew);
        if (!isNew)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write($"Erro não tratado: {args.Exception}");
            args.Handled = true;
        };

        var main = new MainWindow();
        main.Show();
        // "--sessions": start with the sessions popup open.
        if (e.Args.Contains("--sessions")) Dispatcher.BeginInvoke(main.ToggleSessions);
    }

    private async void SendHelloAndExit()
    {
        var ok = false;
        try { ok = await new AutoHello().SendAsync(Json.Load<Config>(Paths.Config)); }
        catch (Exception ex) { Log.Write($"--hello falhou: {ex}"); }
        Shutdown(ok ? 0 : 1);
    }
}
