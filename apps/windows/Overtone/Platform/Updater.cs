using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.UI.Dispatching;

namespace Overtone.Platform;

/// <summary>A published release newer than this build.</summary>
public sealed record UpdateRelease(string Version, string PageUrl, string? SetupName, string? SetupUrl, string? SumsUrl);

/// <summary>
/// Checks GitHub Releases for a newer Overtone (shortly after launch, then every six
/// hours, or from the tray menu) and installs it in one click: downloads this
/// architecture's setup, checks it against the release's SHA256SUMS, runs it silently
/// (it closes Overtone, replaces the files, keeps "Start with Windows" as it is and
/// starts the new version), and quits. A copy not installed by the setup (portable
/// zip, development build) opens the release page instead.
/// </summary>
public sealed class Updater : IDisposable
{
    public const string Repo = "bunkhq/overtone";
    private static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    /// <summary>Where the setup installs (installer/Overtone.iss DefaultDirName, scripts/install-windows.ps1).</summary>
    private static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Overtone");

    /// <summary>This build's version, as stamped by the release workflow (InformationalVersion, without build metadata).</summary>
    public static string CurrentVersion { get; } =
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    // After CurrentVersion: static initializers run in source order, and CreateClient
    // puts the version in the User-Agent (a null one makes ParseAdd throw at startup).
    private static readonly HttpClient Http = CreateClient();

    private readonly DispatcherQueue _ui;
    private readonly DispatcherQueueTimer _timer;
    private bool _checking;

    public Updater(DispatcherQueue ui)
    {
        _ui = ui;
        _timer = ui.CreateTimer();
        _timer.Interval = FirstCheck;
        _timer.Tick += (_, _) =>
        {
            _timer.Interval = Interval;
            _ = CheckAsync(manual: false);
        };
        _timer.Start();
    }

    /// <summary>The newer release found by the last check, or null.</summary>
    public UpdateRelease? Available { get; private set; }

    /// <summary>True while downloading and starting the setup.</summary>
    public bool Installing { get; private set; }

    /// <summary>Whether Install replaces this copy (installed by the setup) or only opens the release page.</summary>
    public static bool CanInstallInPlace =>
        string.Equals(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), InstallDir, StringComparison.OrdinalIgnoreCase);

    /// <summary>Raised on the UI thread when Available or Installing changes.</summary>
    public event Action? Changed;

    /// <summary>A notice for the user: (title, message). Raised on the UI thread.</summary>
    public event Action<string, string>? Notice;

    /// <summary>Asks GitHub for the latest release. A manual check reports "up to date" and errors too.</summary>
    public async Task CheckAsync(bool manual)
    {
        if (_checking || Installing) return;
        _checking = true;
        try
        {
            var latest = await FetchLatestAsync();
            var newer = latest is not null && AppVersion.Compare(latest.Version, CurrentVersion) > 0 ? latest : null;
            var announce = newer is not null && newer.Version != Available?.Version;
            Available = newer;
            Changed?.Invoke();
            if (announce)
                Notice?.Invoke($"Overtone {newer!.Version} is available", CanInstallInPlace
                    ? "Choose Install update in the tray menu. It takes a few seconds."
                    : "Choose Download update in the tray menu.");
            else if (manual && newer is null)
                Notice?.Invoke("Overtone is up to date", $"You have the latest version, {CurrentVersion}.");
        }
        catch (Exception ex)
        {
            Settings.LogError(ex);
            if (manual) Notice?.Invoke("Couldn't check for updates", "Check your connection and try again.");
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>Installs <see cref="Available"/>: downloads, verifies and runs the setup, then calls <paramref name="quit"/>.</summary>
    public async Task InstallAsync(Action quit)
    {
        if (Available is not { } release || Installing) return;
        if (!CanInstallInPlace || release.SetupUrl is null || release.SetupName is null)
        {
            Open(release.PageUrl);
            return;
        }

        Installing = true;
        Changed?.Invoke();
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "Overtone-update");
            Directory.CreateDirectory(dir);
            var setup = Path.Combine(dir, release.SetupName);
            await DownloadAsync(release.SetupUrl, setup);
            if (release.SumsUrl is null) throw new InvalidDataException($"{release.Version} has no SHA256SUMS");
            var sums = await Http.GetStringAsync(release.SumsUrl);
            if (!Matches(sums, release.SetupName, await HashAsync(setup)))
            {
                File.Delete(setup);
                throw new InvalidDataException($"{release.SetupName} does not match SHA256SUMS");
            }

            // /SILENT shows the setup's progress window and nothing else. Keep the
            // sign-in entry as the user has it now (the setup would otherwise use the
            // choice from its first install).
            var tasks = Startup.Enabled ? "startup" : "!startup";
            Process.Start(new ProcessStartInfo(setup)
            {
                Arguments = $"/SILENT /SUPPRESSMSGBOXES /NORESTART /NOCANCEL /SP- /MERGETASKS=\"{tasks}\"",
                UseShellExecute = true,
            });
            quit();
        }
        catch (Exception ex)
        {
            Settings.LogError(ex);
            Installing = false;
            Changed?.Invoke();
            Notice?.Invoke("Couldn't install the update", "Opening the download page instead.");
            Open(release.PageUrl);
        }
    }

    private static async Task<UpdateRelease?> FetchLatestAsync()
    {
        // /releases/latest skips drafts and pre-releases.
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString();
        if (string.IsNullOrEmpty(tag)) return null;
        var version = tag.TrimStart('v', 'V');
        var page = root.GetProperty("html_url").GetString() ?? $"https://github.com/{Repo}/releases/latest";

        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        string? setupName = null, setupUrl = null, sumsUrl = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var url = asset.GetProperty("browser_download_url").GetString();
            if (name.EndsWith($"-Windows-{arch}-Setup.exe", StringComparison.OrdinalIgnoreCase))
                (setupName, setupUrl) = (name, url);
            else if (name == "SHA256SUMS")
                sumsUrl = url;
        }
        return new UpdateRelease(version, page, setupName, setupUrl, sumsUrl);
    }

    private static async Task DownloadAsync(string url, string path)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var file = File.Create(path);
        await response.Content.CopyToAsync(file);
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file));
    }

    /// <summary>True when SHA256SUMS (sha256sum format: "hash  name" per line) lists <paramref name="name"/> with <paramref name="hash"/>.</summary>
    internal static bool Matches(string sums, string name, string hash) =>
        sums.Split('\n').Select(l => l.Trim().Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries))
            .Any(p => p.Length == 2 && p[1].TrimStart('*').Trim() == name && string.Equals(p[0], hash, StringComparison.OrdinalIgnoreCase));

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Settings.LogError(ex); }
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        // GitHub's API rejects requests without a User-Agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Overtone/{CurrentVersion}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    public void Dispose() => _timer.Stop();
}

/// <summary>Semantic-version ordering for release tags: 0.2.0 &gt; 0.2.0-dev.5 &gt; 0.1.9.</summary>
public static class AppVersion
{
    public static int Compare(string a, string b)
    {
        var (na, pa) = Split(a);
        var (nb, pb) = Split(b);
        for (var i = 0; i < Math.Max(na.Length, nb.Length); i++)
        {
            var c = (i < na.Length ? na[i] : 0).CompareTo(i < nb.Length ? nb[i] : 0);
            if (c != 0) return c;
        }
        // A pre-release sorts before its release.
        if (pa is null || pb is null) return (pa is null ? 1 : 0) - (pb is null ? 1 : 0);
        return ComparePre(pa, pb);
    }

    private static (int[] Numbers, string? Pre) Split(string v)
    {
        v = v.Trim().TrimStart('v', 'V').Split('+')[0];
        var dash = v.IndexOf('-');
        var core = dash < 0 ? v : v[..dash];
        var nums = core.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        return (nums, dash < 0 ? null : v[(dash + 1)..]);
    }

    private static int ComparePre(string a, string b)
    {
        var x = a.Split('.');
        var y = b.Split('.');
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            var xn = int.TryParse(x[i], out var xi);
            var yn = int.TryParse(y[i], out var yi);
            var c = xn && yn ? xi.CompareTo(yi) : xn ? -1 : yn ? 1 : string.CompareOrdinal(x[i], y[i]);
            if (c != 0) return Math.Sign(c);
        }
        return x.Length.CompareTo(y.Length);
    }
}
