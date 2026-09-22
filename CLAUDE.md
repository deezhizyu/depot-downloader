# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Fork notice

This is a modified fork of [SteamRE/DepotDownloader](https://github.com/SteamRE/DepotDownloader) (GPL-2.0),
not the official SteamRE build. Fork-specific changes are tracked in [CHANGES.md](CHANGES.md) (required by
GPL-2.0 section 2(a)) — update it when adding a new deviation from upstream. The main fork addition is
`-json` machine-readable output mode, documented in [docs/json-mode.md](docs/json-mode.md).

## Build and run

Single project, no test suite (`DepotDownloader.sln` → `DepotDownloader/DepotDownloader.csproj`, .NET 9,
targets `net9.0`).

```powershell
dotnet build
dotnet run --project DepotDownloader -- -app <id> -depot <id>
dotnet publish DepotDownloader/DepotDownloader.csproj -c Release -o artifacts
```

CI (`.github/workflows/build.yml`) builds Debug+Release on Windows/Linux/macOS with
`/p:ContinuousIntegrationBuild=true`, which turns on `TreatWarningsAsErrors` — fix warnings, don't
suppress them. `EnforceCodeStyleInBuild` is also on, so `.editorconfig` naming/style rules
(PascalCase non-private statics/consts, camelCase locals/instance fields, `System` usings sorted
first) are enforced at build time, not just by an IDE.

There are no automated tests in this repo; verify changes by running the built binary against a
real (or anonymous-account) app/depot.

## Architecture

Everything lives in `DepotDownloader/`, one flat namespace (`DepotDownloader`), no subfolders. Rough
call flow: `Program.Main` parses CLI args → builds a `DownloadConfig` (`ContentDownloader.Config`) →
creates a `Steam3Session` (login) → `ContentDownloader.DownloadAppAsync`/`DownloadPubfileAsync`/etc.
drive the actual depot download using a `CDNClientPool`.

- **Program.cs** — entry point and all CLI argument parsing (`-app`, `-depot`, `-username`, ... see
  README's Parameters tables). `Main` wraps `MainCore` to optionally enable `-json` output before any
  other code runs, and to guarantee a final `done`/`error` JSON event on the way out.
- **Steam3Session.cs** — owns the `SteamClient`/`SteamUser`/`SteamApps`/`SteamContent` handlers,
  login (password, QR, 2FA/Steam Guard via `ConsoleAuthenticator`), license/app-info/depot-key
  fetching, and reconnect/backoff logic.
- **CDNClientPool.cs** — pool of CDN `Server` endpoints and a shared `SteamKit2.CDN.Client`, refreshed
  via `steamSession.steamContent.GetServersForSteamPipe()`.
- **ContentDownloader.cs** (largest file) — the actual download pipeline: resolves apps/depots/
  manifests/branches, plans what needs downloading (dedup, `-filelist`, `-validate`), downloads and
  decompresses chunks, verifies against the manifest, writes files, and updates
  `DepotConfigStore` (installed manifest IDs) when done.
- **DepotConfigStore.cs** / **AccountSettingsStore.cs** — small protobuf-serialized (`protobuf-net`)
  state files persisted next to the install dir / next to the executable (`account.config`) — installed
  manifest IDs and remembered login keys/passwords, respectively.
- **ProtoManifest.cs** — protobuf model for Steam depot manifests (files, chunks, hashes) plus
  load/save helpers for the locally cached `.manifest` files.
- **DownloadConfig.cs** — plain options bag populated from parsed CLI args, read throughout
  `ContentDownloader`.
- **DownloadCounters.cs** — mutable byte/file counters (network/written/verified/files_done) shared
  between `ContentDownloader` and `JsonOutput` to drive both the human progress line and the `-json`
  `progress` events; see the counter semantics documented in `docs/json-mode.md`.
- **JsonOutput.cs** — the fork's `-json` mode: converts internal events into the JSON Lines schema
  documented in [docs/json-mode.md](docs/json-mode.md) (`log`, `auth_prompt`, `plan`, `depot_start`,
  `progress`, `done`, `error`, etc.). When `JsonOutput.Enabled`, normal `Console.Write*`/progress/ANSI
  output is suppressed in favor of these events — check `JsonOutput.Enabled` before adding new
  human-readable console output anywhere in the pipeline.
- **Ansi.cs** / **AnsiDetector.cs** — terminal capability detection and ANSI/OSC escape sequences used
  for the human-readable progress UI (spinners, percentage lines); bypassed entirely in `-json` mode.
- **ConsoleAuthenticator.cs** — `SteamKit2.Authentication.IAuthenticator` implementation; prompts for
  2FA/email codes/device confirmation on stdin, or emits `auth_prompt` JSON events instead when
  `-json` is enabled.
- **Util.cs** — misc helpers (hashing, file path validation/symlink checks, byte formatting).
- **HttpClientFactory.cs** / **HttpDiagnosticEventListener.cs** — `HttpClient` construction for
  SteamKit2 and optional low-level HTTP diagnostics under `-debug`.
- **PlatformUtilities.cs** — Windows-specific console/launch helpers (uses CsWin32-generated
  P/Invoke bindings via `NativeMethods.txt`).

### The `-json` mode invariant

Any code path that writes to stdout for human consumption must have a `-json`-aware counterpart (a
`JsonOutput.*` call) instead, gated on `JsonOutput.Enabled`. Behavior without `-json` must stay
byte-for-byte identical to upstream — see the guarantee stated in `docs/json-mode.md` and
`CHANGES.md`. When touching `Program.cs`, `ContentDownloader.cs`, `Steam3Session.cs`, or
`ConsoleAuthenticator.cs`, check whether the change needs a mirrored update in `JsonOutput.cs` /
`DownloadCounters.cs` and in `docs/json-mode.md`.

## Dependencies

- **SteamKit2** — the Steam network protocol library; almost all Steam-facing behavior (auth, PICS,
  CDN, manifests) is SteamKit2 semantics, not this repo's.
- **protobuf-net** — serializes `DepotConfigStore`, `AccountSettingsStore`, and `ProtoManifest`.
- **QRCoder** — renders the `-qr` login QR code.
- **Microsoft.Windows.CsWin32** — generates Win32 P/Invoke signatures from `NativeMethods.txt` for
  `PlatformUtilities.cs`.
