using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VioletToolBox.Core.Download;

namespace VioletToolBox.Linux.ViewModels;

public sealed record RomRow(string Name, string Size, string Url);

/// <summary>下载专区: 联想查包 / Rom 获取 / 直链抓取.</summary>
public sealed class DownloadCenterPageViewModel : ViewModelBase
{
    private readonly DownloadCenterService _center = new();
    private readonly Action<string> _log;

    private string _source = "lenovo";
    private string _keyword = "Y700三代";
    private RomRow? _selected;
    private double _percent;
    private string _status = "联想查包支持 Y700 二/三/四/五代预设；Rom 获取走 violettool 后端。";

    public DownloadCenterPageViewModel(Action<string> log)
    {
        _log = log;
        QueryCommand = new AsyncCommand(QueryAsync);
        DownloadCommand = new AsyncCommand(() => RunAsync(DownloadAsync));
    }

    public AsyncCommand QueryCommand { get; }
    public AsyncCommand DownloadCommand { get; }

    public ObservableCollection<string> Sources { get; } = new(["lenovo", "violet", "url"]);
    public ObservableCollection<string> LenovoPresets { get; } = new();
    public ObservableCollection<RomRow> Results { get; } = [];

    public string Source { get => _source; set => SetProperty(ref _source, value); }
    public string Keyword { get => _keyword; set => SetProperty(ref _keyword, value); }
    public RomRow? Selected { get => _selected; set => SetProperty(ref _selected, value); }
    public double Percent { get => _percent; set => SetProperty(ref _percent, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    private async Task QueryAsync()
    {
        Results.Clear();
        Status = "查询中…";
        var list = Source switch
        {
            "lenovo" => await _center.QueryLenovoAsync(Keyword).ConfigureAwait(true),
            "violet" => await _center.QueryVioletRomAsync(Keyword).ConfigureAwait(true),
            _ => await _center.ExtractLinksFromPageAsync(Keyword).ConfigureAwait(true), // "url": 直接抓页面链接
        };
        foreach (var r in list) Results.Add(new RomRow(r.Name, r.Extra ?? "", r.Url));
        Status = $"✓ {list.Count} 条结果。";
    }

    private async Task DownloadAsync()
    {
        if (Selected is null) throw new InvalidOperationException("请先选择要下载的固件。");
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "VioletToolBox");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, Selected.Name);
        _log($"[下载] {Selected.Url} → {target}");
        Status = "多连接下载中…";
        var result = await _center.DownloadAsync(Selected.Url, target, 16,
            p => { Percent = p.Percentage; Status = $"下载中… {p.Percentage:F1}%  {p.SpeedText}"; })
            .ConfigureAwait(true);
        Status = result.Succeeded ? $"✓ 已下载：{target}" : $"✗ {result.ErrorMessage}";
    }

    private async Task RunAsync(Func<Task> action)
    {
        try { await action().ConfigureAwait(true); }
        catch (Exception ex) { Status = $"出错：{ex.Message}"; _log($"[下载] {ex}"); }
    }
}
