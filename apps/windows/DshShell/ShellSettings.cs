using System;
using System.IO;
using System.Text.Json;

namespace DshShell;

/// <summary>
/// Shell configuration persisted at %LocalAppData%\DshShell\settings.json.
/// The shell probes the URL first and only runs the launch command when
/// nothing is listening, so the same profile works for attach and spawn.
/// </summary>
public sealed class ShellSettings
{
    /// <summary>Default GUI address served by the shipped web profile.</summary>
    public const string DefaultUrl = "http://127.0.0.1:3080";

    /// <summary>
    /// Default launch command for a development checkout. This is exactly what
    /// the root package.json "dsh" script runs, plus --no-open (the server
    /// would otherwise open the default browser, which the shell replaces);
    /// it bypasses pnpm, whose deps-status check and corepack shim would hang
    /// a no-console spawn.
    /// </summary>
    public const string DefaultCommand = "node --import tsx/esm apps/cli/src/bin.ts web --no-open";

    /// <summary>URL of the dsh web GUI.</summary>
    public string Url { get; set; } = DefaultUrl;

    /// <summary>Command run when nothing listens on <see cref="Url"/>; empty disables spawning.</summary>
    public string Command { get; set; } = DefaultCommand;

    /// <summary>Working directory for the launch command; empty resolves the repository root.</summary>
    public string WorkingDirectory { get; set; } = string.Empty;

    /// <summary>How long to wait for the server to become reachable.</summary>
    public int StartTimeoutSeconds { get; set; } = 90;

    /// <summary>Per-user data directory (settings, log, WebView2 profile).</summary>
    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DshShell");

    private static string SettingsPath => Path.Combine(DataDir, "settings.json");

    /// <summary>Load persisted settings; a missing or unreadable file yields defaults.</summary>
    public static ShellSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return JsonSerializer.Deserialize<ShellSettings>(File.ReadAllText(SettingsPath)) ?? new ShellSettings();
            }
        }
        catch
        {
            // Corrupt settings fall back to defaults instead of blocking launch.
        }
        return new ShellSettings();
    }

    /// <summary>Persist the settings; a write failure is ignored (next launch keeps defaults).</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best effort; settings are re-editable in the shell.
        }
    }

    /// <summary>
    /// Resolve the effective working directory: the configured value when it
    /// exists, otherwise the nearest ancestor of the shell executable that
    /// holds pnpm-lock.yaml (the repository root), otherwise the current
    /// directory.
    /// </summary>
    public static string ResolveWorkingDirectory(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            return configured;
        }
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "pnpm-lock.yaml")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return Environment.CurrentDirectory;
    }
}
