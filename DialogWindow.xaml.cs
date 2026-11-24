using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace ClaudeUsageWidget;

/// <summary>
/// What a dialog says: its colour and kind (as on a notice), a heading, a
/// text, notes in Markdown (the release notes, or a list of steps), a link
/// and one or two buttons.
/// </summary>
public record Dialog(
    NoticeTone Tone, string Kind, string Heading, string Text, string Yes, string? No = null,
    string Notes = "", string? LinkText = null, string? LinkUrl = null)
{
    /// <summary>Asked before removing the widget, from the settings or from --uninstall.</summary>
    public static Dialog Uninstall => new(NoticeTone.Crit, "Desinstalar", "Remover o Claude Usage Widget?",
        "O statusline e o aviso do Claude Code voltam ao que eram antes, e o widget, as definições e o histórico dele são apagados.",
        "Remover", "Cancelar");

    /// <summary>What removing it did, a line per step.</summary>
    public static Dialog Removed(IReadOnlyList<string> steps, bool failed) => new(
        failed ? NoticeTone.Warn : NoticeTone.Done, "Desinstalar",
        failed ? "Não deu para remover tudo" : "Widget removido",
        failed ? "O que ficou está nos passos abaixo." : "Para o voltar a ter, basta instalá-lo de novo.",
        "OK", Notes: string.Join("\n", steps.Select(s => "- " + s)));
}

/// <summary>
/// The widget's questions and reports, drawn like its notices instead of the
/// Windows message box: the version to install with its notes, removing the
/// widget, what removing it did.
/// </summary>
public partial class DialogWindow : Window
{
    private static readonly Brush Strong = Frozen(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Normal = Frozen(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Section = Frozen(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));

    private readonly string? _url;

    public DialogWindow(Dialog dialog)
    {
        InitializeComponent();
        var tone = Frozen(NoticeCard.Colour(dialog.Tone));
        Stripe.Background = tone;
        Kind.Text = dialog.Kind;
        Kind.Foreground = tone;
        Heading.Text = dialog.Heading;
        Body.Text = dialog.Text;
        Body.Visibility = dialog.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var blocks = ReleaseNotes.Parse(dialog.Notes);
        NotesBox.Visibility = blocks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var block in blocks) Notes.Children.Add(Draw(block, tone, first: Notes.Children.Count == 0));

        Yes.Content = dialog.Yes;
        // The button takes the dialog's colour: red to remove, blue to install.
        Yes.Background = tone;
        if (dialog.No is { } no) No.Content = no;
        else
        {
            No.Visibility = Visibility.Collapsed;
            // Esc closes it all the same (an IsCancel button would also set the result).
            PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        }

        _url = dialog.LinkUrl;
        if (dialog.LinkText is { } link && _url is not null)
        {
            Link.Text = link;
            Link.ToolTip = _url;
            Link.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) => Yes.Focus();
    }

    /// <summary>Shows it and waits: true when the main button was pressed.</summary>
    public static bool Ask(Dialog dialog, Window? owner = null)
    {
        var window = new DialogWindow(dialog);
        if (owner is { IsVisible: true }) window.Owner = owner;
        return window.ShowDialog() == true;
    }

    /// <summary>A heading, a bullet or a paragraph of the notes.</summary>
    internal static FrameworkElement Draw(NoteBlock block, Brush tone, bool first)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 16, Foreground = Normal };
        foreach (var run in block.Runs)
            text.Inlines.Add(new Run(run.Text)
            {
                FontWeight = run.Bold ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = run.Bold ? Strong : Normal,
            });

        switch (block.Kind)
        {
            case NoteKind.Heading:
                text.Foreground = Section;
                text.FontWeight = FontWeights.SemiBold;
                foreach (var inline in text.Inlines) inline.Foreground = Section;
                text.Margin = new Thickness(0, first ? 0 : 12, 0, 0);
                return text;
            case NoteKind.Bullet:
                var row = new Grid { Margin = new Thickness(0, first ? 0 : 7, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 4, Height = 4, Fill = tone,
                    VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(1, 6, 0, 0),
                });
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                return row;
            default:
                text.Foreground = Section;
                foreach (var inline in text.Inlines.Where(i => i.FontWeight != FontWeights.SemiBold)) inline.Foreground = Section;
                text.Margin = new Thickness(0, first ? 0 : 9, 0, 0);
                return text;
        }
    }

    private void OnYes(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnClose(object sender, MouseButtonEventArgs e) => Close();

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        // Not from the "x": the drag would take the click that closes.
        if (e.ButtonState == MouseButtonState.Pressed && !ReferenceEquals(e.OriginalSource, CloseGlyph)) DragMove();
    }

    private void OnLink(object sender, MouseButtonEventArgs e)
    {
        if (_url is null) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write($"Não deu para abrir {_url}: {ex.Message}"); }
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
