namespace ClaudeBlink;

/// <summary>Appends errors to %USERPROFILE%\.claude-blink\error.log (kept under ~1 MB).</summary>
static class Log
{
    public static string FilePath => Path.Combine(SessionFiles.BaseDir, "error.log");

    public static void Error(string where, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(SessionFiles.BaseDir);
            var fi = new FileInfo(FilePath);
            if (fi.Exists && fi.Length > 1_000_000) fi.Delete();
            File.AppendAllText(FilePath, $"{DateTime.Now:O} [{where}] {ex}{Environment.NewLine}");
        }
        catch { }
    }
}
