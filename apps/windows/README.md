# DeepSeek Harness Windows Shell

English | [中文](README.zh.md)

A WinUI 3 (Windows App SDK) desktop shell that hosts the dsh Web GUI in a
WebView2 control, so the harness launches like a normal application instead of
from a terminal.

## How it works

The shell embeds the existing `dsh web` server unchanged: on launch it probes
the configured URL, starts the configured launch command when nothing is
listening, waits until the URL answers, and loads it in WebView2. The page is
always served by `dsh web` — the only source that injects
`window.__DSH_BOOT__` — so no protocol, bundle, or trust-fence change is
needed. Since dsh 0.1.3-alpha.1 `dsh web` requires a per-boot process token
printed on its readiness line (`dsh web: http://…/?token=…`) to mint the
browser cookie; when the shell spawns the server it captures that token and
navigates to the authenticated URL. Attaching to an externally started server
relies on the cookie a previous spawn left in the persistent WebView2 profile.
Closing the window kills only the process tree the shell started; an
externally running server is left alone.

A WebView2 renderer can stop painting and stop answering input while the rest
of the window keeps working, which leaves a window that looks alive but
ignores every click. The shell therefore asks the loaded page for an answer
every 10 seconds; a page that misses three answers in a row is rebuilt from a
fresh WebView2 control, and a WebView process failure reloads the page first
and rebuilds it when the reload does not bring it back. A rebuild loads the
server URL again without touching the server process, so it never costs the
sessions the running server holds; the "Rebuild view" button (Ctrl+Shift+R)
does the same on demand. Every navigation, process failure, and rebuild is
recorded in the log.

## Prerequisites

- Windows 10 22H2 or Windows 11 with the [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (built in on Windows 11).
- .NET SDK 10 (the project targets `net10.0-windows10.0.26100.0`).
- A Visual Studio install (17/18 or Build Tools) with the MSBuild AppxPackage
  tooling — used for `resources.pri` generation — or the build fails loading
  `Microsoft.Build.Packaging.Pri.Tasks.dll`.
- The dsh repository built at least once: `pnpm install` and `pnpm run build`
  (the frontend `dist/` is required by `dsh web`).

## Build and run

```sh
pnpm shell:windows          # build (if needed) and launch the shell
# or
dotnet build apps/windows/DshShell
# publish a standalone copy:
pnpm shell:windows:publish  # outputs dist-exe/dsh-shell
```

The first launch spawns the `dsh` script's command directly —
`node --import tsx/esm apps/cli/src/bin.ts web --no-open` — in the repository
root (found by walking up from the executable to `pnpm-lock.yaml`), waits for
`http://127.0.0.1:3080`, and attaches the window. The default bypasses pnpm
because pnpm's dependency-status check and corepack shim both prompt on a
no-console spawn, and passes `--no-open` because the shell replaces the
browser. A second instance attaches to the same server; the single-instance
mutex prevents two windows from fighting over one server process.

## Configuration

The gear button opens the server settings; they persist to
`%LocalAppData%\DshShell\settings.json`:

| Key | Default | Meaning |
|---|---|---|
| `Url` | `http://127.0.0.1:3080` | GUI address to probe and load |
| `Command` | `node --import tsx/esm apps/cli/src/bin.ts web --no-open` | Run when nothing listens on `Url`; empty disables spawning |
| `WorkingDirectory` | auto | Working directory for the command; empty auto-detects the repository root |
| `StartTimeoutSeconds` | `90` | How long to wait for the server to become reachable |

The shell and server output both append to `%LocalAppData%\DshShell\server.log`
(the "Open log" button appears on failure states).

## Known limitations

- The spawned server runs non-interactively: the shell sets
  `COREPACK_ENABLE_DOWNLOAD_PROMPT=0` and `npm_config_verify_deps_before_run=false`,
  so a configured pnpm/corepack command can never hang the launch on a prompt.
- The shell neither installs nor builds the harness. After a `git pull`, run
  `pnpm install`, `pnpm run build:lib`, and `pnpm run build:web` in the
  repository before relaunching. Both lib faces are required: the host pass
  emits the Typert declarations (`lib/typert.host.d.ts`,
  `lib/typert.remote-client.d.ts`) that the client typecheck consumes, so
  `pnpm run build:lib:client` alone fails with unresolved `/remote` modules.
  A pull that deleted a package also leaves that package's `lib/` output
  behind — the bundling pass still reads a directory whose `package.json` is
  gone — and fails with `MISSING_EXPORT`; delete those manifest-less package
  directories before the rebuild (`pnpm run clean` is the tool for it, but on
  dsh 0.1.7-rc.2 it aborts on `lib/desktop-keyboard-test-types` from
  `tsconfig.desktop-keyboard-tests.json` before removing anything). When the
  spawn fails for either reason the status bar shows the exit code, the dialog
  names the command that repairs the checkout, and `server.log` holds the full
  output.
- `dotnet build` resolves the Pri-generation MSBuild tasks from a Visual
  Studio install via the `AppxMSBuildToolsPath` fallback in `DshShell.csproj`
  (VS 18 Community and VS 2022 Build Tools paths); a machine without either
  needs `-p:AppxMSBuildToolsPath=...` pointing at its AppxPackage task
  directory. Building with the Visual Studio `MSBuild.exe` needs no fallback.
- Unpackaged and self-contained in the Windows App SDK runtime
  (`WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`), so no MSIX
  packaging or runtime installer is needed; the output folder is large
  (~100 MB) because of the bundled WinAppSDK runtime.
