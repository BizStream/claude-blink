using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeBlink;

sealed record Session(string Id, SessionState State, Status Status, DateTime LastActivityUtc, DateTime SinceUtc, int Subagents, string? Cwd);

/// <summary>
/// Reads the session files (see SessionFiles) and works out each session's displayed status.
/// </summary>
static class SessionStore
{
    static readonly TimeSpan CompactingTimeout = TimeSpan.FromMinutes(3);

    // transcript path -> (last write time, ended with a user interrupt?)
    static readonly Dictionary<string, (DateTime Mtime, bool Interrupted)> InterruptCache = new();

    public static List<Session> LoadAll(Config cfg)
    {
        var result = new List<Session>();
        if (!Directory.Exists(SessionFiles.Dir)) return result;
        var now = DateTime.UtcNow;
        var stale = TimeSpan.FromMinutes(cfg.StaleWorkingMinutes);

        foreach (var file in Directory.EnumerateFiles(SessionFiles.Dir, "*.json"))
        {
            try
            {
                string id = Path.GetFileNameWithoutExtension(file);
                var node = JsonNode.Parse(File.ReadAllText(file))!;
                var state = SessionFiles.ParseState(node["state"]);
                var updated = SessionFiles.TryTime(node["updatedUtc"]) ?? File.GetLastWriteTimeUtc(file);
                var since = SessionFiles.TryTime(node["sinceUtc"]) ?? updated;
                string? cwd = (string?)node["cwd"];
                string? transcript = (string?)node["transcriptPath"];

                DateTime? tMtime = transcript is not null && File.Exists(transcript) ? File.GetLastWriteTimeUtc(transcript) : null;
                DateTime last = tMtime is { } m && m > updated ? m : updated;
                bool recordedDone = state == SessionState.Done;

                // Pressing Esc doesn't fire a Stop hook; detect it from the transcript instead.
                // (Esc also stops foreground subagents.)
                if (state is SessionState.Working or SessionState.Waiting or SessionState.Compacting
                    && tMtime is { } tm && tm > updated.AddSeconds(1)
                    && EndsWithInterrupt(transcript!, tm))
                {
                    state = SessionState.Done;
                    try { Directory.Delete(SessionFiles.AgentsDirFor(id), true); } catch { }
                }

                // Compaction takes seconds. If PreCompact was never followed by PostCompact (aborted/failed compaction),
                // give up quickly. Measured from the recorded event only: transcript writes (e.g. app metadata
                // lines) must not keep a dead "compacting" alive.
                if (state == SessionState.Compacting && now - updated > CompactingTimeout)
                    state = SessionState.Done;

                int agents = LiveSubagents(id, stale, now);

                // Killed/crashed sessions never send Stop or SessionEnd.
                if (state is SessionState.Working or SessionState.Compacting && agents == 0 && now - last > stale)
                    state = SessionState.Done;

                if (state == SessionState.Done && agents == 0 && now - last > TimeSpan.FromHours(cfg.DoneExpireHours))
                {
                    SessionFiles.Delete(id);
                    continue;
                }

                Status status = state switch
                {
                    SessionState.Waiting => cfg.IsOn(Status.LongWait) && now - since > TimeSpan.FromMinutes(cfg.LongWaitMinutes)
                        ? Status.LongWait : Status.NeedsInput,
                    SessionState.Compacting => cfg.IsOn(Status.Compacting) ? Status.Compacting : Status.Running,
                    SessionState.Working => agents > 0 && cfg.IsOn(Status.Subagents) ? Status.Subagents : Status.Running,
                    SessionState.Done when agents > 0 => cfg.IsOn(Status.Subagents) ? Status.Subagents : Status.Running,
                    SessionState.Done => recordedDone && cfg.IsOn(Status.JustFinished) && now - since < TimeSpan.FromSeconds(cfg.JustFinishedSeconds)
                        ? Status.JustFinished : Status.Done,
                    _ => Status.Idle,
                };

                if (state != SessionState.None)
                    result.Add(new Session(id, state, status, last, since, agents, cwd));
            }
            catch { /* half-written or corrupt file; skip this tick */ }
        }
        return result;
    }

    /// <summary>Counts subagent markers, dropping ones with no activity for too long (missed SubagentStop).</summary>
    static int LiveSubagents(string id, TimeSpan stale, DateTime now)
    {
        var dir = SessionFiles.AgentsDirFor(id);
        if (!Directory.Exists(dir)) return 0;
        int n = 0;
        foreach (var f in Directory.EnumerateFiles(dir))
        {
            if (now - File.GetLastWriteTimeUtc(f) > stale) { try { File.Delete(f); } catch { } }
            else n++;
        }
        return n;
    }

    static bool EndsWithInterrupt(string transcript, DateTime mtime)
    {
        if (InterruptCache.TryGetValue(transcript, out var c) && c.Mtime == mtime) return c.Interrupted;
        bool interrupted = false;
        try
        {
            using var fs = new FileStream(transcript, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long len = Math.Min(fs.Length, 64 * 1024);
            fs.Seek(-len, SeekOrigin.End);
            var buf = new byte[len];
            fs.ReadExactly(buf);
            var lines = Encoding.UTF8.GetString(buf).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            // Skip trailing metadata lines (last-prompt, custom-title, ...) and look at the last real message.
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                if (!lines[i].Contains("\"message\":{")) continue;
                interrupted = lines[i].Contains("[Request interrupted by user");
                break;
            }
        }
        catch { }
        InterruptCache[transcript] = (mtime, interrupted);
        return interrupted;
    }

    public static void Clear(Func<Session, bool> predicate, Config cfg)
    {
        foreach (var s in LoadAll(cfg).Where(predicate))
            SessionFiles.Delete(s.Id);
    }
}
