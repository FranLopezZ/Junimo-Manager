using System.Security.Cryptography;
using System.Text;

namespace SdvMods.Core.Settings;

/// <summary>Protects API keys / tokens stored in settings.json: Windows DPAPI (tied to the Windows user); on Linux the file is chmod 600.</summary>
public static class Secrets
{
    const string Dpapi = "dpapi:";
    const string Plain = "plain:";

    public static string? Protect(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return null;
        if (OperatingSystem.IsWindows())
        {
#pragma warning disable CA1416
            var enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser);
#pragma warning restore CA1416
            return Dpapi + Convert.ToBase64String(enc);
        }
        return Plain + Convert.ToBase64String(Encoding.UTF8.GetBytes(secret));
    }

    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        try
        {
            if (stored.StartsWith(Plain)) return Encoding.UTF8.GetString(Convert.FromBase64String(stored[Plain.Length..]));
            if (stored.StartsWith(Dpapi) && OperatingSystem.IsWindows())
            {
#pragma warning disable CA1416
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored[Dpapi.Length..]), null, DataProtectionScope.CurrentUser));
#pragma warning restore CA1416
            }
        }
        catch (Exception e) when (e is CryptographicException or FormatException) { }
        return null; // e.g. settings copied from another PC/user: the secret has to be entered again
    }
}
