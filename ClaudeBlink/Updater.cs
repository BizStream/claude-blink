using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace ClaudeBlink;

/// <summary>
/// Checks GitHub Releases for a newer version and downloads it in the background. The tray app applies a
/// downloaded update (restarting itself) once no session is active, so the light never drops out mid-work.
/// Does nothing when running from a dev build (not installed by Setup.exe) or before the repo URL is set.
/// </summary>
sealed class Updater
{
    static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30), CheckInterval = TimeSpan.FromHours(6);

    readonly UpdateManager? _mgr;
    DateTime _nextCheckUtc = DateTime.UtcNow + FirstCheckDelay;
    bool _busy;

    /// <summary>A downloaded update waiting to be applied, or null.</summary>
    public UpdateInfo? Ready { get; private set; }

    public string? LastError { get; private set; }

    public Updater()
    {
        if (RepoUrl is not { } url) return;
        try
        {
            var mgr = new UpdateManager(new GithubSource(url, accessToken: null, prerelease: false));
            if (mgr.IsInstalled) _mgr = mgr;
        }
        catch (Exception ex) { Log.Error("update-init", ex); }
    }

    public bool Enabled => _mgr is not null;

    /// <summary>Installed version, or the assembly version for a dev build.</summary>
    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "dev";

    static string? RepoUrl =>
        Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "RepositoryUrl")?.Value is { } u && !u.Contains("/OWNER/") ? u : null;

    /// <summary>Called from the poll timer; starts a background check when one is due.</summary>
    public void Tick()
    {
        if (DateTime.UtcNow >= _nextCheckUtc) _ = CheckAsync();
    }

    /// <summary>Check now (also used by the tray menu). Downloads the update if there is one.</summary>
    public async Task CheckAsync()
    {
        if (_mgr is null || _busy || Ready is not null) return;
        _busy = true;
        _nextCheckUtc = DateTime.UtcNow + CheckInterval;
        try
        {
            var info = await _mgr.CheckForUpdatesAsync();
            if (info is not null)
            {
                await _mgr.DownloadUpdatesAsync(info);
                Ready = info;
            }
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Error("update", ex);
        }
        finally { _busy = false; }
    }

    /// <summary>Forget a downloaded update that failed to apply.</summary>
    public void Discard() => Ready = null;

    /// <summary>Exits this process, applies the downloaded update and starts the new version.</summary>
    public void ApplyAndRestart()
    {
        if (_mgr is null || Ready is null) return;
        _mgr.ApplyUpdatesAndRestart(Ready);
    }
}
