using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace ClaudeUsageWidget;

/// <summary>
/// What a double click on ClaudeUsageWidget.exe in the unpacked zip shows:
/// what installing does, two choices, then each step as it is done. An
/// installed (older or same) version is updated in place.
/// </summary>
public partial class InstallWindow : Window
{
    private readonly string _dir;
    private bool _done;
    private string? _report;

    public InstallWindow(string dir)
    {
        InitializeComponent();
        _dir = dir;
        var version = Updater.Current.ToString(3);
        var installed = Installer.InstalledVersion(dir);
        (Heading.Text, Go.Content) = installed switch
        {
            null => ($"Instalar o Claude Usage Widget {version}", "Instalar"),
            var v when v == version => ($"Reinstalar o Claude Usage Widget {version}", "Reinstalar"),
            var v => ($"Atualizar o Claude Usage Widget de {v} para {version}", "Atualizar"),
        };
        WhereTo.Text = $"• Copiar o widget para {dir} (não é preciso ser administrador).";
        // A new install starts with Windows unless unticked; an update keeps what was chosen.
        AutoStart.IsChecked = installed is null || Installer.AutoStarts();
    }

    private async void OnGo(object sender, RoutedEventArgs e)
    {
        if (_done)
        {
            Close();
            return;
        }

        Choices.Visibility = Visibility.Collapsed;
        Steps.Visibility = Visibility.Visible;
        Busy.Visibility = Visibility.Visible;
        Go.IsEnabled = false;
        Cancel.IsEnabled = false;
        var steps = new System.Collections.ObjectModel.ObservableCollection<string>();
        Steps.ItemsSource = steps;
        // Steps come from this thread; the hello awaits without leaving it.
        void Step(string text)
        {
            steps.Add("• " + text);
            Log.Write($"Instalação: {text}");
        }

        try
        {
            var result = await Installer.InstallAsync(
                new InstallOptions(_dir, AutoStart.IsChecked == true, Check.IsChecked == true, Launch: true), Step);
            Outcome.Text = result.LimitsOk == false ? "Instalado, mas sem os limites." : "Instalado.";
            Hint.Text = result.LimitsOk == false
                ? "As sessões e os avisos funcionam. Para os limites (5h / 7d): instala o Claude Code CLI, corre 'claude' num terminal e faz /login. " +
                  "Depois: botão direito no widget > Atualizar agora." +
                  (result.DiagnosticsPath is { } path ? $"\nSe não resolver, envia o diagnóstico a quem te deu o widget ({path})." : "")
                : "O widget está no fundo do ecrã. Clica nele para ver as sessões; botão direito para as Definições.";
            _report = result.DiagnosticsPath;
        }
        catch (Exception ex)
        {
            Log.Write($"Instalação falhou: {ex}");
            Outcome.Text = "Não deu para instalar.";
            Outcome.Foreground = System.Windows.Media.Brushes.Firebrick;
            Hint.Text = ex.Message;
        }

        Busy.Visibility = Visibility.Collapsed;
        Outcome.Visibility = Visibility.Visible;
        Hint.Visibility = Visibility.Visible;
        CopyReport.Visibility = _report is not null ? Visibility.Visible : Visibility.Collapsed;
        _done = true;
        Go.Content = "Fechar";
        Go.IsEnabled = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnCopyReport(object sender, RoutedEventArgs e)
    {
        if (_report is null || !File.Exists(_report)) return;
        try
        {
            Clipboard.SetText(File.ReadAllText(_report));
            CopyReport.Content = "Copiado";
        }
        catch (ExternalException) { CopyReport.Content = "Não deu para copiar"; }
    }
}
