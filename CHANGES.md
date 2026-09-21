# Changes from upstream

This is a modified fork of [SteamRE/DepotDownloader](https://github.com/SteamRE/DepotDownloader)
(GPL-2.0). It is **not** the official SteamRE build. Changes, as required by GPL-2.0 section 2(a):

* Added the `-json` flag: machine-readable JSON Lines on stdout with exact byte counters
  (`DepotDownloader/JsonOutput.cs`, `DepotDownloader/DownloadCounters.cs`, hooks in
  `Program.cs`, `ContentDownloader.cs`, `Steam3Session.cs`, `ConsoleAuthenticator.cs`, `Ansi.cs`).
  See [docs/json-mode.md](docs/json-mode.md). Behavior without `-json` is unchanged, except that
  `--help` lists the new flag.
* Added `.github/workflows/release.yml` (tag-triggered release builds); the draft `release` job in
  `build.yml` and the winget submission workflow are disabled on this fork.

Original copyright notices and the LICENSE file are unchanged.
