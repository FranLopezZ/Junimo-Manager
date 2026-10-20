using System.Formats.Tar;
using System.IO.Compression;

namespace SdvMods.Core.Updates;

/// <summary>Unpacks a release package: .zip on Windows, .tar.gz on Linux.</summary>
public static class PackageExtractor
{
    /// <summary>Empties and (re)creates <paramref name="destination"/>, then unpacks into it. TarFile does not create the folder by itself, ZipFile does.</summary>
    public static void Extract(string archive, string destination)
    {
        if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        Directory.CreateDirectory(destination);
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archive, destination);
            return;
        }
        using var file = File.OpenRead(archive);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, destination, overwriteFiles: true);
    }

    /// <summary>The folder (inside the unpacked package) that holds the program file.</summary>
    public static string? FindProgramFolder(string root, string programFileName)
        => Directory.EnumerateFiles(root, programFileName, SearchOption.AllDirectories).FirstOrDefault() is { } f ? Path.GetDirectoryName(f) : null;
}
