using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VioletToolBox.Core.Extraction;
using VioletToolBox.Core.Payload;

namespace VioletToolBox.Linux.ViewModels;

public sealed class ExtractionPageViewModel : ViewModelBase
{
    private readonly Action<string> _log;
    private readonly IFilePickers _pickers;
    private CancellationTokenSource? _cts;

    private string _payloadSource = string.Empty;
    private string _outputDir = string.Empty;
    private double _percent;
    private string _status = "支持 payload.bin / 压缩包 / URL、.ofp、.ops、分段 super 合并。";

    public ExtractionPageViewModel(Action<string> log, IFilePickers pickers)
    {
        _log = log;
        _pickers = pickers;
        PickSourceCommand = new AsyncCommand(PickSourceAsync);
        PickOutputCommand = new AsyncCommand(PickOutputAsync);
        AnalyzeCommand = new AsyncCommand(() => RunAsync(AnalyzeAsync));
        DumpAllCommand = new AsyncCommand(() => RunAsync(DumpAsync));
        ExtractOfpCommand = new AsyncCommand(() => RunAsync(ExtractOfpAsync));
        ExtractOpsCommand = new AsyncCommand(() => RunAsync(ExtractOpsAsync));
        MergeSuperCommand = new AsyncCommand(() => RunAsync(MergeSuperAsync));
        MakeSuperCommand = new AsyncCommand(() => RunAsync(MakeSuperAsync));
    }

    public AsyncCommand PickSourceCommand { get; }
    public AsyncCommand PickOutputCommand { get; }
    public AsyncCommand AnalyzeCommand { get; }
    public AsyncCommand DumpAllCommand { get; }
    public AsyncCommand ExtractOfpCommand { get; }
    public AsyncCommand ExtractOpsCommand { get; }
    public AsyncCommand MergeSuperCommand { get; }
    public AsyncCommand MakeSuperCommand { get; }

    public ObservableCollection<PartitionItem> PayloadPartitions { get; } = [];

    public string PayloadSource { get => _payloadSource; set => SetProperty(ref _payloadSource, value); }
    public string OutputDir { get => _outputDir; set => SetProperty(ref _outputDir, value); }
    public double Percent { get => _percent; set => SetProperty(ref _percent, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    private async Task PickSourceAsync()
    {
        var f = await _pickers.PickOpenFileAsync("选择 payload.bin / zip / ofp / ops / super 分段",
            "bin", "zip", "ofp", "ops", "img").ConfigureAwait(true);
        if (f is not null) PayloadSource = f;
    }

    private async Task PickOutputAsync()
    {
        var d = await _pickers.PickFolderAsync("选择输出目录").ConfigureAwait(true);
        if (d is not null) OutputDir = d;
    }

    private string RequireOutput()
    {
        if (string.IsNullOrWhiteSpace(OutputDir))
            throw new InvalidOperationException("请先选择输出目录。");
        Directory.CreateDirectory(OutputDir);
        return OutputDir;
    }

    private async Task RunAsync(Func<Task> action)
    {
        _cts = new CancellationTokenSource();
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Status = "已取消。";
        }
        catch (Exception ex)
        {
            Status = $"出错：{ex.Message}";
            _log($"[解包] {ex}");
        }
    }

    /// <summary>Reads the payload manifest (local file or URL) and lists partitions.</summary>
    private async Task AnalyzeAsync()
    {
        if (string.IsNullOrWhiteSpace(PayloadSource))
            throw new InvalidOperationException("请先选择来源（文件或 URL）。");

        PayloadPartitions.Clear();
        Status = "正在解析 payload 清单…";
        var ctx = await OpenPayloadContextAsync(PayloadSource).ConfigureAwait(true);
        // The bundled update_metadata.proto only carries partition names + operations,
        // so exact new-partition sizes are not part of the manifest here.
        foreach (var name in ctx.Manifest.Partitions
                     .Select(x => x.PartitionName)
                     .Where(n => !string.IsNullOrEmpty(n))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            PayloadPartitions.Add(new PartitionItem { Name = name, SizeBytes = 0, SizeReadable = "—" });
        }
        Status = $"✓ 清单解析完成，共 {PayloadPartitions.Count} 个分区。";
        _log($"[解包] payload 清单：{PayloadPartitions.Count} 个分区");
    }

    private async Task DumpAsync()
    {
        var outDir = RequireOutput();
        var selected = PayloadPartitions.Where(p => p.IsSelected).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0)
            throw new InvalidOperationException("请先解析清单并勾选要解出的分区。");

        Status = "正在解出分区…";
        var ctx = await OpenPayloadContextAsync(PayloadSource).ConfigureAwait(true);
        var progress = new Progress<(long DoneBytes, long TotalBytes)>(p =>
        {
            Percent = p.TotalBytes > 0 ? p.DoneBytes * 100.0 / p.TotalBytes : 0;
            Status = $"解包中… {Percent:F1}%";
        });
        await PayloadProcessing.ExtractPartitionsAsync(ctx, selected, outDir, workers: 4,
            log: m => _log($"[解包] {m}"), progress, _cts?.Token ?? CancellationToken.None).ConfigureAwait(true);
        Percent = 100;
        Status = $"✓ 已解出 {selected.Count} 个分区到 {outDir}";
    }

    private async Task ExtractOfpAsync()
    {
        var outDir = RequireOutput();
        if (!File.Exists(PayloadSource)) throw new InvalidOperationException("请选择 .ofp 文件。");
        Status = "正在提取 OFP…";
        var progress = new Progress<OfpExtractProgress>(p =>
        {
            Percent = p.Percent;
            Status = $"{p.Message} {p.Percent:F1}%";
        });
        await OppoOfpExtractor.ExtractAsync(PayloadSource, outDir, progress, _cts?.Token ?? CancellationToken.None).ConfigureAwait(true);
        Percent = 100;
        Status = $"✓ OFP 提取完成：{outDir}";
        _log($"[解包] OFP 提取完成：{PayloadSource} → {outDir}");
    }

    private async Task ExtractOpsAsync()
    {
        var outDir = RequireOutput();
        if (!File.Exists(PayloadSource)) throw new InvalidOperationException("请选择 .ops 文件。");
        Status = "正在提取 OPS…";
        var progress = new Progress<OpsExtractProgress>(p =>
        {
            Percent = p.Percent;
            Status = $"{p.Message} {p.Percent:F1}%";
        });
        await OnePlusOpsExtractor.ExtractAsync(PayloadSource, outDir, progress, _cts?.Token ?? CancellationToken.None).ConfigureAwait(true);
        Percent = 100;
        Status = $"✓ OPS 提取完成：{outDir}";
        _log($"[解包] OPS 提取完成：{PayloadSource} → {outDir}");
    }

    private async Task MergeSuperAsync()
    {
        if (!Directory.Exists(PayloadSource))
            throw new InvalidOperationException("来源请填「包含 super_*.bin 分段的目录」（或用文件选择器选目录文件）。");
        var outDir = RequireOutput();
        Status = "正在合并分段 super…";
        var progress = new Progress<OfpSuperMergeProgress>(p =>
        {
            Percent = p.Percent;
            Status = $"{p.Message ?? "合并中…"} {p.Percent:F1}%";
        });
        var result = await OfpSegmentedSuperMerger.MergeAsync(PayloadSource, outDir, progress).ConfigureAwait(true);
        Status = $"✓ 合并完成：{result.OutputPath}";
        _log($"[解包] super 合并完成：{result.OutputPath}");
    }

    private async Task MakeSuperAsync()
    {
        var outDir = RequireOutput();
        if (!Directory.Exists(PayloadSource))
            throw new InvalidOperationException("来源请填解包后的固件目录（含分区镜像）。");
        Status = "正在用 lpmake 构建 super.img…";
        var maker = new SuperMaker(binDir: "", m => _log($"[super] {m}"));
        var ok = await maker.MakeSuperFromDirectoryAsync(PayloadSource, outDir).ConfigureAwait(true);
        Status = ok ? $"✓ super.img 已生成：{outDir}" : "✗ super 构建失败，详见日志。";
        if (ok) Percent = 100;
    }

    private async Task<PayloadContext> OpenPayloadContextAsync(string source)
    {
        var reader = await PayloadProcessing.OpenSourceAsync(source, _cts?.Token ?? CancellationToken.None).ConfigureAwait(true);
        var (payloadReader, _) = await PayloadProcessing.OpenPayloadReaderAsync(reader,
            _cts?.Token ?? CancellationToken.None, log: m => _log($"[解包] {m}")).ConfigureAwait(true);
        return await PayloadProcessing.ReadManifestAsync(payloadReader, _cts?.Token ?? CancellationToken.None).ConfigureAwait(true);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KiB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F1} MiB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GiB",
    };
}
