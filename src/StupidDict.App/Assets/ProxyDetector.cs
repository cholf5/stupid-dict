using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;

namespace StupidDict.App.Assets;

/// <summary>
/// Finds local HTTP proxies when direct downloads fail — the machine may sit
/// behind a system-wide proxy (Clash, V2Ray, Surge) that the browser uses but
/// that .NET on macOS does not pick up automatically. Sources, in order:
/// environment variables, the Windows per-user proxy (WinINET registry), the
/// macOS system proxy settings (scutil), then a probe of the ports the common
/// local proxies listen on.
/// </summary>
internal static class ProxyDetector
{
    private static readonly (int Port, string Scheme)[] CommonLocalPorts =
    [
        (7890, "http"),   // Clash / Clash Verge
        (7897, "http"),   // Clash Verge Rev (default since 2024)
        (1087, "http"),   // V2RayN http
        (10809, "http"),  // V2RayN http
        (6152, "http"),   // Surge
        (8118, "http"),   // Privoxy
        (1080, "socks5"), // classic SOCKS
    ];

    public static IEnumerable<IWebProxy> DetectFromEnvironmentAndSystem()
    {
        foreach (var name in new[] { "HTTPS_PROXY", "https_proxy", "ALL_PROXY", "all_proxy" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Host is { Length: > 0 })
            {
                yield return new WebProxy(uri);
                yield break;
            }
        }

        if (OperatingSystem.IsWindows())
            foreach (var proxy in WindowsSystemProxies())
                yield return proxy;

        if (OperatingSystem.IsMacOS())
            foreach (var proxy in MacSystemProxies())
                yield return proxy;
    }

    private static IEnumerable<IWebProxy> WindowsSystemProxies()
    {
        if (!OperatingSystem.IsWindows())
            yield break;

        // WinINET per-user proxy (the "use a proxy server" toggle in Windows
        // settings). PAC scripts (AutoConfigURL) are not evaluated here —
        // they are covered by the platform-default proxy attempt, which lets
        // .NET resolve the user's browser-equivalent route itself.
        Microsoft.Win32.RegistryKey? key = null;
        IWebProxy? proxy = null;
        try
        {
            key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if ((key?.GetValue("ProxyEnable") as int?) == 1)
            {
                var server = (key.GetValue("ProxyServer") as string)?.Trim();
                if (!string.IsNullOrEmpty(server))
                    proxy = ParseWindowsProxyServer(server);
            }
        }
        catch
        {
            // unreadable registry — no proxy
        }
        finally
        {
            key?.Dispose();
        }
        if (proxy is not null)
            yield return proxy;
    }

    /// <summary>
    /// WinINET's ProxyServer value: either "host:port" for all protocols or a
    /// per-protocol list like "http=h:p;https=h:p;socks=h:p". The https entry
    /// wins, then http, then socks (downloads ride https).
    /// </summary>
    internal static IWebProxy? ParseWindowsProxyServer(string server)
    {
        var scheme = "http";
        var entry = server.Trim();
        if (entry.Contains('=', StringComparison.Ordinal))
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in entry.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var equals = pair.IndexOf('=');
                if (equals <= 0) continue;
                map[pair[..equals].Trim()] = pair[(equals + 1)..].Trim();
            }
            if (!map.TryGetValue("https", out entry!) && !map.TryGetValue("http", out entry!))
            {
                if (!map.TryGetValue("socks", out entry!))
                    return null;
                scheme = "socks5";
            }
        }
        var separator = entry!.LastIndexOf(':');
        if (separator <= 0) return null;
        var host = entry[..separator].Trim().Split("://").Last().Trim();
        if (host.Length == 0 || !int.TryParse(entry[(separator + 1)..].Trim(), out var port) || port is < 1 or > 65535)
            return null;
        try
        {
            return new WebProxy(new Uri($"{scheme}://{host}:{port}"));
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    public static IEnumerable<IWebProxy> ProbeCommonLocalPorts()
    {
        foreach (var (port, scheme) in CommonLocalPorts)
        {
            if (!PortOpen(port)) continue;
            yield return new WebProxy(new Uri($"{scheme}://127.0.0.1:{port}"));
        }
    }

    private static IEnumerable<IWebProxy> MacSystemProxies()
    {
        var output = RunScutil();
        if (output is null) yield break;

        // "HTTPSProxy : 127.0.0.1" / "HTTPSPort : 7890" / "HTTPSEnable : 1"
        var settings = new Dictionary<string, string>();
        foreach (var line in output.Split('\n'))
        {
            var match = Regex.Match(line, @"^\s*(\w+)\s*:\s*(.+?)\s*$");
            if (match.Success) settings[match.Groups[1].Value] = match.Groups[2].Value;
        }

        string? Host(string key) => settings.TryGetValue(key, out var value) ? value : null;
        int? Port(string key) => settings.TryGetValue(key, out var value) && int.TryParse(value, out var port) ? port : null;
        bool Enabled(string key) => settings.TryGetValue(key, out var value) && value == "1";

        if (Enabled("HTTPSEnable") && Host("HTTPSProxy") is { Length: > 0 } httpsHost && Port("HTTPSPort") is { } httpsPort)
            yield return new WebProxy($"http://{httpsHost}:{httpsPort}");
        else if (Enabled("HTTPEnable") && Host("HTTPProxy") is { Length: > 0 } httpHost && Port("HTTPPort") is { } httpPort)
            yield return new WebProxy($"http://{httpHost}:{httpPort}");

        if (Enabled("SOCKSEnable") && Host("SOCKSProxy") is { Length: > 0 } socksHost && Port("SOCKSPort") is { } socksPort)
            yield return new WebProxy($"socks5://{socksHost}:{socksPort}");
    }

    private static string? RunScutil()
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = "/usr/sbin/scutil",
                ArgumentList = { "--proxy" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var process = Process.Start(info);
            if (process is null) return null;
            // Bounded read that kills a hung scutil (B-011): an orphan would
            // sit on the download thread's call forever otherwise.
            var output = SubprocessOutput.ReadWithTimeout(process, 3000);
            return process.HasExited && process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool PortOpen(int port)
    {
        var client = new System.Net.Sockets.TcpClient();
        try
        {
            var connect = client.ConnectAsync(IPAddress.Loopback, port);
            connect.Wait(300);
            return client.Connected;
        }
        catch
        {
            return false;
        }
        finally
        {
            client.Dispose();
        }
    }
}
