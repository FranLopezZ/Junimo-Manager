using System.Formats.Tar;
using System.IO.Compression;
using SdvMods.Core.Updates;

namespace SdvMods.Tests;

public class PackageExtractorTests
{
    static void WriteTarGz(string path, string root, params (string Name, string Text)[] files)
    {
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.SmallestSize);
        using var tar = new TarWriter(gz, TarEntryFormat.Pax);
        tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, root + "/") { Mode = (UnixFileMode)0b111_101_101 });
        foreach (var (name, text) in files)
        {
            using var data = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"{root}/{name}") { Mode = (UnixFileMode)0b111_101_101, DataStream = data });
        }
    }

    [Fact]
    public void TarGzIsUnpackedIntoAFolderThatDoesNotExistYet()
    {
        // This is what failed on Linux: the destination ("…/update/new") had not been created.
        using var t = new TempDir();
        var archive = t.Sub("JunimoManager-9.9.9-linux-x64.tar.gz");
        WriteTarGz(archive, "JunimoManager-9.9.9-linux-x64", ("JunimoManager", "binary"), ("install.sh", "#!/bin/bash"), ("JunimoManager.png", "png"));

        var dest = t.Sub("downloads/update/new");
        Assert.False(Directory.Exists(dest));
        PackageExtractor.Extract(archive, dest);

        var folder = PackageExtractor.FindProgramFolder(dest, "JunimoManager");
        Assert.NotNull(folder);
        Assert.Equal("binary", File.ReadAllText(Path.Combine(folder!, "JunimoManager")));
        Assert.True(File.Exists(Path.Combine(folder!, "JunimoManager.png")));
    }

    [Fact]
    public void ZipIsUnpackedAndAnOldFolderIsReplaced()
    {
        using var t = new TempDir();
        var archive = t.Sub("JunimoManager-9.9.9-win-x64.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("JunimoManager.exe");
            using var w = new StreamWriter(e.Open()); w.Write("exe");
        }
        var dest = t.Sub("new");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "leftover.txt"), "old");     // from a previous, interrupted update

        PackageExtractor.Extract(archive, dest);
        Assert.False(File.Exists(Path.Combine(dest, "leftover.txt")));
        Assert.Equal(dest, PackageExtractor.FindProgramFolder(dest, "JunimoManager.exe"));
        Assert.Null(PackageExtractor.FindProgramFolder(dest, "Other.exe"));
    }

    [Fact]
    public void TheRealLinuxPackageProducedByTheBuildScriptCanBeUnpacked()
    {
        // Uses the latest package in ./release when it is there (skipped otherwise).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "JunimoManager.sln"))) dir = dir.Parent;
        var package = dir is null ? null : Directory.Exists(Path.Combine(dir.FullName, "release"))
            ? Directory.GetFiles(Path.Combine(dir.FullName, "release"), "*-linux-x64.zip").OrderBy(f => f).LastOrDefault() : null;
        if (package is null) return;

        using var t = new TempDir();
        PackageExtractor.Extract(package, t.Sub("a/b/new"));
        var folder = PackageExtractor.FindProgramFolder(t.Sub("a/b/new"), "JunimoManager");
        Assert.NotNull(folder);
        Assert.True(File.Exists(Path.Combine(folder!, "install.sh")));
        Assert.True(new FileInfo(Path.Combine(folder!, "JunimoManager")).Length > 10_000_000);
    }
}
