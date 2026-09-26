using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DshShell;

/// <summary>
/// Owns the dsh web server child process. It probes the configured URL, starts
/// the configured launch command when nothing is listening, waits for
/// readiness, and on Stop() kills only the process tree it started — an
/// attached external server is left alone.
/// </summary>
public sealed partial class DshServerProcess : IDisposable
{
    /// <summary>How EnsureStartedAsync settled.</summary>
    public enum StartResultKind
    {
        /// <summary>The URL already answered; no process was started.</summary>
        AlreadyRunning,
        /// <summary>The launch command was started and the URL became reachable.</summary>
        Started,
        /// <summary>The command exited before readiness, timed out, or could not be started.</summary>
        Failed,
    }

    /// <summary>One EnsureStartedAsync outcome.</summary>
    public sealed record StartResult(StartResultKind Kind, string Message, int? ExitCode = null);

    /// <summary>
    /// Failure signatures a launched dsh most often exits with, each paired
    /// with the command that repairs the checkout. The signatures are the
    /// exceptions the server prints before Node exits, so they survive the
    /// truncated stack trace a failed boot leaves in the log.
    /// </summary>
    private static readonly (string Signature, string Remediation)[] KnownStartupFailures =
    {
        ("MissingClientBundleError", "the dsh client bundles are missing; run \"pnpm install\", \"pnpm run build:lib\", and \"pnpm run build:web\""),
        ("Cannot find module", "a dsh module cannot be resolved; run \"pnpm install\""),
        ("ERR_MODULE_NOT_FOUND", "a dsh module cannot be resolved; run \"pnpm install\""),
        ("EADDRINUSE", "the port is already held by another process; stop it or change the URL"),
        ("EACCES", "the server could not bind its port or write its data; check both"),
    };

    /// <summary>Server output lines kept for failure diagnosis; a crash banner plus stack trace fits well inside this.</summary>
    private const int RecentLineCapacity = 60;

    private readonly string _logPath;
    private readonly object _recentLock = new();
    private readonly Queue<string> _recentLines = new();
    private Process? _process;
    private bool _ownsProcess;
    private string? _launchDirectory;

    public DshServerProcess()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DshShell");
        Directory.CreateDirectory(dir);
        _logPath = Path.Combine(dir, "server.log");
    }

    /// <summary>Absolute path of the combined shell/server log file.</summary>
    public string LogPath => _logPath;

    /// <summary>
    /// The process-token URL the server prints on its readiness line
    /// (`dsh web: http://…?token=…`). From dsh 0.1.3-alpha.1 the Web GUI
    /// requires that token to mint the browser cookie, so the shell must
    /// navigate to this URL, not the bare one. Null until the server prints it.
    /// </summary>
    public string? AuthenticatedUrl { get; private set; }

    /// <summary>Whether this instance started the running server process.</summary>
    public bool OwnsProcess => _ownsProcess;

    /// <summary>Working directory of the last launch attempt, used to name the remediation a failure needs.</summary>
    public string? LaunchDirectory => _launchDirectory;

    /// <summary>
    /// Append a shell-side diagnostic line to the combined log, so WebView
    /// navigation, process failures, and recoveries land beside the server
    /// output they belong to.
    /// </summary>
    /// <param name="message">Line to record under the <c>shell</c> source.</param>
    public void LogShellMessage(string message) => Log(message);

    /// <summary>Raised on a background thread when the owned process exits.</summary>
    public event Action<int>? Exited;

    /// <summary>
    /// Whether the URL answers any HTTP request. A response of any status
    /// counts: the GUI server answers 404 for unclaimed routes only after the
    /// plugin tree is up, so any response means readiness.
    /// </summary>
    public static async Task<bool> IsReachableAsync(string url, CancellationToken ct = default)
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(2),
        };
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Start the configured command and wait until the URL answers. When the
    /// URL already answers, no process is started.
    /// </summary>
    /// <param name="settings">URL, command, working directory, and timeout.</param>
    /// <param name="ct">Cancellation aborts the readiness wait but leaves a
    /// started process running, so a closing window can still kill it via Stop().</param>
    public async Task<StartResult> EnsureStartedAsync(ShellSettings settings, CancellationToken ct)
    {
        if (await IsReachableAsync(settings.Url, ct))
        {
            return new StartResult(StartResultKind.AlreadyRunning, "URL already reachable");
        }

        var command = ResolveCommand(settings.Command);
        var cwd = ShellSettings.ResolveWorkingDirectory(settings.WorkingDirectory);
        if (!Directory.Exists(cwd))
        {
            return new StartResult(StartResultKind.Failed, $"working directory does not exist: {cwd}");
        }

        Log($"starting: {command} (cwd: {cwd})");
        _launchDirectory = cwd;
        // Diagnosis reads only this attempt's output; a line left from an
        // earlier failure would name the wrong repair.
        lock (_recentLock)
        {
            _recentLines.Clear();
        }
        var psi = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = $"/d /s /c \"{command}\"",
            WorkingDirectory = cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // pnpm on PATH is often a corepack shim. With no console to answer
        // "Do you want to continue? [Y/n]", the shim would hang forever when
        // the pinned pnpm version is not in its cache; this flag makes the
        // download proceed non-interactively.
        psi.Environment["COREPACK_ENABLE_DOWNLOAD_PROMPT"] = "0";
        // pnpm 10+ runs a dependency-status check before scripts and prompts
        // "The modules directories will be removed and reinstalled from
        // scratch. Proceed?" when the lockfile changed since the last install
        // (a git pull). confirmModulesPurge does NOT cover that check — the
        // actual knob is verify-deps-before-run; disable it so a configured
        // pnpm command cannot hang the no-console launch.
        psi.Environment["npm_config_verify_deps_before_run"] = "false";

        Process process;
        try
        {
            process = new Process
            {
                StartInfo = psi,
                EnableRaisingEvents = true,
            };
            process.OutputDataReceived += (_, e) => { if (e.Data is { } line) { CaptureAuthUrl(line); RememberLine(line); LogServerLine(line); } };
            process.ErrorDataReceived += (_, e) => { if (e.Data is { } line) { CaptureAuthUrl(line); RememberLine(line); LogServerLine(line); } };
            process.Exited += (_, _) =>
            {
                Log($"process exited (code {process.ExitCode})");
                Exited?.Invoke(process.ExitCode);
            };
            if (!process.Start())
            {
                return new StartResult(StartResultKind.Failed, "process could not be started");
            }
        }
        catch (Exception ex)
        {
            return new StartResult(StartResultKind.Failed, ex.Message);
        }

        _process = process;
        _ownsProcess = true;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(10, settings.StartTimeoutSeconds));
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                return new StartResult(
                    StartResultKind.Failed,
                    $"process exited (code {process.ExitCode}) before the server was ready",
                    process.ExitCode);
            }
            if (await IsReachableAsync(settings.Url, ct))
            {
                return new StartResult(StartResultKind.Started, "server ready");
            }
            await Task.Delay(400, ct);
        }

        return new StartResult(
            StartResultKind.Failed,
            $"server did not become reachable within {settings.StartTimeoutSeconds}s; process still running, see {_logPath}");
    }

    /// <summary>
    /// Kill the process tree this instance started. External servers this
    /// instance merely attached to are never touched.
    /// </summary>
    public void Stop()
    {
        var process = _process;
        if (process is null || !_ownsProcess || process.HasExited)
        {
            return;
        }
        Log("stopping server process tree");
        try
        {
            var killer = Process.Start(new ProcessStartInfo("taskkill.exe", $"/PID {process.Id} /T /F")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            killer?.WaitForExit(5000);
        }
        catch
        {
            // Best effort; the tree kill below is the fallback.
        }
        if (!process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The process already exited between the two checks.
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _process?.Dispose();
        _process = null;
    }

    /// <summary>
    /// Resolve the first token of the launch command through the PATH when it
    /// is a bare name (pnpm, dsh), so the shell does not depend on the current
    /// environment of its own parent process.
    /// </summary>
    private static string ResolveCommand(string command)
    {
        var trimmed = command.Trim();
        var match = FirstTokenRegex().Match(trimmed);
        if (!match.Success)
        {
            return trimmed;
        }
        var token = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
        if (token.Contains(Path.DirectorySeparatorChar) || token.Contains(Path.AltDirectorySeparatorChar))
        {
            return trimmed;
        }
        var resolved = FindOnPath(token);
        if (resolved is null)
        {
            return trimmed;
        }
        var rest = trimmed[match.Length..].TrimStart();
        var quoted = resolved.Contains(' ') ? $"\"{resolved}\"" : resolved;
        return rest.Length == 0 ? quoted : $"{quoted} {rest}";
    }

    private static string? FindOnPath(string name)
    {
        try
        {
            var psi = new ProcessStartInfo("where.exe", name)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }
            var first = process.StandardOutput.ReadLine();
            process.WaitForExit(5000);
            return string.IsNullOrWhiteSpace(first) ? null : first.Trim();
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex(@"^(""([^""]+)""|([^\s]+))")]
    private static partial Regex FirstTokenRegex();

    [GeneratedRegex(@"dsh web: (?<url>https?://\S+)")]
    private static partial Regex AuthUrlRegex();

    /// <summary>
    /// Capture the process-token URL from a server output line. The
    /// <c>dsh web: &lt;url&gt;</c> readiness line carries the launch token in
    /// its query, which the Web GUI needs to authenticate the first request.
    /// </summary>
    private void CaptureAuthUrl(string line)
    {
        if (AuthenticatedUrl is not null)
        {
            return;
        }
        var match = AuthUrlRegex().Match(line);
        if (match.Success)
        {
            AuthenticatedUrl = match.Groups["url"].Value;
        }
    }

    /// <summary>
    /// Keep the most recent server output for <see cref="DiagnoseStartupFailure"/>;
    /// the handlers run on pool threads, so the buffer is guarded.
    /// </summary>
    private void RememberLine(string line)
    {
        lock (_recentLock)
        {
            _recentLines.Enqueue(line);
            while (_recentLines.Count > RecentLineCapacity)
            {
                _recentLines.Dequeue();
            }
        }
    }

    /// <summary>
    /// Remediation for the most recent failed launch, picked from the output
    /// the server produced before it exited. A checkout whose client bundles
    /// were never rebuilt reports <c>MissingClientBundleError</c>, and one
    /// pulled without an install reports unresolved modules; the shell names
    /// the command that fixes whichever it saw.
    /// </summary>
    /// <returns>The remediation sentence naming the repository directory, or null when the output matched no known signature.</returns>
    public string? DiagnoseStartupFailure()
    {
        string[] lines;
        lock (_recentLock)
        {
            lines = _recentLines.ToArray();
        }
        foreach (var line in lines)
        {
            foreach (var (signature, remediation) in KnownStartupFailures)
            {
                if (line.Contains(signature, StringComparison.Ordinal))
                {
                    var directory = _launchDirectory ?? ShellSettings.ResolveWorkingDirectory(string.Empty);
                    return $"{remediation} in {directory}, then retry";
                }
            }
        }
        return null;
    }

    private void Log(string message) => LogLine("shell", message);

    private void LogServerLine(string line) => LogLine("server", line);

    private void LogLine(string source, string message)
    {
        try
        {
            File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] [{source}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never break the shell.
        }
    }
}
