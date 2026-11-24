using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Xunit;
using Forms = System.Windows.Forms;

namespace ClaudeUsageWidget.Tests;

/// <summary>
/// The release notes read from their Markdown, the widget's own dialogs that
/// show them, and its two menus.
/// </summary>
public class DialogTests
{
    // ---- the notes --------------------------------------------------------------------------

    private const string Notes = """
        ## Novidades

        - **Definições com o aspeto do widget.** A janela passa a ter o fundo escuro,
          e arrasta-se pelo cabeçalho.
        - Ctrl+Tab passa ao separador seguinte (`Ctrl+PageDown` também).

        Esta atualização copia só os ficheiros pequenos. Ver [o LEIA-ME](https://example.com).
        """;

    [Fact]
    public void Notes_are_read_into_headings_bullets_and_paragraphs()
    {
        var blocks = ReleaseNotes.Parse(Notes.Replace("\n", "\r\n"));

        Assert.Equal([NoteKind.Heading, NoteKind.Bullet, NoteKind.Bullet, NoteKind.Text], blocks.Select(b => b.Kind));
        Assert.Equal("Novidades", blocks[0].Plain);
        // A bullet goes on over the next line; its bold part stays apart.
        Assert.Equal("Definições com o aspeto do widget. A janela passa a ter o fundo escuro, e arrasta-se pelo cabeçalho.", blocks[1].Plain);
        Assert.Equal([true, false], blocks[1].Runs.Select(r => r.Bold));
        // No Markdown left for code or links.
        Assert.Equal("Ctrl+Tab passa ao separador seguinte (Ctrl+PageDown também).", blocks[2].Plain);
        Assert.Equal("Esta atualização copia só os ficheiros pequenos. Ver o LEIA-ME.", blocks[3].Plain);
        Assert.Empty(ReleaseNotes.Parse(""));
    }

    [Fact]
    public void Several_versions_get_a_part_each_newest_first()
    {
        var one = ReleaseNotes.Combine([(new Version(1, 12, 0), "## Novidades\n\n- Um.")]);
        Assert.Equal("## Novidades\n\n- Um.", one);

        var two = ReleaseNotes.Combine([(new Version(1, 11, 0), "## Novidades\n\n- Antes."), (new Version(1, 12, 0), "## Novidades\r\n\r\n- Depois.")]);
        Assert.Equal("## Versão 1.12.0\n\n- Depois.\n\n## Versão 1.11.0\n\n- Antes.", two);
    }

    [Fact]
    public void An_update_lists_the_releases_it_went_past()
    {
        static object Release(string tag, bool draft = false) => new Dictionary<string, object>
        {
            ["tag_name"] = tag, ["draft"] = draft, ["prerelease"] = false, ["html_url"] = $"https://github.com/x/releases/tag/{tag}",
            ["body"] = $"## Novidades\n\n- Em {tag}.\n\n## Instalar ou atualizar\n\n1. ...",
            ["assets"] = new[] { new Dictionary<string, object> { ["name"] = "ClaudeUsageWidget.zip", ["browser_download_url"] = "https://x/z.zip", ["size"] = 1 } },
        };
        var json = JsonSerializer.Serialize(new[]
        {
            Release("v1.13.0"), Release("v1.12.0"), Release("v1.11.1", draft: true), Release("v1.11.0"), Release("v1.10.0"), Release("nightly"),
        });

        var since = Updater.Since(json, new Version(1, 10, 0), new Version(1, 12, 0));

        Assert.Equal(["1.12.0", "1.11.0"], since.Select(r => r.Version.ToString(3)));
        Assert.Equal("## Novidades\n\n- Em v1.12.0.", since[0].Notes);
        Assert.Empty(Updater.Since("{}", new Version(1, 0, 0), new Version(2, 0, 0)));
    }

    // ---- the dialogs ------------------------------------------------------------------------

    [Fact]
    public void A_dialog_draws_its_notes_and_buttons_in_its_colour()
    {
        OnSta(() =>
        {
            var ask = new DialogWindow(new Dialog(NoticeTone.Info, "Versão nova", "Instalar o Claude Usage 1.13.0?",
                "Tens a 1.12.0.", "Instalar", "Agora não", Notes, "Ver no GitHub", "https://github.com/"));
            Assert.Equal(Visibility.Visible, ask.NotesBox.Visibility);
            Assert.Equal(4, ask.Notes.Children.Count);
            Assert.Equal(Visibility.Visible, ask.No.Visibility);
            Assert.Equal(Visibility.Visible, ask.Link.Visibility);
            Assert.Equal(NoticeCard.Colour(NoticeTone.Info), ((SolidColorBrush)ask.Yes.Background).Color);

            var bullet = (Grid)ask.Notes.Children[1];
            var text = (TextBlock)bullet.Children[1];
            Assert.Equal(FontWeights.SemiBold, text.Inlines.First().FontWeight);

            var removed = new DialogWindow(Dialog.Removed(["Statusline: reposto.", "Não deu para remover tudo: em uso."], failed: true));
            Assert.Equal(Visibility.Collapsed, removed.No.Visibility);
            Assert.Equal(2, removed.Notes.Children.Count);
            Assert.Equal(NoticeCard.Colour(NoticeTone.Warn), ((SolidColorBrush)removed.Yes.Background).Color);
            Assert.Equal(Visibility.Collapsed, removed.Link.Visibility);

            var remove = new DialogWindow(Dialog.Uninstall);
            Assert.Equal(Visibility.Collapsed, remove.NotesBox.Visibility);
            Assert.Equal(NoticeCard.Colour(NoticeTone.Crit), ((SolidColorBrush)remove.Yes.Background).Color);
        });
    }

    // ---- the menus --------------------------------------------------------------------------

    [Fact]
    public void The_widgets_menu_takes_the_theme_down_to_its_separators()
    {
        OnSta(() =>
        {
            var theme = new ResourceDictionary { Source = new Uri("/ClaudeUsageWidget;component/Theme.xaml", UriKind.Relative) };
            Assert.IsType<Style>(theme[typeof(ContextMenu)]);
            Assert.IsType<Style>(theme[typeof(MenuItem)]);
            // A menu does not look its separators up by type.
            Assert.IsType<Style>(theme[MenuItem.SeparatorStyleKey]);
        });
    }

    [Fact]
    public void The_tray_menu_and_its_submenus_get_the_widgets_look()
    {
        OnSta(() =>
        {
            using var menu = new Forms.ContextMenuStrip();
            TrayMenu.Apply(menu);
            var item = new Forms.ToolStripMenuItem("Definições…").WithIcon("E713");
            var quiet = new Forms.ToolStripMenuItem("Não incomodar");
            menu.Items.Add(item);
            menu.Items.Add(quiet);
            quiet.DropDownItems.Add("30 minutos");

            Assert.Equal(TrayMenu.Panel, menu.BackColor);
            Assert.Equal(((char)0xE713).ToString(), item.Tag);
            Assert.Equal(TrayMenu.Text, item.ForeColor);
            // A submenu, even one filled when the menu opens, is drawn the same way.
            Assert.Same(menu.Renderer.GetType(), quiet.DropDown.Renderer.GetType());
            Assert.Equal(TrayMenu.Panel, quiet.DropDown.BackColor);
        });
    }

    // ---- the widget's tooltip ---------------------------------------------------------------

    [Fact]
    public void The_widgets_tooltip_shows_only_the_parts_it_has()
    {
        OnSta(() =>
        {
            WidgetTip.Limit[] limits =
            [
                new("5h", "45%", Colors.LightGreen, "renova hoje às 15:40 (faltam 2h10)"),
                new("7d", "72%", Colors.Gold, "renova sexta às 17:00 (faltam 3d02h)"),
            ];
            var bare = (StackPanel)WidgetTip.Build("", limits, [], [], "");
            Assert.Equal(2, bare.Children.Count);   // the heading and the limits
            Assert.Equal(6, ((Grid)bare.Children[1]).Children.Count);

            var full = (StackPanel)WidgetTip.Build("atualizado há 3m", limits,
                [("7d: esgota quinta às 14:00", true)], [("Uma sessão à tua espera", NoticeTone.Input)], "Auto-hello ligado");
            var texts = full.Children.OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("Ritmo", texts);
            Assert.Equal("Auto-hello ligado", texts[^1]);
            // The pace line that warns, and what is waiting, get the orange dot.
            // After the heading and the limits, each line is a grid with its dot.
            var dots = full.Children.OfType<Grid>().Skip(2).Select(g => ((SolidColorBrush)g.Children.OfType<System.Windows.Shapes.Ellipse>().Single().Fill).Color);
            Assert.Equal([NoticeCard.Colour(NoticeTone.Input), NoticeCard.Colour(NoticeTone.Input)], dots);
        });
    }

    private static void OnSta(Action test)
    {
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try { test(); }
            catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure is not null) throw failure;
    }
}
