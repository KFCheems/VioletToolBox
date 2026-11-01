using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VioletToolBox.Core.Download;

public sealed record RomListing(string Name, string Version, string Url, string? Extra = null);

/// <summary>
/// 下载专区: 联想查包 (Lenovo pad ROM query), violettool ROM 获取 backend and
/// Google factory-image link parsing. Ports the endpoints used by upstream RomDownload.cs.
/// </summary>
public sealed class DownloadCenterService
{
    private const string LenovoMachineQueryUrl = "https://ptstpd.lenovo.com.cn/home/ConfigurationQuery/getMachineSequenceInfo";
    private const string LenovoPackageQueryUrl = "https://ptstpd.lenovo.com.cn/home/ConfigurationQuery/getPadFlashingMachine";
    private const string LenovoPackagePassword = "FC(fv:SknR";
    private const string RomBackendBaseUrl = "https://violettool.top/rom-api";

    private static readonly IReadOnlyDictionary<string, string> LenovoPresetMtm = new Dictionary<string, string>
    {
        ["Y700二代"] = "ZACW0006CN",
        ["Y700三代"] = "ZAEF0029CN",
        ["Y700四代"] = "ZAG40005CN",
        ["Y700五代"] = "ZAH20097CN",
    };

    public IReadOnlyList<string> LenovoPresets() => LenovoPresetMtm.Keys.ToList();

    private static HttpClient NewHttp()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (X11; Linux x86_64) VioletToolBox-Linux");
        return http;
    }

    /// <summary>Queries the Lenovo backend for pad flashing packages by MTM code.</summary>
    public async Task<List<RomListing>> QueryLenovoAsync(string mtmOrModel, CancellationToken ct = default)
    {
        var mtm = LenovoPresetMtm.TryGetValue(mtmOrModel, out var preset) ? preset : mtmOrModel.Trim();
        using var http = NewHttp();

        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["machineCode"] = mtm,
            ["password"] = LenovoPackagePassword,
        });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(LenovoPackageQueryUrl, content, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        var results = new List<RomListing>();
        var data = doc.RootElement.GetProperty("data");
        foreach (var item in data.EnumerateArray())
        {
            var name = item.TryGetProperty("packageName", out var n) ? n.GetString() ?? "" : "";
            var url = item.TryGetProperty("packageUrl", out var u) ? u.GetString() ?? "" : "";
            var size = item.TryGetProperty("packageSize", out var s) ? s.GetString() ?? "" : "";
            if (!string.IsNullOrEmpty(url))
                results.Add(new RomListing(name, "", url, size));
        }
        return results;
    }

    /// <summary>Queries the violettool ROM backend (Rom 获取) for a keyword.</summary>
    public async Task<List<RomListing>> QueryVioletRomAsync(string keyword, CancellationToken ct = default)
    {
        using var http = NewHttp();
        var json = await http.GetStringAsync($"{RomBackendBaseUrl}?keyword={Uri.EscapeDataString(keyword)}", ct)
            .ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var results = new List<RomListing>();
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(url)) results.Add(new RomListing(name, "", url));
            }
        }
        return results;
    }

    /// <summary>Scrapes direct download links (for Google factory images / OTA pages).</summary>
    public async Task<List<RomListing>> ExtractLinksFromPageAsync(string pageUrl, CancellationToken ct = default)
    {
        using var http = NewHttp();
        var html = await http.GetStringAsync(pageUrl, ct).ConfigureAwait(false);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<RomListing>();
        foreach (Match m in Regex.Matches(html, @"https?://[^\s""'<>]+"))
        {
            var url = m.Value;
            if (!url.Contains(".tgz") && !url.Contains(".zip") && !url.Contains(".img") && !url.Contains(".ofp")
                && !url.Contains(".ops")) continue;
            if (seen.Add(url))
                results.Add(new RomListing(Uri.UnescapeDataString(Path.GetFileName(new Uri(url).LocalPath)), "", url));
        }
        return results;
    }

    /// <summary>Downloads with the shared aria2 engine (multi-connection).</summary>
    public Task<Aria2DownloadResult> DownloadAsync(string url, string outputPath, int split,
        Action<Aria2DownloadProgress>? progress = null, CancellationToken ct = default)
        => Aria2DownloadService.DownloadAsync(url, outputPath, split, progress, ct);
}
