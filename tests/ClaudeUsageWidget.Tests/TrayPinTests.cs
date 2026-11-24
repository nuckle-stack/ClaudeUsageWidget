using Microsoft.Win32;
using Xunit;

namespace ClaudeUsageWidget.Tests;

/// <summary>
/// The tray icon on the taskbar or in the "^": Windows' entries are copied
/// into a key of the tests' own, never the real one.
/// </summary>
public sealed class TrayPinTests : IDisposable
{
    private const string Exe = @"C:\Users\Ana\AppData\Local\Programs\ClaudeUsageWidget\ClaudeUsageWidget.exe";
    private const string Parent = @"Software\ClaudeUsageWidget.Tests";
    private readonly string _key = $@"{Parent}\NotifyIconSettings-{Guid.NewGuid():N}";

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_key, throwOnMissingSubKey: false);
        var empty = false;
        using (var parent = Registry.CurrentUser.OpenSubKey(Parent))
            empty = parent is { SubKeyCount: 0, ValueCount: 0 };
        if (empty) Registry.CurrentUser.DeleteSubKey(Parent, throwOnMissingSubKey: false);
    }

    private void Entry(string name, string path, int? promoted = null)
    {
        using var entry = Registry.CurrentUser.CreateSubKey($@"{_key}\{name}");
        entry.SetValue("ExecutablePath", path);
        if (promoted is { } p) entry.SetValue("IsPromoted", p, RegistryValueKind.DWord);
    }

    private int? Promoted(string name)
    {
        using var entry = Registry.CurrentUser.OpenSubKey($@"{_key}\{name}");
        return entry?.GetValue("IsPromoted") as int?;
    }

    [Fact]
    public void Windows_paths_are_matched_with_or_without_a_known_folder_in_front()
    {
        Assert.True(TrayPin.SameProgram(Exe.ToUpperInvariant(), Exe));
        Assert.False(TrayPin.SameProgram(@"D:\Home Work\ClaudeUsageWidget\bin\Debug\ClaudeUsageWidget.exe", Exe));

        var local = Guid.Parse("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
        string? Folder(Guid id) => id == local ? @"C:\Users\Ana\AppData\Local" : null;
        Assert.True(TrayPin.SameProgram(@"{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}\Programs\ClaudeUsageWidget\ClaudeUsageWidget.exe", Exe, Folder));
        // A folder it can't resolve is not a match.
        Assert.False(TrayPin.SameProgram(@"{6D809377-6AF0-444B-8957-A3773F02200E}\Programs\ClaudeUsageWidget\ClaudeUsageWidget.exe", Exe, Folder));
    }

    [Fact]
    public void The_widgets_entry_goes_on_the_taskbar_and_back_without_touching_the_others()
    {
        // Before Windows has seen the icon there is nothing to change.
        Assert.Null(TrayPin.IsPinned(Exe, _key));
        Assert.False(TrayPin.Pin(Exe, true, _key));

        Entry("1", Exe);
        Entry("2", @"C:\Program Files\Other\other.exe");
        Entry("3", @"C:\Program Files\Kept\kept.exe", promoted: 1);
        Assert.False(TrayPin.IsPinned(Exe, _key));

        Assert.True(TrayPin.Pin(Exe, true, _key));
        Assert.True(TrayPin.IsPinned(Exe, _key));
        Assert.Equal(1, Promoted("1"));
        Assert.Null(Promoted("2"));

        Assert.True(TrayPin.Pin(Exe, false, _key));
        Assert.False(TrayPin.IsPinned(Exe, _key));
        Assert.Equal(0, Promoted("1"));
        Assert.Equal(1, Promoted("3"));
    }
}
