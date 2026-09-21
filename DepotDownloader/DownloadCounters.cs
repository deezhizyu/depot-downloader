// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System.Threading;

namespace DepotDownloader
{
    // Cumulative per-run counters. Monotonic, never reset between depots.
    // Mutated only via Interlocked, read with Interlocked.Read.
    internal static class DownloadCounters
    {
        private static long network;
        private static long written;
        private static long verified;
        private static long filesDone;

        public static void AddNetwork(long bytes) => Interlocked.Add(ref network, bytes);
        public static void AddWritten(long bytes) => Interlocked.Add(ref written, bytes);
        public static void AddVerified(long bytes) => Interlocked.Add(ref verified, bytes);
        public static void AddFileDone() => Interlocked.Increment(ref filesDone);

        public static long Network => Interlocked.Read(ref network);
        public static long Written => Interlocked.Read(ref written);
        public static long Verified => Interlocked.Read(ref verified);
        public static long FilesDone => Interlocked.Read(ref filesDone);
    }
}
