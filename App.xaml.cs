using System.Windows;

namespace ClaudeUsageWidget;

public partial class App : Application
{
    private Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Draw without the graphics card, as the widget's see-through windows
        // already do: plain windows (the installer, the settings) came out
        // blank on a machine where hardware rendering did not paint them.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

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

        // Installing: "--install" / "--uninstall", or a double click on the exe
        // in the unpacked zip. Before the single-instance check: an installed
        // widget may be running, and the installer replaces it.
        if (e.Args.Contains("--uninstall"))
        {
            UninstallAndExit(e.Args);
            return;
        }
        if (e.Args.Contains("--install") || (e.Args.Length == 0 && Installer.RunningFromPackage()))
        {
            Install(e.Args);
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
        // "--week": start with the weekly summary open.
        else if (e.Args.Contains("--week")) Dispatcher.BeginInvoke(() => main.OpenWeek(showLast: false));
    }

    // ---- installing ------------------------------------------------------------------

    /// <summary>The value after an option ("--dir D:\x"), or null.</summary>
    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>
    /// "--install [--silent] [--no-check] [--autostart|--no-autostart] [--no-launch] [--dir &lt;folder&gt;]".
    /// Silent is for updates: no window, the steps in install.log, exit code 0 when installed.
    /// </summary>
    private async void Install(string[] args)
    {
        var dir = Option(args, "--dir") ?? Installer.DefaultDir;
        if (!args.Contains("--silent"))
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            MainWindow = new InstallWindow(dir);
            MainWindow.Show();
            return;
        }

        var log = new List<string>();
        void Step(string text)
        {
            log.Add(text);
            Log.Write($"Instalação: {text}");
        }
        var code = 0;
        try
        {
            bool? autoStart = args.Contains("--autostart") ? true : args.Contains("--no-autostart") ? false : null;
            await Installer.InstallAsync(new InstallOptions(dir, autoStart, !args.Contains("--no-check"), !args.Contains("--no-launch")), Step);
            Step("Instalado.");
        }
        catch (Exception ex)
        {
            Step($"Não deu para instalar: {ex.Message}");
            code = 1;
        }
        try
        {
            System.IO.Directory.CreateDirectory(Paths.Dir);
            System.IO.File.WriteAllLines(System.IO.Path.Combine(Paths.Dir, "install.log"), log);
        }
        catch { }
        Shutdown(code);
    }

    /// <summary>"--uninstall [--silent] [--dir &lt;folder&gt;]": asks first unless silent.</summary>
    private void UninstallAndExit(string[] args)
    {
        var dir = Option(args, "--dir") ?? Installer.DefaultDir;
        var silent = args.Contains("--silent");
        if (!silent && MessageBox.Show(
                "Remover o Claude Usage Widget?\n\nO statusline e o aviso do Claude Code voltam ao que eram antes, e o widget, as definições e o histórico dele são apagados.",
                "Claude Usage Widget", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            Shutdown();
            return;
        }
        var steps = new List<string>();
        try { Installer.Uninstall(dir, steps.Add); }
        catch (Exception ex) { steps.Add($"Não deu para remover tudo: {ex.Message}"); }
        if (!silent) MessageBox.Show(string.Join("\n", steps), "Claude Usage Widget", MessageBoxButton.OK, MessageBoxImage.Information);
        Installer.RemoveFilesAfterExit(dir);
        Shutdown();
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
