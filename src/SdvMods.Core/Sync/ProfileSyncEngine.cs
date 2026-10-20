using SdvMods.Core.Profiles;

namespace SdvMods.Core.Sync;

public enum SyncState
{
    UpToDate,
    /// <summary>Changed here only: goes up.</summary>
    Push,
    /// <summary>Changed elsewhere only (or new there): comes down.</summary>
    Pull,
    /// <summary>Changed both here and elsewhere since the last sync: needs a decision.</summary>
    Conflict,
    /// <summary>This profile is switched off for sync.</summary>
    LocalOnly,
}

public enum SyncDirection { Both, PushOnly, PullOnly }
public enum ConflictChoice { KeepLocal, KeepRemote }

public sealed record SyncItem(string Id, string Name, SyncState State, bool IsNew, RemoteProfile? Remote);

public sealed class SyncPlan
{
    public IReadOnlyList<SyncItem> Items { get; init; } = Array.Empty<SyncItem>();
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.Now;
    public int Count(SyncState s) => Items.Count(i => i.State == s);
    public SyncItem? Find(string id) => Items.FirstOrDefault(i => i.Id == id);
}

public sealed class SyncReport
{
    public List<string> Pushed { get; } = new();
    public List<string> Pulled { get; } = new();
    public List<string> Conflicts { get; } = new();
}

/// <summary>
/// Profile-by-profile synchronisation with a conflict check. Every local edit gives the profile a new revision; the revision last
/// agreed with the shared copy is remembered (BaseRev). Comparing local, shared and agreed revisions tells which side changed:
/// only here → upload, only there → download, both → conflict (nothing is overwritten until the user decides).
/// </summary>
public sealed class ProfileSyncEngine
{
    readonly ProfileService _local;
    readonly ProfileConfigStore _configs;
    readonly string _device;

    public ProfileSyncEngine(ProfileService local, ProfileConfigStore configs, string device)
    {
        _local = local; _configs = configs; _device = device;
    }

    /// <summary>What is waiting to go up, known without any network access.</summary>
    public int PendingLocalChanges()
        => _local.Document.Profiles.Count(p => p.SyncEnabled && (p.BaseRev is null ? !p.Deleted : p.Rev != p.BaseRev));

    string LocalHash(Profile p) => RemoteProfile.Hash(p.Name, p.Deleted, p.Mods, p.Deleted ? new Dictionary<string, string>() : _configs.ReadAll(p));

    public async Task<SyncPlan> CheckAsync(IRemoteProfileStore remote, CancellationToken ct = default)
    {
        var remoteById = (await remote.ReadAsync(ct)).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.UpdatedAt).First());
        var items = new List<SyncItem>();
        var adopted = false;

        foreach (var p in _local.Document.Profiles.ToList())
        {
            remoteById.TryGetValue(p.Id, out var r);
            if (!p.SyncEnabled) { items.Add(new(p.Id, p.Name, SyncState.LocalOnly, false, r)); continue; }
            if (r is null)
            {
                if (!(p.Deleted && p.BaseRev is null)) items.Add(new(p.Id, p.Name, SyncState.Push, true, null)); // a never-shared deleted profile needs nothing
                continue;
            }

            SyncState state;
            if (p.BaseRev is not null && p.BaseRev == r.Rev)
                state = p.Rev == p.BaseRev ? SyncState.UpToDate : SyncState.Push;
            else if (LocalHash(p) == r.ContentHash())
            {
                // Both sides ended up with the same thing (e.g. the same edit on two devices): just agree on the revision.
                p.Rev = r.Rev; p.BaseRev = r.Rev;
                adopted = true;
                state = SyncState.UpToDate;
            }
            else if (r.Legacy && p.BaseRev is null)
                state = p.UpdatedAt > r.UpdatedAt ? SyncState.Push : SyncState.Pull;   // old format had no revisions: the newer date wins once
            else if (p.BaseRev is not null && p.Rev == p.BaseRev)
                state = SyncState.Pull;
            else
                state = SyncState.Conflict;
            items.Add(new(p.Id, r.Name.Length > 0 ? r.Name : p.Name, state, false, r));
        }

        var known = _local.Document.Profiles.Select(p => p.Id).ToHashSet();
        foreach (var r in remoteById.Values.Where(r => !known.Contains(r.Id) && !r.Deleted))
            items.Add(new(r.Id, r.Name, SyncState.Pull, true, r));

        if (adopted) _local.Save();
        return new SyncPlan { Items = items };
    }

    public async Task<SyncReport> ApplyAsync(SyncPlan plan, IRemoteProfileStore remote, SyncDirection direction = SyncDirection.Both, CancellationToken ct = default)
    {
        var report = new SyncReport();
        var toPush = new List<Profile>();
        foreach (var item in plan.Items)
        {
            switch (item.State)
            {
                case SyncState.Pull when direction != SyncDirection.PushOnly && item.Remote is not null:
                    ApplyRemote(item.Remote);
                    report.Pulled.Add(item.Id);
                    break;
                case SyncState.Push when direction != SyncDirection.PullOnly && _local.Document.Profiles.FirstOrDefault(p => p.Id == item.Id) is { } p:
                    toPush.Add(p);
                    break;
                case SyncState.Conflict:
                    report.Conflicts.Add(item.Id);
                    break;
            }
        }
        if (toPush.Count > 0)
        {
            await remote.WriteAsync(toPush.Select(p => RemoteProfile.From(p, p.Deleted ? new Dictionary<string, string>() : _configs.ReadAll(p), _device)).ToList(), ct);
            foreach (var p in toPush) { p.BaseRev = p.Rev; report.Pushed.Add(p.Id); }
        }
        if (report.Pulled.Count > 0 || toPush.Count > 0) _local.Save();
        return report;
    }

    /// <summary>Settles a conflict. Returns the id of the extra local copy created when keeping the remote one (otherwise null).</summary>
    public async Task<string?> ResolveAsync(string profileId, ConflictChoice choice, IRemoteProfileStore remote, CancellationToken ct = default)
    {
        var p = _local.Document.Profiles.FirstOrDefault(x => x.Id == profileId) ?? throw new InvalidOperationException("Unknown profile.");
        var r = (await remote.ReadAsync(ct)).Where(x => x.Id == profileId).OrderByDescending(x => x.UpdatedAt).FirstOrDefault()
                ?? throw new InvalidOperationException("The shared copy of this profile is gone.");

        if (choice == ConflictChoice.KeepLocal)
        {
            p.BaseRev = r.Rev;                                  // we have seen the remote one; ours replaces it
            await ApplyAsync(new SyncPlan { Items = new[] { new SyncItem(p.Id, p.Name, SyncState.Push, false, r) } }, remote, SyncDirection.PushOnly, ct);
            return null;
        }

        // Keep the remote version here, but do not lose the local one: it becomes a separate profile.
        string? copyId = null;
        if (!p.Deleted)
        {
            var copy = new Profile
            {
                Name = p.Name + " (local)", Mods = p.Mods.Select(m => new ProfileMod { UniqueID = m.UniqueID, Name = m.Name, Version = m.Version, Enabled = m.Enabled, UpdateKeys = m.UpdateKeys.ToList() }).ToList(),
            };
            _local.Add(copy);
            _configs.Copy(p.Id, copy.Id);
            copyId = copy.Id;
        }
        ApplyRemote(r);
        _local.Save();
        return copyId;
    }

    void ApplyRemote(RemoteProfile r)
    {
        var p = _local.Document.Profiles.FirstOrDefault(x => x.Id == r.Id);
        if (p is null) { p = new Profile { Id = r.Id }; _local.Document.Profiles.Add(p); }
        p.Name = r.Name;
        p.Deleted = r.Deleted;
        p.Mods = r.Deleted ? new() : r.Mods.Select(m => new ProfileMod { UniqueID = m.UniqueID, Name = m.Name, Version = m.Version, Enabled = m.Enabled, UpdateKeys = m.UpdateKeys.ToList() }).ToList();
        p.UpdatedAt = r.UpdatedAt;
        p.Rev = r.Rev.StartsWith("legacy-", StringComparison.Ordinal) ? Guid.NewGuid().ToString("N") : r.Rev;   // legacy copies get their first real revision on the next upload
        p.BaseRev = r.Legacy ? null : r.Rev;
        if (r.Deleted) _configs.Delete(p.Id); else if (!r.Legacy) _configs.Replace(p.Id, r.Configs);
        _local.Save();
    }
}
