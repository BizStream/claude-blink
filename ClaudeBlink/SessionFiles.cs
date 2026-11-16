using System.Text.Json.Nodes;

namespace ClaudeBlink;

/// <summary>What the hooks record for a session (the tray derives the displayed status from this).</summary>
enum SessionState { None = 0, Done, Working, Compacting, Waiting }

/// <summary>
/// One small JSON file per Claude session in %USERPROFILE%\.claude-blink\sessions, plus a
/// "&lt;id&gt;.agents" folder holding one marker file per running subagent.
/// Shared by the hook exe (writer) and the tray app (reader).
/// </summary>
static class SessionFiles
{
    // Deliberately NOT under AppData: the Claude desktop app is an MSIX package, and Windows silently redirects
    // AppData writes from its sessions (and their hooks) into a private per-package folder that the tray app
    // can't see. The profile root isn't redirected (it's why ~/.claude is shared by the desktop app and the CLI).
    public static string BaseDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude-blink");
    public static string Dir => Path.Combine(BaseDir, "sessions");

    /// <summary>
    /// Read-modify-write a session file. <paramref name="decide"/> gets the current state and returns
    /// the new one (or null to leave the file alone).
    /// </summary>
    /// <param name="eventUtc">When the hook was launched. Async hooks can finish out of order,
    /// so the update is skipped if the file already holds a newer event.</param>
    public static void Update(string sessionId, DateTime eventUtc, string eventName, string? cwd, string? transcriptPath,
        Func<SessionState, JsonObject?, SessionState?> decide)
    {
        Directory.CreateDirectory(Dir);
        string path = FileFor(sessionId);
        JsonObject? existing = null;
        try { if (File.Exists(path)) existing = JsonNode.Parse(File.ReadAllText(path)) as JsonObject; } catch { }

        if (TryTime(existing?["updatedUtc"]) is { } prevUpdated && prevUpdated > eventUtc) return;

        var prev = ParseState(existing?["state"]);
        if (decide(prev, existing) is not { } next) return;

        // "since" = when the session entered its current state.
        string since = eventUtc.ToString("O");
        if (prev == next && (string?)existing?["sinceUtc"] is { } keep) since = keep;
        else if (prev == SessionState.Compacting && ParseState(existing?["prevState"]) == next && (string?)existing?["prevSince"] is { } back) since = back;

        var obj = new JsonObject
        {
            ["state"] = next.ToString().ToLowerInvariant(),
            ["sinceUtc"] = since,
            ["updatedUtc"] = eventUtc.ToString("O"),
            ["event"] = eventName,
            ["cwd"] = cwd ?? (string?)existing?["cwd"],
            ["transcriptPath"] = transcriptPath ?? (string?)existing?["transcriptPath"],
        };
        if (next == SessionState.Compacting)
        {
            // Remember what to go back to when compaction finishes.
            bool already = prev == SessionState.Compacting;
            obj["prevState"] = already ? (string?)existing?["prevState"] : prev.ToString().ToLowerInvariant();
            obj["prevSince"] = already ? (string?)existing?["prevSince"] : (string?)existing?["sinceUtc"];
        }

        string tmp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(tmp, obj.ToJsonString());
        File.Move(tmp, path, overwrite: true);
    }

    public static void Delete(string sessionId)
    {
        try { File.Delete(FileFor(sessionId)); } catch { }
        try { Directory.Delete(AgentsDirFor(sessionId), recursive: true); } catch { }
    }

    // ---- subagent markers ----

    public static void AddAgent(string sessionId, string agentId)
    {
        var dir = AgentsDirFor(sessionId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, Safe(agentId)), "");
    }

    public static void RemoveAgent(string sessionId, string agentId)
    {
        try { File.Delete(Path.Combine(AgentsDirFor(sessionId), Safe(agentId))); } catch { }
    }

    /// <summary>Marks a subagent as still alive (only if it's already known).</summary>
    public static void TouchAgent(string sessionId, string agentId)
    {
        try { File.SetLastWriteTimeUtc(Path.Combine(AgentsDirFor(sessionId), Safe(agentId)), DateTime.UtcNow); } catch { }
    }

    public static string AgentsDirFor(string sessionId) => Path.Combine(Dir, Safe(sessionId) + ".agents");

    // ---- helpers ----

    public static SessionState ParseState(JsonNode? n) =>
        Enum.TryParse<SessionState>((string?)n, true, out var s) ? s : SessionState.None;

    public static DateTime? TryTime(JsonNode? n) =>
        DateTime.TryParse((string?)n, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t.ToUniversalTime() : null;

    static string FileFor(string sessionId) => Path.Combine(Dir, Safe(sessionId) + ".json");

    static string Safe(string id) => new(id.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());
}
