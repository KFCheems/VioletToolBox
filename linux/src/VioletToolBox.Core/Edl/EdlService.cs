using System.IO.Ports;
using SharpEDL;
using SharpEDL.DataClass;

namespace VioletToolBox.Core.Edl;

public sealed record EdlDeviceInfo(string MsmHwid, string OemPkHash, string SerialNum, string SblVersion);

public sealed record EdlPartition(string Label, ulong StartSector, long SectorLen, int Lun, bool Sparse)
{
    public ulong SizeBytes => (ulong)SectorLen * 512;
}

/// <summary>
/// A live Qualcomm EDL (9008) session built on SharpEDL — a fully managed Sahara/Firehose
/// implementation. Works natively on Linux serial ports (/dev/ttyUSB*) so the toolbox no
/// longer needs fh_loader.exe or QSaharaServer.exe.
/// </summary>
public sealed class EdlSession : IDisposable
{
    private readonly SerialPort _port;
    private readonly SaharaServer _sahara;
    private FirehoseServer? _firehose;

    private EdlSession(SerialPort port, SaharaServer sahara, EdlDeviceInfo deviceInfo)
    {
        _port = port;
        _sahara = sahara;
        DeviceInfo = deviceInfo;
    }

    public EdlDeviceInfo DeviceInfo { get; }

    public FirehoseServer Firehose => _firehose ??= new FirehoseServer(_port);

    /// <summary>Candidate EDL serial ports on Linux (need the qcserial/diag kernel driver bound).</summary>
    public static IReadOnlyList<string> ListEdlPorts()
    {
        if (!Directory.Exists("/dev")) return [];
        return Directory.GetFiles("/dev")
            .Where(p => p.StartsWith("/dev/ttyUSB") || p.StartsWith("/dev/ttyACM"))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Opens the serial port, performs the Sahara handshake and uploads the firehose programmer.
    /// The returned session is ready for Firehose operations.
    /// </summary>
    public static EdlSession Connect(string portName, string programmerPath, Action<string>? log = null)
    {
        log?.Invoke($"打开串口 {portName} …");
        var port = new SerialPort(portName);
        port.Open();

        try
        {
            var sahara = new SaharaServer(port);
            log?.Invoke("Sahara 握手（Command 模式）…");
            sahara.DoHelloHandshake(SaharaMode.Command);

            var info = new EdlDeviceInfo(
                MsmHwid: sahara.GetMsmHWID(),
                OemPkHash: sahara.GetOEMPkHash(),
                SerialNum: sahara.GetSerialNum().ToString(),
                SblVersion: sahara.GetSblVersion().ToString());
            log?.Invoke($"MSM HWID: {info.MsmHwid}\nOEM PK Hash: {info.OemPkHash}\nSerial: {info.SerialNum}  SBL: {info.SblVersion}");

            log?.Invoke($"发送 programmer：{Path.GetFileName(programmerPath)} ({new FileInfo(programmerPath).Length} 字节)…");
            sahara.SwitchMode(SaharaMode.ImageTxPending);
            var state = sahara.DoHelloHandshake(SaharaMode.ImageTxPending);
            using var programmer = File.OpenRead(programmerPath);
            sahara.SendProgrammer(state.ImageTransfer, programmer, (uint)programmer.Length);
            sahara.SendDonePacket();
            log?.Invoke("Firehose 已就绪。");

            return new EdlSession(port, sahara, info);
        }
        catch
        {
            port.Dispose();
            throw;
        }
    }

    /// <summary>Reads the full partition table across all LUNs.</summary>
    public IReadOnlyList<EdlPartition> GetPartitions(Action<string>? log = null)
    {
        var list = Firehose.GetPartitionsFromDevice(true);
        var result = list
            .Select(p => new EdlPartition(
                p.Label,
                ulong.TryParse(p.StartSector, out var start) ? start : 0,
                p.SectorLen, p.Lun, p.Sparse))
            .Where(p => !string.IsNullOrEmpty(p.Label))
            .ToList();
        log?.Invoke($"共读取 {result.Count} 个分区。");
        return result;
    }

    /// <summary>Reads a partition into a local file. progress reports 0..1.</summary>
    public void ReadPartition(EdlPartition partition, string outputPath, Action<double>? progress = null)
    {
        var fh = Firehose;
        var handler = MakeProgress(progress);
        fh.ProgressChanged += handler;
        try
        {
            using var output = File.Create(outputPath);
            fh.ReadbackImage(ToSharp(partition), output).CheckAndThrow();
        }
        finally
        {
            fh.ProgressChanged -= handler;
        }
    }

    /// <summary>Writes a raw image into a partition. progress reports 0..1.</summary>
    public void WritePartition(EdlPartition partition, string imagePath, Action<double>? progress = null)
    {
        var fh = Firehose;
        var handler = MakeProgress(progress);
        fh.ProgressChanged += handler;
        try
        {
            using var input = File.OpenRead(imagePath);
            fh.WriteUnsparseImage(ToSharp(partition), input).CheckAndThrow();
        }
        finally
        {
            fh.ProgressChanged -= handler;
        }
    }

    public void ErasePartition(EdlPartition partition, Action<string>? log = null)
    {
        Firehose.ErasePartition(ToSharp(partition)).CheckAndThrow();
        log?.Invoke($"已擦除分区：{partition.Label}");
    }

    /// <summary>Resets the device into the given mode ("reset", "edl", "fastboot", …).</summary>
    public void Reset(string mode = "reset", Action<string>? log = null)
    {
        Firehose.ResetDevice(0, mode).CheckAndThrow();
        log?.Invoke($"已发送重置命令：{mode}");
    }

    private static EventHandler<(long Current, long Total)> MakeProgress(Action<double>? progress)
        => (_, args) => progress?.Invoke(args.Total > 0 ? Math.Clamp(args.Current / (double)args.Total, 0, 1) : 0);

    private static PartitionInfo ToSharp(EdlPartition p) => new()
    {
        Label = p.Label,
        Lun = p.Lun,
        StartSector = p.StartSector.ToString(),
        SectorLen = p.SectorLen,
        BytesPerSector = 512,
        Sparse = p.Sparse,
        FileSectorOffset = 0,
    };

    public void Dispose()
    {
        try { _port.Dispose(); } catch { /* port already gone */ }
    }
}
