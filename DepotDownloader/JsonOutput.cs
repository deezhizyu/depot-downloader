// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

// Fork addition: machine-readable output for the -json flag. See docs/json-mode.md.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using SteamKit2;

namespace DepotDownloader
{
    internal readonly record struct PlanDepot(uint DepotId, ulong ManifestId, string Branch, long Files, long CompressedBytes, long UncompressedBytes);

    /// <summary>
    /// Owns stdout when -json is enabled. Every line written is exactly one JSON object.
    /// This is the only place that writes to stdout in that mode.
    /// </summary>
    internal static class JsonOutput
    {
        public static bool Enabled { get; private set; }

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly object WriteLock = new();
        private static readonly ArrayBufferWriter<byte> Buffer = new(4096);
        private static Utf8JsonWriter writer;
        private static Stream stdout;

        private static readonly object ProgressLock = new();
        private static Timer progressTimer;
        private static long lastNetwork = -1, lastWritten = -1, lastVerified = -1, lastFilesDone = -1;

        private static readonly object FailLock = new();
        private static string failCode, failMessage, lastLogMessage, lastErrorMessage;
        private static bool errorEmitted;

        public static void Enable()
        {
            if (Enabled)
            {
                return;
            }

            stdout = Console.OpenStandardOutput();
            writer = new Utf8JsonWriter(Buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            Enabled = true;

            // Everything upstream prints with Console.Write/WriteLine becomes a "log" event.
            Console.SetOut(new LineLogWriter());

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                EmitFatal(e.ExceptionObject is Exception ex ? ex.Message : "Unhandled exception");
        }

        private static void Emit(string name, Action<Utf8JsonWriter> body)
        {
            if (!Enabled)
            {
                return;
            }

            lock (WriteLock)
            {
                try
                {
                    Buffer.ResetWrittenCount();
                    writer.Reset(Buffer);
                    writer.WriteStartObject();
                    writer.WriteString("event", name);
                    writer.WriteNumber("t_ms", Clock.ElapsedMilliseconds);
                    body(writer);
                    writer.WriteEndObject();
                    writer.Flush();

                    stdout.Write(Buffer.WrittenSpan);
                    stdout.WriteByte((byte)'\n');
                    stdout.Flush();
                }
                catch (IOException)
                {
                    // Consumer closed the pipe; nothing sensible left to do.
                }
            }
        }

        public static void Log(string level, string message)
        {
            lastLogMessage = message;

            if (level == "error")
            {
                lastErrorMessage = message;
            }

            Emit("log", w =>
            {
                w.WriteString("level", level);
                w.WriteString("message", message);
            });
        }

        public static void AuthPrompt(string kind, string message)
        {
            if (!Enabled)
            {
                return;
            }

            // The prompt text was just written with Console.Write and has no newline; it is in the event instead.
            (Console.Out as LineLogWriter)?.DropPartial();

            Emit("auth_prompt", w =>
            {
                w.WriteString("kind", kind);
                w.WriteString("message", message);
            });
        }

        public static void Qr(string url) => Emit("qr", w => w.WriteString("url", url));

        public static void LoginSuccess(string username) => Emit("login_success", w =>
        {
            if (username == null)
            {
                w.WriteNull("username");
            }
            else
            {
                w.WriteString("username", username);
            }
        });

        public static void AppInfo(uint appId, string name, string installDir) => Emit("app_info", w =>
        {
            w.WriteNumber("app_id", appId);
            w.WriteString("name", name);
            w.WriteString("install_dir", installDir);
        });

        public static void UserApps(IReadOnlyList<(uint AppId, string Name)> apps) => Emit("user_apps", w =>
        {
            w.WriteStartArray("apps");
            foreach (var app in apps)
            {
                w.WriteStartObject();
                w.WriteNumber("app_id", app.AppId);
                w.WriteString("name", app.Name);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteNumber("count", apps.Count);
        });

        public static void Plan(IReadOnlyList<PlanDepot> depots)
        {
            long compressed = 0, uncompressed = 0, files = 0;
            foreach (var d in depots)
            {
                compressed += d.CompressedBytes;
                uncompressed += d.UncompressedBytes;
                files += d.Files;
            }

            Emit("plan", w =>
            {
                w.WriteStartArray("depots");
                foreach (var d in depots)
                {
                    w.WriteStartObject();
                    w.WriteNumber("depot_id", d.DepotId);
                    w.WriteNumber("manifest_id", d.ManifestId);
                    w.WriteString("branch", d.Branch);
                    w.WriteNumber("files", d.Files);
                    w.WriteNumber("compressed_bytes", d.CompressedBytes);
                    w.WriteNumber("uncompressed_bytes", d.UncompressedBytes);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteNumber("total_compressed_bytes", compressed);
                w.WriteNumber("total_uncompressed_bytes", uncompressed);
                w.WriteNumber("total_files", files);
            });
        }

        public static void DepotStart(uint depotId) => Emit("depot_start", w => w.WriteNumber("depot_id", depotId));

        public static void DepotDone(uint depotId, ulong compressed, ulong uncompressed) => Emit("depot_done", w =>
        {
            w.WriteNumber("depot_id", depotId);
            w.WriteNumber("compressed_bytes", compressed);
            w.WriteNumber("uncompressed_bytes", uncompressed);
        });

        // Timer thread only reads the Interlocked counters; chunk workers never write to stdout.
        public static void StartProgress()
        {
            if (!Enabled || progressTimer != null)
            {
                return;
            }

            progressTimer = new Timer(_ => EmitProgress(false), null, 33, 33);
        }

        public static void StopProgress()
        {
            var timer = progressTimer;
            if (timer == null)
            {
                return;
            }

            progressTimer = null;
            timer.Dispose();
            EmitProgress(true);
        }

        private static void EmitProgress(bool force)
        {
            lock (ProgressLock)
            {
                var n = DownloadCounters.Network;
                var wr = DownloadCounters.Written;
                var v = DownloadCounters.Verified;
                var f = DownloadCounters.FilesDone;

                if (!force && n == lastNetwork && wr == lastWritten && v == lastVerified && f == lastFilesDone)
                {
                    return;
                }

                lastNetwork = n;
                lastWritten = wr;
                lastVerified = v;
                lastFilesDone = f;

                Emit("progress", w =>
                {
                    w.WriteNumber("network_bytes", n);
                    w.WriteNumber("written_bytes", wr);
                    w.WriteNumber("verified_bytes", v);
                    w.WriteNumber("files_done", f);

                    var current = DownloadCounters.CurrentFile;
                    if (current == null)
                    {
                        w.WriteNull("current_file");
                    }
                    else
                    {
                        w.WriteString("current_file", current);
                    }
                });
            }
        }

        public static void Done() => Emit("done", w =>
        {
            w.WriteNumber("network_bytes", DownloadCounters.Network);
            w.WriteNumber("written_bytes", DownloadCounters.Written);
            w.WriteNumber("verified_bytes", DownloadCounters.Verified);
        });

        /// <summary>Records the first known failure so the final "error" event carries a stable code.</summary>
        public static void Fail(string code, string message)
        {
            if (!Enabled)
            {
                return;
            }

            lock (FailLock)
            {
                failCode ??= code;
                failMessage ??= message;
            }
        }

        public static string CodeForResult(EResult result) => result switch
        {
            EResult.InvalidPassword => "invalid_password",
            EResult.AccountLogonDenied
                or EResult.AccountLoginDeniedNeedTwoFactor
                or EResult.TwoFactorCodeMismatch
                or EResult.InvalidLoginAuthCode
                or EResult.AccountLoginDeniedThrottle => "steam_guard_required",
            EResult.Timeout => "network_timeout",
            _ => "unknown",
        };

        /// <summary>Emits the single terminal "error" event (at most once per run).</summary>
        public static void EmitFatal(string fallbackMessage)
        {
            string code, message;
            lock (FailLock)
            {
                if (errorEmitted)
                {
                    return;
                }

                errorEmitted = true;
                code = failCode ?? "unknown";
                message = failMessage ?? lastErrorMessage ?? lastLogMessage ?? fallbackMessage;
            }

            StopProgress();
            Emit("error", w =>
            {
                w.WriteString("code", code);
                w.WriteString("message", message);
            });
        }

        // Turns line-oriented Console output into "log" events.
        private sealed class LineLogWriter : TextWriter
        {
            private readonly StringBuilder line = new();
            private readonly object gate = new();

            public override Encoding Encoding => Encoding.UTF8;

            public void DropPartial()
            {
                lock (gate)
                {
                    line.Clear();
                }
            }

            public override void Write(char value)
            {
                string complete = null;

                lock (gate)
                {
                    if (value == '\n')
                    {
                        complete = line.ToString();
                        line.Clear();
                    }
                    else if (value != '\r')
                    {
                        line.Append(value);
                    }
                }

                if (complete != null)
                {
                    Publish(complete);
                }
            }

            public override void Write(string value)
            {
                if (value == null)
                {
                    return;
                }

                foreach (var c in value)
                {
                    Write(c);
                }
            }

            public override void Write(char[] buffer, int index, int count)
            {
                for (var i = index; i < index + count; i++)
                {
                    Write(buffer[i]);
                }
            }

            private static void Publish(string text)
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    return;
                }

                var level = text.StartsWith("Error", StringComparison.Ordinal) ? "error"
                    : text.StartsWith("Warning", StringComparison.Ordinal) ? "warn"
                    : "info";

                Log(level, text);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    string rest;
                    lock (gate)
                    {
                        rest = line.ToString();
                        line.Clear();
                    }

                    Publish(rest);
                }

                base.Dispose(disposing);
            }
        }
    }
}
