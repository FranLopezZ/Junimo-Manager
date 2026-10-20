using System.IO.Compression;

namespace SdvMods.Tests;

public class UnixZipTests
{
    static string MakeFolder(TempDir t)
    {
        var folder = t.Sub("JunimoManager-1.2.3-linux-x64");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "JunimoManager"), "binary");
        File.WriteAllText(Path.Combine(folder, "install.sh"), "#!/bin/bash\n");
        File.WriteAllText(Path.Combine(folder, "JunimoManager.png"), "png");
        return folder;
    }

    [Fact]
    public void ZipKeepsTheExecutableBitAndIsMarkedAsMadeOnUnix()
    {
        using var t = new TempDir();
        var zipPath = t.Sub("pkg.zip");
        UnixZip.Create(MakeFolder(t), zipPath, "JunimoManager-1.2.3-linux-x64");

        using (var zip = ZipFile.OpenRead(zipPath))
        {
            int Mode(string name) => (zip.GetEntry("JunimoManager-1.2.3-linux-x64/" + name)!.ExternalAttributes >> 16) & 0xFFF;
            Assert.Equal(0b111_101_101, Mode("JunimoManager"));          // 755
            Assert.Equal(0b111_101_101, Mode("install.sh"));             // 755
            Assert.Equal(0b110_100_100, Mode("JunimoManager.png"));      // 644
        }

        // Unix unzip only trusts those modes when every central-directory entry says "made by Unix" (host byte 3).
        var b = File.ReadAllBytes(zipPath);
        var eocd = b.Length - 22;
        while (!(b[eocd] == 0x50 && b[eocd + 1] == 0x4B && b[eocd + 2] == 5 && b[eocd + 3] == 6)) eocd--;
        var count = BitConverter.ToUInt16(b, eocd + 10);
        var pos = (int)BitConverter.ToUInt32(b, eocd + 16);
        Assert.Equal(3, count);
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(3, b[pos + 5]);
            pos += 46 + BitConverter.ToUInt16(b, pos + 28) + BitConverter.ToUInt16(b, pos + 30) + BitConverter.ToUInt16(b, pos + 32);
        }
    }

    [Fact]
    public void TheZipIsStillAValidArchiveThatTheUpdaterCanUnpack()
    {
        using var t = new TempDir();
        var zipPath = t.Sub("pkg.zip");
        UnixZip.Create(MakeFolder(t), zipPath, "JunimoManager-1.2.3-linux-x64");
        SdvMods.Core.Updates.PackageExtractor.Extract(zipPath, t.Sub("out/new"));
        var folder = SdvMods.Core.Updates.PackageExtractor.FindProgramFolder(t.Sub("out/new"), "JunimoManager");
        Assert.NotNull(folder);
        Assert.Equal("binary", File.ReadAllText(Path.Combine(folder!, "JunimoManager")));
        Assert.True(File.Exists(Path.Combine(folder!, "install.sh")));
    }
}
