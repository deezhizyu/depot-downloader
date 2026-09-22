# `-json` mode

With `-json`, **stdout carries only JSON Lines**: one JSON object per line, UTF-8, no BOM, `\n`
terminated, flushed after every line. Without `-json` the program behaves exactly like upstream.

* stdin behaves as upstream. The consumer answers prompts by writing a line to stdin after it
  sees an `auth_prompt` event.
* stderr is unchanged (warnings, unused-argument notes, unhandled-exception traces).
* Exit code: `0` on success, non-zero on failure (as upstream). Every failure is preceded by an
  `error` event.
* The ANSI/OSC progress sequences, the `-debug` .NET `EventListener` output, spinners and
  per-file `NN.NN%` lines are not emitted. All other text upstream prints becomes a `log` event.

Every object has:

| field  | type   | meaning |
|--------|--------|---------|
| `event`| string | event name |
| `t_ms` | int64  | monotonic milliseconds from a `Stopwatch` started when `-json` output was enabled (process start, before any output) |

Field names are snake_case. Integers are JSON numbers (int64).

## Events

| event | fields | when |
|-------|--------|------|
| `log` | `level`: `info`\|`warn`\|`error`\|`debug`, `message` | any text without a dedicated event (`Error…` prefix -> `error`, `Warning…` -> `warn`, `-debug` lines -> `debug`) |
| `auth_prompt` | `kind`: `password`\|`steam_guard_code`\|`email_code`\|`device_confirmation`\|`other`, `message` | before blocking on stdin, or waiting for the phone (`device_confirmation`) |
| `qr` | `url` | `-qr` login: raw challenge URL, on creation and on every refresh |
| `login_success` | `username` (string, `null` for anonymous) | authentication succeeded |
| `app_info` | `app_id`, `name`, `install_dir` | app info available (`install_dir` = app `installdir`) |
| `user_apps` | `apps`: [`app_id`, `name`], `count` | `-list-user-apps`: once, with every app the account's licenses grant |
| `branches` | `app_id`, `branches`: [`name`, `build_id`, `time_updated`, `password_required`] | `-list-branches`: once, with every branch of the requested app |
| `plan` | `depots`: [`depot_id`, `manifest_id`, `branch`, `files`, `compressed_bytes`, `uncompressed_bytes`], `total_compressed_bytes`, `total_uncompressed_bytes`, `total_files` | once, after all manifests are resolved and de-duplicated, before any file is touched. Files exclude directories. With `-manifest-only` `depots` is empty |
| `depot_start` | `depot_id` | depot download begins |
| `depot_done` | `depot_id`, `compressed_bytes`, `uncompressed_bytes` | depot finished; bytes are what this run downloaded/wrote for that depot |
| `progress` | `network_bytes`, `written_bytes`, `verified_bytes`, `files_done`, `current_file` | ~30 Hz timer, only if a counter changed, plus one final emission |
| `done` | `network_bytes`, `written_bytes`, `verified_bytes` | success (last line) |
| `error` | `code`, `message` | fatal failure (last line), then non-zero exit |

### Counters (cumulative, monotonic, never reset between depots)

* `network_bytes`: compressed bytes of every chunk successfully received from the CDN. Added when
  a chunk download completes, using the chunk's compressed length from the manifest (SteamKit2
  does not expose in-flight byte counts). Failed/retried attempts are not counted.
* `written_bytes`: uncompressed bytes written to disk from downloaded chunks, added right after
  each chunk `WriteAsync` completes.
* `verified_bytes`: uncompressed bytes already valid on disk and not re-downloaded (whole
  files, or the valid chunks of a partly present file, including chunks reused from an older
  version of the file). Without `-validate`, a file whose hash equals the previously installed
  manifest is counted as verified without re-hashing it.
* `files_done`: files that are complete (downloaded or verified).

### `progress.current_file`

String or `null`. Path of the file currently being downloaded or written, relative to the install
dir, using the OS path separator as in the manifest. It is set from the moment a chunk of the file
starts downloading until that chunk is written. When several chunks are in flight at once, it is the
file of the most recently started chunk that is still active. `null` when no chunk is in flight
(for example while validating files, or in the final emission). It is not a counter and does not
by itself trigger a `progress` event.

Invariant on a successful run: `written_bytes + verified_bytes == plan.total_uncompressed_bytes`;
on a clean download also `network_bytes == plan.total_compressed_bytes` and
`written_bytes == plan.total_uncompressed_bytes`.

### `error.code`

`invalid_password`, `steam_guard_required`, `no_license`, `no_manifest_access`, `network_timeout`,
`unknown` (fallback).
