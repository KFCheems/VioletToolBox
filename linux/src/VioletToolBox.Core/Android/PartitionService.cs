using System.Diagnostics;

namespace VioletToolBox.Core.Android;

public sealed record PartitionEntry(string Name, ulong SizeBytes, string Source)
{
    public string SizeDisplay => SizeBytes switch
    {
        0 => "—",
        < 1024 * 1024 => $"{SizeBytes / 1024.0:F1} KiB",
        < 1024L * 1024 * 1024 => $"{SizeBytes / 1024.0 / 1024.0:F1} MiB",
        _ => $"{SizeBytes / 1024.0 / 1024.0 / 1024.0:F2} GiB",
    };
}

public sealed record PartitionProgress(long Bytes, long? TotalBytes, string Message)
{
    public double? Percent => TotalBytes is > 0 ? Bytes * 100.0 / TotalBytes.Value : null;
}

/// <summary>
/// Visual partition read/write/erase.
/// ADB path uses su + dd over /dev/block/by-name (streamed through exec-out, no temp file on host for reads).
/// Fastboot path uses getvar/partition listing, flash/erase/fetch.
/// </summary>
public sealed class PartitionService
{
    private readonly AdbService _adb = new();
    private readonly FastbootService _fastboot = new();

    // ---------- ADB (root) ----------

    public async Task<IReadOnlyList<PartitionEntry>> ListAdbPartitionsAsync(string serial, CancellationToken ct = default)
    {
        var byName = await _adb.SuShellAsync(serial, "ls -1 /dev/block/by-name", timeout: TimeSpan.FromSeconds(20), ct: ct).ConfigureAwait(false);
        var names = byName.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Sizes come from the GPT, which also survives when ls is oddly formatted.
        var gpt = await ReadAdbGptAsync(serial, ct).ConfigureAwait(false);
        var sizeByName = gpt.ToDictionary(p => p.Name, p => p.SizeBytes, StringComparer.OrdinalIgnoreCase);

        return names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n => new PartitionEntry(n, sizeByName.GetValueOrDefault(n), "adb"))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<GptPartition>> ReadAdbGptAsync(string serial, CancellationToken ct = default)
    {
        // GPT lives in LBA0..~LBA34; 34 sectors × 512B covers header + default entry array.
        var data = await ExecOutBytesAsync(serial,
            "su -c \"dd if=/dev/block/by-name/gpt bs=512 count=34 2>/dev/null\"",
            expectedBytes: 34 * 512, ct: ct).ConfigureAwait(false);
        return GptParser.Parse(data);
    }

    /// <summary>Streams a block partition from the device to a local file (requires root).</summary>
    public async Task ReadAdbPartitionAsync(string serial, string partition, string outputPath,
        IProgress<PartitionProgress>? progress = null, CancellationToken ct = default)
    {
        long? total = null;
        try
        {
            var gpt = await ReadAdbGptAsync(serial, ct).ConfigureAwait(false);
            total = (long?)gpt.FirstOrDefault(p => p.Name.Equals(partition, StringComparison.OrdinalIgnoreCase))?.SizeBytes;
        }
        catch
        {
            // Size display is best-effort; the read itself does not depend on it.
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        var adb = _adb.AdbPath ?? throw new InvalidOperationException("未找到 adb。");
        var psi = ProcessRunner.MakeStartInfo(adb,
            $"-s {serial} exec-out su -c \"dd if=/dev/block/by-name/{partition} 2>/dev/null\"");

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
            throw new InvalidOperationException("adb 进程启动失败。");

        var done = 0L;
        var buffer = new byte[4 * 1024 * 1024];
        var lastReport = Stopwatch.StartNew();

        try
        {
            await using var output = File.Create(outputPath);
            var stdout = process.StandardOutput.BaseStream;
            int read;
            while ((read = await stdout.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (lastReport.ElapsedMilliseconds > 120)
                {
                    progress?.Report(new PartitionProgress(done, total, $"正在读取 {partition}…"));
                    lastReport.Restart();
                }
            }
            await output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            }
        }

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        progress?.Report(new PartitionProgress(done, total, $"读取完成：{done} 字节"));
        if (done == 0)
            throw new IOException($"分区 {partition} 读到 0 字节：请确认设备已 root 且分区名正确。");
    }

    /// <summary>Pushes a local image and dd-writes it into a block partition (requires root).</summary>
    public async Task WriteAdbPartitionAsync(string serial, string partition, string imagePath,
        IProgress<PartitionProgress>? progress = null, CancellationToken ct = default)
    {
        var adb = _adb.AdbPath ?? throw new InvalidOperationException("未找到 adb。");
        var remote = "/data/local/tmp/violettoolbox_write.img";

        progress?.Report(new PartitionProgress(0, null, "推送镜像到设备…"));
        var push = await ProcessRunner.RunAsync(adb, $"-s {serial} push \"{imagePath}\" {remote}",
            timeout: TimeSpan.FromMinutes(30), ct: ct).ConfigureAwait(false);
        if (!push.Success)
            throw new IOException($"镜像推送失败：{push.Combined}");

        var size = new FileInfo(imagePath).Length;
        progress?.Report(new PartitionProgress(0, size, $"写入 {partition}…"));
        var write = await _adb.SuShellAsync(serial,
            $"dd if={remote} of=/dev/block/by-name/{partition} bs=4M conv=fsync",
            timeout: TimeSpan.FromMinutes(30), ct: ct).ConfigureAwait(false);

        await _adb.ShellAsync(serial, $"rm -f {remote}", ct: ct).ConfigureAwait(false);

        if (write.Length == 0)
            throw new IOException($"写入命令没有输出，可能失败。设备回显：{write}");
        progress?.Report(new PartitionProgress(size, size, $"写入完成：{partition}"));
    }

    // ---------- Fastboot ----------

    public async Task<IReadOnlyList<PartitionEntry>> ListFastbootPartitionsAsync(string serial, CancellationToken ct = default)
    {
        var vars = await _fastboot.GetAllVarsAsync(serial, ct).ConfigureAwait(false);
        var entries = new List<PartitionEntry>();
        foreach (var (key, value) in vars)
        {
            var m = System.Text.RegularExpressions.Regex.Match(key, @"^partition-type:(?<name>.+)$");
            if (!m.Success) continue;
            var sizeKey = $"partition-size:{m.Groups["name"].Value}";
            var size = vars.TryGetValue(sizeKey, out var raw) ? ParseHexOrDecimal(raw) : 0;
            entries.Add(new PartitionEntry(m.Groups["name"].Value, size, "fastboot"));
        }
        return entries.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Task WriteFastbootPartitionAsync(string serial, string partition, string imagePath,
        IProgress<PartitionProgress>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(new PartitionProgress(0, null, $"fastboot 刷入 {partition}…"));
        return _fastboot.FlashAsync(serial, partition, imagePath, ct);
    }

    // ---------- helpers ----------

    private async Task<byte[]> ExecOutBytesAsync(string serial, string command,
        int? expectedBytes = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var adb = _adb.AdbPath ?? throw new InvalidOperationException("未找到 adb。");
        var psi = ProcessRunner.MakeStartInfo(adb, $"-s {serial} exec-out {command}");
        using var process = new Process { StartInfo = psi };
        if (!process.Start())
            throw new InvalidOperationException("adb 进程启动失败。");

        using var ms = new MemoryStream(expectedBytes ?? 4096);
        var stdout = process.StandardOutput.BaseStream;
        var buffer = new byte[256 * 1024];
        int read;
        while ((read = await stdout.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            ms.Write(buffer, 0, read);

        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        return ms.ToArray();
    }

    private static ulong ParseHexOrDecimal(string raw)
        => ulong.TryParse(raw.Replace("0x", "").Trim(), System.Globalization.NumberStyles.HexNumber, null, out var hex)
            ? hex
            : ulong.TryParse(raw.Trim(), out var dec) ? dec : 0;
}
