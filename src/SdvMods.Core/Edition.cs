namespace SdvMods.Core;

/// <summary>
/// Which build this is. The same source produces two editions: the regular one (GitHub) and the one hosted on Nexus Mods
/// (<c>-p:NexusEdition=true</c>). Nexus scans every file it hosts, so the Nexus edition never downloads program code or other
/// authors' mods from outside Nexus: it only opens the relevant web page and lets the user download from there.
/// </summary>
public static class Edition
{
    public static bool IsNexus { get; internal set; } =
#if NEXUS_EDITION
        true;
#else
        false;
#endif

    /// <summary>Install other authors' mods from their GitHub releases, and download the SMAPI installer.</summary>
    public static bool DownloadsFromGitHub => !IsNexus;

    /// <summary>Download and install a newer version of this program from its GitHub releases.</summary>
    public static bool SelfUpdate => !IsNexus;

    /// <summary>This program's own page on Nexus Mods, where the Nexus edition is updated from.</summary>
    public const string NexusPageUrl = "https://www.nexusmods.com/stardewvalley/mods/54058";

    /// <summary>Official SMAPI page the Nexus edition opens instead of downloading the installer.</summary>
    public const string SmapiPageUrl = "https://smapi.io/";
}
