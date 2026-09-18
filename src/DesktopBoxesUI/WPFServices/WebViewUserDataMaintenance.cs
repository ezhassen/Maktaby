using System.IO;

namespace DesktopBoxesUI.WPFServices;

/// <summary>
/// Best-effort pruning of the shared WebView2 user-data folder (<c>EBWebView</c> under the app-data
/// dir). Every widget WebView2 shares one environment, so Chromium's GPU/shader/crash payload caches
/// accumulate there for the life of the install with no built-in eviction wired up. Only regenerable
/// payload caches are touched — profile state (cookies, local/session storage, IndexedDB,
/// Preferences) is never deleted. Everything is exception-swallowed per entry: locked files
/// (a live renderer) are simply skipped. Runs once at startup, before any WebView2 init.
/// </summary>
public static class WebViewUserDataMaintenance
{
    // Regenerable payload caches, safe to wipe whole. Present at the env root and/or Default profile.
    private static readonly string[] SafeCacheDirs =
    {
        "ShaderCache", "GpuCache", "GPUCache", "GrShaderCache", "DawnCache", "Crashpad",
    };

    // HTTP caches: valuable for network-allowed widgets, so only wiped when oversized.
    private static readonly string[] SizeGatedCacheDirs = { "Cache", "Code Cache" };
    private const long HttpCacheSizeThresholdBytes = 256L * 1024 * 1024;

    private static readonly string[] SafeFilePatterns = { "*.log", "*.old" };

    public static void TryPrune(string appDataDir)
    {
        try
        {
            string envRoot = Path.Combine(appDataDir, "EBWebView");
            if (!Directory.Exists(envRoot))
            {
                return;
            }

            string[] scopes = { envRoot, Path.Combine(envRoot, "Default") };
            foreach (string scope in scopes)
            {
                if (!Directory.Exists(scope))
                {
                    continue;
                }

                foreach (string name in SafeCacheDirs)
                {
                    DeleteDirectoryQuiet(Path.Combine(scope, name));
                }

                foreach (string pattern in SafeFilePatterns)
                {
                    string[] files;
                    try
                    {
                        files = Directory.GetFiles(scope, pattern, SearchOption.TopDirectoryOnly);
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (string file in files)
                    {
                        DeleteFileQuiet(file);
                    }
                }

                // "Service Worker" script caches regenerate on next fetch; wipe only when oversized.
                PruneOversizedCaches(scope);
            }
        }
        catch
        {
            // Maintenance must never fail startup.
        }
    }

    private static void PruneOversizedCaches(string scope)
    {
        foreach (string name in SizeGatedCacheDirs)
        {
            string dir = Path.Combine(scope, name);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            if (GetDirectorySizeQuiet(dir) > HttpCacheSizeThresholdBytes)
            {
                DeleteDirectoryQuiet(dir);
            }
        }
    }

    private static long GetDirectorySizeQuiet(string dir)
    {
        long size = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    size += new FileInfo(file).Length;
                }
                catch
                {
                    // Locked/disappearing file: ignore, keep estimating.
                }

                if (size > HttpCacheSizeThresholdBytes)
                {
                    break; // already over: no need to walk the rest
                }
            }
        }
        catch
        {
            return 0;
        }

        return size;
    }

    private static void DeleteDirectoryQuiet(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // Locked by a live renderer or already gone: skip.
        }
    }

    private static void DeleteFileQuiet(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
        catch
        {
            // Locked or already gone: skip.
        }
    }
}
