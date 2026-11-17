using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace RPG4.Server
{
    /// <summary>Fixed-window IP + normalized-username + global admission control with bounded key memory.</summary>
    internal sealed class AuthenticationRateLimiter
    {
        private sealed class Bucket { public long Window; public int Count; }
        private readonly object gate = new object();
        private readonly Dictionary<string, Bucket> ips = new Dictionary<string, Bucket>(StringComparer.Ordinal);
        private readonly Dictionary<string, Bucket> usernames = new Dictionary<string, Bucket>(StringComparer.Ordinal);
        private readonly int ipLimit, usernameLimit, globalLimit, keyCapacity, seconds;
        private readonly long windowTicks;
        private long globalWindow;
        private int globalCount;
        public AuthenticationRateLimiter(ServerConfiguration config)
        {
            ipLimit = config.attemptsPerIp; usernameLimit = config.attemptsPerUsername; globalLimit = config.attemptsGlobal;
            keyCapacity = config.rateKeyCapacity; seconds = config.rateWindowSeconds;
            windowTicks = Stopwatch.Frequency * (long)seconds;
        }
        public bool TryEnter(string ip, string username, out int retryAfterSeconds)
        {
            long now = Stopwatch.GetTimestamp();
            long window = now / windowTicks;
            lock (gate)
            {
                retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((windowTicks - now % windowTicks) / (double)Stopwatch.Frequency));
                if (window != globalWindow)
                { globalWindow = window; globalCount = 0; ips.Clear(); usernames.Clear(); }
                // Count denied work globally too: dictionary operations remain globally bounded per window.
                if (globalCount >= globalLimit) return false;
                globalCount++;
                if (ip == null || ip.Length > 128 || username == null || username.Length > 32) return false;
                bool ipAllowed = Increment(ips, ip, window, ipLimit);
                bool userAllowed = Increment(usernames, username, window, usernameLimit);
                return ipAllowed && userAllowed;
            }
        }
        private bool Increment(Dictionary<string, Bucket> buckets, string key, long window, int limit)
        {
            if (!buckets.TryGetValue(key, out Bucket bucket))
            {
                if (buckets.Count >= keyCapacity) return false;
                bucket = new Bucket { Window = window };
                buckets.Add(key, bucket);
            }
            if (bucket.Count >= limit) return false;
            bucket.Count++;
            return true;
        }
    }
}
