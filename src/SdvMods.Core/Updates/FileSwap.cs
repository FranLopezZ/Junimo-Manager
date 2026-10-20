namespace SdvMods.Core.Updates;

public static class FileSwap
{
    /// <summary>
    /// Puts <paramref name="src"/> in place of <paramref name="dst"/> even when dst is the running program:
    /// Windows can rename a running exe (the leftover ".old" is deleted on the next start); Linux replaces it by renaming a new file over it.
    /// </summary>
    public static void Replace(string src, string dst, bool isExe)
    {
        if (OperatingSystem.IsWindows())
        {
            var old = dst + ".old";
            if (File.Exists(dst)) File.Move(dst, old, overwrite: true);
            try { File.Copy(src, dst, overwrite: true); }
            catch { if (File.Exists(old) && !File.Exists(dst)) File.Move(old, dst); throw; }
        }
        else
        {
            var tmp = dst + ".new";
            File.Copy(src, tmp, overwrite: true);
            if (isExe)
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.Move(tmp, dst, overwrite: true);
        }
    }
}
