using System.Collections.Concurrent;
using System.Diagnostics;

namespace Icom_Web_Control.Services
{
    /// <summary>
    /// Caches process status checks to avoid expensive GetProcessesByName calls on every request.
    /// Process status is cached for a short duration (2 seconds) to balance responsiveness with performance.
    /// </summary>
    public class ProcessStatusCacheService
    {
        private readonly ConcurrentDictionary<string, (bool isRunning, DateTime cachedAt)> _cache = new();
        private readonly TimeSpan _cacheDuration = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Checks if a process is running, using cached value if available and fresh.
        /// </summary>
        /// <param name="processName">The name of the process to check (without .exe extension)</param>
        /// <returns>True if the process is running, false otherwise</returns>
        public bool IsProcessRunning(string processName)
        {
            var now = DateTime.UtcNow;

            // Check if we have a cached value that's still fresh
            if (_cache.TryGetValue(processName, out var cached) && (now - cached.cachedAt) < _cacheDuration)
            {
                return cached.isRunning;
            }

            // Cache miss or stale - check the actual process status
            var isRunning = Process.GetProcessesByName(processName).Length > 0;
            _cache[processName] = (isRunning, now);

            return isRunning;
        }

        /// <summary>
        /// Invalidates the cache for a specific process (useful after launching a process).
        /// </summary>
        /// <param name="processName">The name of the process to invalidate</param>
        public void InvalidateCache(string processName)
        {
            _cache.TryRemove(processName, out _);
        }

        /// <summary>
        /// Clears all cached process statuses.
        /// </summary>
        public void ClearCache()
        {
            _cache.Clear();
        }
    }
}
