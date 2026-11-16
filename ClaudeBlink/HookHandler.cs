using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeBlink;

/// <summary>
/// ClaudeBlinkHook.exe — invoked by Claude Code for each hook event with the event JSON on stdin.
/// Records the session's state and exits immediately. Never fails the hook.
/// </summary>
static class HookHandler
{
    public static int Run()
    {
        var eventUtc = DateTime.UtcNow;
        try
        {
            using var stdin = Console.OpenStandardInput();
            using var doc = JsonDocument.Parse(stdin);
            var root = doc.RootElement;

            string ev = Str(root, "hook_event_name") ?? "";
            string? sid = Str(root, "session_id");
            if (string.IsNullOrEmpty(sid)) return 0;
            string? agentId = Str(root, "agent_id"); // set when the event comes from inside a subagent

            void Set(Func<SessionState, JsonObject?, SessionState?> decide) =>
                SessionFiles.Update(sid, eventUtc, ev, Str(root, "cwd"), Str(root, "transcript_path"), decide);
            void SetTo(SessionState? s) { if (s is { } v) Set((_, _) => v); }

            switch (ev)
            {
                case "SessionEnd":
                    SessionFiles.Delete(sid);
                    break;

                case "SubagentStart":
                    if (agentId is not null) SessionFiles.AddAgent(sid, agentId);
                    Set((cur, _) => cur == SessionState.None ? SessionState.Working : null);
                    break;

                case "SubagentStop":
                    if (agentId is not null) SessionFiles.RemoveAgent(sid, agentId);
                    break;

                case "UserPromptSubmit":
                case "PermissionDenied":
                    SetTo(SessionState.Working);
                    break;

                case "PostToolUse":
                case "PostToolUseFailure":
                    if (agentId is null)
                        SetTo(SessionState.Working);
                    else
                    {
                        // A subagent's tool call: keep it alive, and clear a permission wait it caused,
                        // but don't flip a finished parent back to "running".
                        SessionFiles.TouchAgent(sid, agentId);
                        Set((cur, _) => cur == SessionState.Waiting ? SessionState.Working : null);
                    }
                    break;

                case "PreToolUse":
                    SetTo(Str(root, "tool_name") is "AskUserQuestion" or "ExitPlanMode" ? SessionState.Waiting : null);
                    break;

                case "PermissionRequest":
                    SetTo(SessionState.Waiting);
                    break;

                case "Notification":
                    SetTo(NotificationState(root));
                    break;

                case "Stop":
                case "StopFailure":
                    SetTo(SessionState.Done);
                    break;

                case "PreCompact":
                    SetTo(SessionState.Compacting);
                    break;

                case "PostCompact":
                case "SessionStart" when Str(root, "source") == "compact":
                    Set((cur, file) => cur == SessionState.Compacting
                        ? SessionFiles.ParseState(file?["prevState"]) is var p && p != SessionState.None && p != SessionState.Compacting ? p : SessionState.Done
                        : null);
                    break;
            }
        }
        catch { /* never block or fail Claude */ }
        return 0;
    }

    static SessionState? NotificationState(JsonElement root)
    {
        switch (Str(root, "notification_type"))
        {
            case "permission_prompt":
            case "elicitation_dialog":
            case "elicitation_url_dialog":
            case "agent_needs_input":
                return SessionState.Waiting;
            case null:
                var msg = Str(root, "message") ?? "";
                return msg.Contains("permission", StringComparison.OrdinalIgnoreCase) ? SessionState.Waiting : null;
            default: // idle_prompt etc.: "done" is already recorded by Stop
                return null;
        }
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
