using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace ClaudeUsageWidget.Tests;

/// <summary>Updating from the GitHub releases.</summary>
public class UpdaterTests
{
    private readonly string _dir = Path.Combine(Isolation.Root, "update-" + Guid.NewGuid().ToString("N"));

    /// <summary>The fields the widget reads from GitHub's latest-release answer, as GitHub sends them.</summary>
    private static string Release(string tag = "v1.2.0", bool draft = false, bool prerelease = false,
        string? digest = "sha256:7107E68F4C4C223AAFCE870F426DD59716AE3B24F214F85AE3BD2D539879C4AA", string asset = "ClaudeUsageWidget.zip",
        string body = "## Novidades\r\n\r\n- **Contexto ocupado** em todas as sessões (`45% ctx`).\r\n\r\n## Instalar ou atualizar\r\n\r\n1. Em **Assets**...\r\n\r\nSHA256 do zip: `aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa`")
    {
        var zip = new Dictionary<string, object?>
        {
            ["name"] = asset,
            ["size"] = 63663611,
            ["browser_download_url"] = $"https://github.com/nuckle-stack/ClaudeUsageWidget/releases/download/{tag}/{asset}",
        };
        if (digest is not null) zip["digest"] = digest;
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["html_url"] = $"https://github.com/nuckle-stack/ClaudeUsageWidget/releases/tag/{tag}",
            ["tag_name"] = tag,
            ["draft"] = draft,
            ["prerelease"] = prerelease,
            ["assets"] = new[] { zip },
            ["body"] = body,
        });
    }

    [Fact]
    public void Reads_the_version_the_zip_and_its_hash()
    {
        var u = Updater.Parse(Release())!;
        Assert.Equal(new Version(1, 2, 0), u.Version);
        Assert.Equal("https://github.com/nuckle-stack/ClaudeUsageWidget/releases/download/v1.2.0/ClaudeUsageWidget.zip", u.ZipUrl);
        Assert.Equal(63663611, u.Size);
        Assert.Equal("7107e68f4c4c223aafce870f426dd59716ae3b24f214f85ae3bd2d539879c4aa", u.Sha256);
        Assert.EndsWith("/releases/tag/v1.2.0", u.PageUrl);
    }

    [Fact]
    public void The_notes_stop_before_the_install_steps_and_lose_the_markdown()
    {
        Assert.Equal("## Novidades\n\n- **Contexto ocupado** em todas as sessões (`45% ctx`).", Updater.Parse(Release())!.Notes);
    }

    [Fact]
    public void Without_GitHub_s_hash_the_one_in_the_notes_is_used()
    {
        Assert.Equal(new string('a', 64), Updater.Parse(Release(digest: null))!.Sha256);
        Assert.Null(Updater.Parse(Release(digest: null, body: "Sem hash."))!.Sha256);
    }

    [Theory]
    [InlineData("v1.2.0-beta", false, false, "ClaudeUsageWidget.zip")]
    [InlineData("nightly", false, false, "ClaudeUsageWidget.zip")]
    [InlineData("v1.2.0", true, false, "ClaudeUsageWidget.zip")]
    [InlineData("v1.2.0", false, true, "ClaudeUsageWidget.zip")]
    [InlineData("v1.2.0", false, false, "source.zip")]
    public void Releases_it_cannot_install_are_ignored(string tag, bool draft, bool prerelease, string asset)
    {
        Assert.Null(Updater.Parse(Release(tag, draft, prerelease, asset: asset)));
        Assert.Null(Updater.Parse("not json"));
    }

    [Fact]
    public void A_tag_without_a_patch_number_still_compares_as_a_version()
    {
        Assert.Equal(new Version(2, 0, 0), Updater.Parse(Release("v2.0"))!.Version);
    }

    [Fact]
    public void Each_version_is_announced_once()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "update.json");
        var u = Updater.Parse(Release())!;
        Assert.True(new Updater(path).ShouldAnnounce(u));
        Assert.False(new Updater(path).ShouldAnnounce(u));   // also after a restart
        Assert.True(new Updater(path).ShouldAnnounce(Updater.Parse(Release("v1.3.0"))!));
    }

    [Fact]
    public void An_update_is_told_once_and_a_fresh_install_not_at_all()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "update.json");
        Assert.Null(new Updater(path).JustUpdatedFrom());   // first run after installing
        Assert.Null(new Updater(path).JustUpdatedFrom());   // same version again

        File.WriteAllText(path, """{"announced":"","lastRun":"0.9.0"}""");
        Assert.Equal("0.9.0", new Updater(path).JustUpdatedFrom());
        Assert.Null(new Updater(path).JustUpdatedFrom());
    }

    // ---- the download --------------------------------------------------------------

    /// <summary>A zip laid out as pack.ps1 makes it: a folder, with backslashes, as Windows PowerShell writes them.</summary>
    private string Zip(bool withInstaller = true)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "ClaudeUsageWidget.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            // The widget installs itself; the feed goes with it.
            if (withInstaller) Write(zip, @"ClaudeUsageWidget\ClaudeUsageWidget.exe", "MZ");
            Write(zip, @"ClaudeUsageWidget\ClaudeUsageFeed.exe", "MZ");
            Write(zip, @"ClaudeUsageWidget\install.ps1", "Write-Host hi");
        }
        return path;

        static void Write(ZipArchive zip, string name, string text)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open());
            w.Write(text);
        }
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    [Fact]
    public void A_good_zip_is_unpacked_and_its_installer_found()
    {
        var zip = Zip();
        var folder = Updater.Unpack(zip, Sha(zip), Path.Combine(_dir, "out"));
        Assert.True(File.Exists(Path.Combine(folder, "ClaudeUsageWidget.exe")));
        Assert.True(File.Exists(Path.Combine(folder, "ClaudeUsageFeed.exe")));
    }

    [Fact]
    public void A_zip_that_does_not_match_its_hash_is_refused()
    {
        var zip = Zip();
        var e = Assert.Throws<InvalidDataException>(() => Updater.Unpack(zip, new string('0', 64), Path.Combine(_dir, "out")));
        Assert.Contains("não confere", e.Message);
        Assert.False(Directory.Exists(Path.Combine(_dir, "out")));
    }

    [Fact]
    public void A_zip_without_the_installer_is_refused()
    {
        var zip = Zip(withInstaller: false);
        Assert.Throws<InvalidDataException>(() => Updater.Unpack(zip, Sha(zip), Path.Combine(_dir, "out")));
    }

    [Fact]
    public void Unpacking_again_replaces_what_an_earlier_try_left()
    {
        var zip = Zip();
        var target = Path.Combine(_dir, "out");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "leftover.txt"), "x");
        Updater.Unpack(zip, null, target);
        Assert.False(File.Exists(Path.Combine(target, "leftover.txt")));
    }
}
