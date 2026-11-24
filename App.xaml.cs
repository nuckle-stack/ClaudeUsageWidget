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

        if (!TakeSingleInstance())
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

    // ---- one widget at a time ----------------------------------------------------------

    public const string ShowSignalName = "ClaudeUsageWidget.Show";

    /// <summary>
    /// Only one widget runs. When another holds the lock: if this is the
    /// installed copy and the other runs from somewhere else (an unpacked zip,
    /// an old copy), the installed one takes over; if it is the same widget,
    /// it is asked to show itself (opened from the Start menu while running).
    /// Either way the log says what happened: a widget that closes without a
    /// word looks like one that never started.
    /// </summary>
    private bool TakeSingleInstance()
    {
        _single = new Mutex(true, "ClaudeUsageWidget.SingleInstance", out var isNew);
        if (isNew) return true;

        // The one holding it may be on its way out: an update closes the old
        // widget and starts this one. Give it a moment before giving way.
        try
        {
            if (_single.WaitOne(TimeSpan.FromSeconds(3)))
            {
                Log.Write("O widget anterior acabou de fechar; fica este.");
                return true;
            }
        }
        catch (AbandonedMutexException) { return true; }

        var me = Environment.ProcessPath ?? "";
        var others = Installer.OtherCopies();
        var strays = others.Where(o => !string.Equals(o.Path, me, StringComparison.OrdinalIgnoreCase)).ToList();
        if (Installer.IsInstalledCopy(me) && strays.Count > 0)
        {
            foreach (var (pid, path, _) in strays)
            {
                Log.Write($"Outra cópia do widget aberta (pid {pid}, {path}): fechada, fica a instalada.");
                try
                {
                    using var p = System.Diagnostics.Process.GetProcessById(pid);
                    p.Kill();   // that process only: never a tree this one could be part of
                    p.WaitForExit(5000);
                }
                catch { }
            }
            try
            {
                if (_single.WaitOne(TimeSpan.FromSeconds(5))) return true;
            }
            // Its owner was killed: the lock is ours.
            catch (AbandonedMutexException) { return true; }
        }

        Log.Write($"O widget já está aberto ({string.Join(", ", others.Select(o => $"pid {o.Pid}, {o.Path}"))}): a mostrá-lo.");
        try
        {
            using var show = EventWaitHandle.OpenExisting(ShowSignalName);
            show.Set();
        }
        catch { }
        return false;
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
            // The old widget is closed by now: never leave the user with none.
            var installed = System.IO.Path.Combine(dir, Installer.ExeName);
            if (!args.Contains("--no-launch") && System.IO.File.Exists(installed))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(installed) { UseShellExecute = true, WorkingDirectory = dir });
                    Step("Aberto o widget que estava instalado.");
                }
                catch { }
            }
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
        if (!silent && !DialogWindow.Ask(Dialog.Uninstall))
        {
            Shutdown();
            return;
        }
        var steps = new List<string>();
        var failed = false;
        try { Installer.Uninstall(dir, steps.Add); }
        catch (Exception ex)
        {
            steps.Add($"Não deu para remover tudo: {ex.Message}");
            failed = true;
        }
        if (!silent) DialogWindow.Ask(Dialog.Removed(steps, failed));
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
