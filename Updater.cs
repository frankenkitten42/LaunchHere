using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace LaunchHere;

/// <summary>A release on GitHub that is newer than the running version.</summary>
internal sealed record UpdateInfo(string Version, string Tag, string PageUrl, string Notes, string ExeUrl, string ShaUrl, string SigUrl);

/// <summary>Checks GitHub Releases for a newer version and installs it in place: download, verify the
/// SHA-256 against the release's .sha256 file and the ECDSA signature in its .sig file (made with the
/// developer's private key, which is never on GitHub: see tools/sign-release.ps1), swap the exe, restart. The exe path stays the same, so
/// Start with Windows keeps working.</summary>
internal static class Updater
{
    const string ReleasesUrl = "https://api.github.com/repos/frankenkitten42/LaunchHere/releases";
    const string ExeAsset = "LaunchHere.exe";
    const string ShaAsset = "LaunchHere.exe.sha256";
    const string SigAsset = "LaunchHere.exe.sig";

    /// <summary>Release downloads must come from this repo's GitHub releases, over HTTPS.</summary>
    const string DownloadPrefix = "https://github.com/frankenkitten42/LaunchHere/releases/download/";

    /// <summary>Public half of the release signing key (ECDSA P-256). An update is installed only if its
    /// .sig verifies against this, so a release uploaded by someone without the private key is refused.</summary>
    const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAESaFCL4spk2SMycw5LlvOmCxfuWrL
        /HA3SreEsVqHkc5M8BvP67JLGtwF9HXqPelEHGxyM1Un+P5bKlHIg6LPLw==
        -----END PUBLIC KEY-----
        """;

#if TEST_HOOKS
    // Test builds only (dotnet publish -p:TestHooks=true): a local feed, a test key, and localhost downloads.
    static string FeedUrl => Environment.GetEnvironmentVariable("LAUNCHHERE_UPDATE_FEED") is { Length: > 0 } u ? u : ReleasesUrl;
    static string PublicKey => Environment.GetEnvironmentVariable("LAUNCHHERE_UPDATE_PUBKEY") is { Length: > 0 } k ? File.ReadAllText(k) : PublicKeyPem;
    static bool AllowedUrl(string url) => url.StartsWith(DownloadPrefix, StringComparison.Ordinal)
        || url.StartsWith("http://localhost:", StringComparison.Ordinal) || url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal);
#else
    const string FeedUrl = ReleasesUrl;
    const string PublicKey = PublicKeyPem;
    static bool AllowedUrl(string url) => url.StartsWith(DownloadPrefix, StringComparison.Ordinal);
#endif

    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

    static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // GitHub's API requires a User-Agent. Generic: no user details.
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"LaunchHere-updater/{CurrentVersion}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>The newest release that is newer than this version and has all three assets (exe, .sha256,
    /// .sig) at allowed URLs, or null.
    /// Pre-releases count (every release is a beta for now); drafts don't.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var http = NewClient();
        using var stream = await http.GetStreamAsync(FeedUrl, ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        UpdateInfo? best = null;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
            string tag = r.GetProperty("tag_name").GetString() ?? "";
            string version = tag.TrimStart('v', 'V');
            if (Compare(version, CurrentVersion) <= 0) continue;
            if (best != null && Compare(version, best.Version) <= 0) continue;

            string exe = "", sha = "", sig = "";
            foreach (var a in r.GetProperty("assets").EnumerateArray())
            {
                string name = a.GetProperty("name").GetString() ?? "";
                string url = a.GetProperty("browser_download_url").GetString() ?? "";
                if (name.Equals(ExeAsset, StringComparison.OrdinalIgnoreCase)) exe = url;
                else if (name.Equals(ShaAsset, StringComparison.OrdinalIgnoreCase)) sha = url;
                else if (name.Equals(SigAsset, StringComparison.OrdinalIgnoreCase)) sig = url;
            }
            // Can't verify it, or it would download from somewhere else: don't offer it.
            if (!AllowedUrl(exe) || !AllowedUrl(sha) || !AllowedUrl(sig)) continue;
            best = new UpdateInfo(version, tag, r.GetProperty("html_url").GetString() ?? "",
                r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "", exe, sha, sig);
        }
        return best;
    }

    /// <summary>Compare "1.2.3" / "1.2.3-beta" style versions. A release without a suffix is newer
    /// than the same numbers with one; suffixes compare alphabetically.</summary>
    public static int Compare(string a, string b)
    {
        static (Version num, string pre) Parse(string s)
        {
            s = s.Split('+')[0];
            int dash = s.IndexOf('-');
            string num = dash >= 0 ? s[..dash] : s, pre = dash >= 0 ? s[(dash + 1)..] : "";
            var parts = num.Split('.').Select(p => int.TryParse(p, out int n) ? n : 0).ToList();
            while (parts.Count < 3) parts.Add(0);
            return (new Version(parts[0], parts[1], parts[2]), pre);
        }
        var (na, pa) = Parse(a);
        var (nb, pb) = Parse(b);
        int c = na.CompareTo(nb);
        if (c != 0) return c;
        if (pa == pb) return 0;
        if (pa == "") return 1;
        if (pb == "") return -1;
        return string.Compare(pa, pb, StringComparison.OrdinalIgnoreCase);
    }

    static string ExePath => Environment.ProcessPath!;
    static string NewPath => ExePath + ".new";
    public static string OldPath => ExePath + ".old";

    /// <summary>Download the update next to the running exe (as .new) and check its SHA-256 and signature.
    /// Returns the path of the verified file; throws with a readable message otherwise.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        using var http = NewClient();
        string shaText = await http.GetStringAsync(info.ShaUrl, ct);
        string expected = shaText.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (expected.Length != 64) throw new InvalidDataException("The release's checksum file is not valid.");
        byte[] signature;
        try { signature = Convert.FromBase64String((await http.GetStringAsync(info.SigUrl, ct)).Trim()); }
        catch (FormatException) { throw new InvalidDataException("The release's signature file is not valid. Nothing was changed."); }

        string target = NewPath;
        try
        {
            using (var resp = await http.GetAsync(info.ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? -1;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(target);
                var buf = new byte[81920];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }
            byte[] hash;
            await using (var f = File.OpenRead(target))
                hash = await SHA256.HashDataAsync(f, ct);
            string actual = Convert.ToHexString(hash);
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The download doesn't match the release's checksum (expected {expected[..12]}..., got {actual[..12]}...). Nothing was changed.");
            using (var ec = ECDsa.Create())
            {
                ec.ImportFromPem(PublicKey);
                if (!ec.VerifyHash(hash, signature))
                    throw new InvalidDataException("The download isn't signed by the LaunchHere developer, so it was not installed. Nothing was changed.");
            }
            return target;
        }
        catch
        {
            try { File.Delete(target); } catch { }
            throw;
        }
    }

    /// <summary>Name of the event the new version sets once it is up and running.</summary>
    public static string StartedEventName => Program.InstanceName + ".UpdateStarted";

    /// <summary>Swap the verified download in and start it. Call after giving up the single-instance
    /// mutex and hiding the tray icon. Waits for the new version to report that it started; if it
    /// doesn't, puts the old exe back and returns an error message (the caller keeps running).
    /// Returns null on success: the caller should exit.</summary>
    public static string? SwapAndStart(string downloaded, Logger log)
    {
        string exe = ExePath, old = OldPath;
        try { if (File.Exists(old)) File.Delete(old); } catch { }   // a leftover from an earlier update
        try
        {
            File.Move(exe, old);            // a running exe can be renamed, not overwritten
            File.Move(downloaded, exe);
        }
        catch (Exception ex)
        {
            if (!File.Exists(exe) && File.Exists(old)) File.Move(old, exe);
            return $"Could not replace the exe: {ex.Message}";
        }

        using var started = new EventWaitHandle(false, EventResetMode.ManualReset, StartedEventName);
        Process? p = null;
        try
        {
            p = Process.Start(new ProcessStartInfo(exe, "--after-update") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
            log.Always($"update: started new version, pid {p?.Id}");
            // Wait for the "I'm running" signal (or for it to die).
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(20))
            {
                if (started.WaitOne(200)) return null;
                if (p == null || p.HasExited) break;
            }
            log.Error($"update: new version did not start (exited={p?.HasExited}, code={(p?.HasExited == true ? p.ExitCode : 0)})");
            try { if (p != null && !p.HasExited) p.Kill(); p?.WaitForExit(5000); } catch { }
        }
        catch (Exception ex)
        {
            log.Error($"update: could not start the new version: {ex.Message}");
        }
        finally { p?.Dispose(); }

        // Roll back.
        try
        {
            File.Move(exe, exe + ".failed", overwrite: true);
            File.Move(old, exe);
            try { File.Delete(exe + ".failed"); } catch { }
            return "The new version didn't start, so LaunchHere went back to the current version.";
        }
        catch (Exception ex)
        {
            return $"The new version didn't start, and restoring the old one failed: {ex.Message}. " +
                   $"Rename {Path.GetFileName(old)} back to {Path.GetFileName(exe)}.";
        }
    }

    /// <summary>Run by the new version: tell the old one we're up, then remove the old exe once the
    /// old process has exited (it's locked until then).</summary>
    public static void ConfirmStarted(Logger log)
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(StartedEventName, out var ev)) using (ev) ev.Set();
        }
        catch (Exception ex) { log.Error($"update: could not signal start: {ex.Message}"); }
        log.Always($"update: now running {CurrentVersion}");
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 60; i++)
            {
                try
                {
                    if (!File.Exists(OldPath)) return;
                    File.Delete(OldPath);
                    log.Always("update: removed the previous version");
                    return;
                }
                catch { await Task.Delay(500); }
            }
        });
    }
}
