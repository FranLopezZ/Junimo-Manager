using System.IO.Compression;

/// <summary>
/// A zip whose entries carry Unix modes, so unzip / Ark / Dolphin give back an executable program and an executable install.sh.
/// .NET writes the modes into the external attributes, but on Windows it labels the archive as made by MS-DOS and Unix tools then ignore them,
/// so the "version made by" host of every central-directory entry is patched to Unix (3).
/// </summary>
public static class UnixZip
{
    const int ModeExecutable = 0x81ED; // regular file, rwxr-xr-x
    const int ModeFile = 0x81A4;       // regular file, rw-r--r--

    public static void Create(string folder, string zipPath, string rootName)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var file in Directory.GetFiles(folder).OrderBy(f => f, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(file);
                var executable = name == "JunimoManager" || name.EndsWith(".sh", StringComparison.Ordinal);
                var entry = zip.CreateEntry($"{rootName}/{name}", CompressionLevel.SmallestSize);
                entry.ExternalAttributes = unchecked((executable ? ModeExecutable : ModeFile) << 16);
                entry.LastWriteTime = File.GetLastWriteTime(file);
                using var input = File.OpenRead(file);
                using var output = entry.Open();
                input.CopyTo(output);
            }
        }
        MarkAsMadeOnUnix(zipPath);
    }

    static void MarkAsMadeOnUnix(string path)
    {
        var b = File.ReadAllBytes(path);
        var eocd = -1;
        for (var i = b.Length - 22; i >= Math.Max(0, b.Length - 22 - 65535); i--)
            if (b[i] == 0x50 && b[i + 1] == 0x4B && b[i + 2] == 5 && b[i + 3] == 6) { eocd = i; break; }
        if (eocd < 0) throw new InvalidDataException("zip end-of-central-directory record not found");

        var count = BitConverter.ToUInt16(b, eocd + 10);
        var pos = (int)BitConverter.ToUInt32(b, eocd + 16);
        for (var n = 0; n < count; n++)
        {
            if (BitConverter.ToUInt32(b, pos) != 0x02014B50) throw new InvalidDataException("bad central directory entry");
            b[pos + 5] = 3; // high byte of "version made by": 3 = Unix
            pos += 46 + BitConverter.ToUInt16(b, pos + 28) + BitConverter.ToUInt16(b, pos + 30) + BitConverter.ToUInt16(b, pos + 32);
        }
        File.WriteAllBytes(path, b);
    }
}
