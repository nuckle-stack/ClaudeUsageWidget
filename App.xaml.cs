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

        // "--diagnostics": write the diagnostics report to its file, then exit.
        // For the installer when its check fails, and for a widget that won't start.
        if (e.Args.Contains("--diagnostics"))
        {
            WriteDiagnosticsAndExit();
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

    private async void WriteDiagnosticsAndExit()
    {
        var ok = false;
        try
        {
            var cfg = Json.Load<Config>(Paths.Config);
            var hello = new AutoHello();
            var limits = Json.Load<Limits>(Paths.Limits);
            var claudeDir = System.IO.Path.GetDirectoryName(Paths.Dir)!;
            await UsageLedger.Shared.RefreshAsync(TimeSpan.Zero);
            var observations = await Task.Run(() => FormatWatch.Observe(claudeDir, limits.UpdatedAt, false));
            var sessions = await Task.Run(SessionScanner.Scan);
            // Without the widget running, nothing knows the shortcut's state or who is waiting.
            var text = await Diagnostics.BuildAsync(new Diagnostics.Live(
                cfg, limits, hello.Ping, false, hello.Backoff,
                (cfg.Hotkey.Length > 0 ? cfg.Hotkey : "nenhum") + " (o widget não estava aberto)", sessions, -1,
                Format: FormatWatch.Once(observations, DateTimeOffset.UtcNow.ToUnixTimeSeconds())));
            System.IO.Directory.CreateDirectory(Paths.Dir);
            System.IO.File.WriteAllText(Diagnostics.ReportPath, text);
            ok = true;
        }
        catch (Exception ex) { Log.Write($"--diagnostics falhou: {ex}"); }
        Shutdown(ok ? 0 : 1);
    }
}
