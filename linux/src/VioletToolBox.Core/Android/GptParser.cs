using System.Buffers.Binary;
using System.Text;

namespace VioletToolBox.Core.Android;

public sealed record GptPartition(
    string Name,
    Guid PartitionGuid,
    Guid TypeGuid,
    ulong FirstLba,
    ulong LastLba,
    ulong Attributes)
{
    public ulong SizeBytes => FirstLba == 0 ? 0 : (LastLba - FirstLba + 1) * 512;
}

/// <summary>
/// Minimal read-only GPT parser for the visual partition tools.
/// Accepts a raw LBA0 dump (protective MBR + GPT) as pulled from /dev/block/by-name/gpt.
/// </summary>
public static class GptParser
{
    private static readonly Guid BasicData = new("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");
    private static readonly Guid AndroidMeta = new("19A710A2-B3CA-11E4-B026-10604B889DCF");

    public static IReadOnlyList<GptPartition> Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2 * 512)
            throw new InvalidDataException("GPT 数据过短，无法解析。");

        // Protective MBR sanity: 0xEE partition type at offset 0x1BE+4.
        if (data[0x1C2] != 0xEE)
            throw new InvalidDataException("未找到 GPT 保护性 MBR，文件可能不是分区表镜像。");

        // GPT header at LBA1.
        var header = data.Slice(512, 92);
        if (Encoding.ASCII.GetString(header.Slice(0, 8)) != "EFI PART")
            throw new InvalidDataException("GPT 头签名无效（EFI PART 缺失）。");

        ulong entryArrayLba = BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(72, 8));
        uint entryCount = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(80, 4));
        uint entrySize = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(84, 4));

        if (entrySize < 128 || entryCount == 0 || entryCount > 1024)
            throw new InvalidDataException($"GPT 分区表项异常（数量 {entryCount}，大小 {entrySize}）。");

        long entryArrayOffset = checked((long)entryArrayLba * 512);
        if (entryArrayOffset + entryCount * entrySize > data.Length)
            throw new InvalidDataException("GPT 表项超出镜像范围，镜像可能被截断。");

        var partitions = new List<GptPartition>((int)entryCount);
        for (uint i = 0; i < entryCount; i++)
        {
            var entry = data.Slice((int)(entryArrayOffset + i * entrySize), 128);
            if (entry.Slice(0, 16).SequenceEqual(stackalloc byte[16])) continue; // zero GUID = unused

            ulong first = BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(32, 8));
            ulong last = BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(40, 8));
            if (first == 0 && last == 0) continue;

            var nameBytes = entry.Slice(56, 72);
            var name = Encoding.Unicode.GetString(nameBytes).TrimEnd('\0');
            if (string.IsNullOrWhiteSpace(name)) continue;

            partitions.Add(new GptPartition(
                name,
                new Guid(entry.Slice(24, 16)),
                new Guid(entry.Slice(0, 16)),
                first, last,
                BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(48, 8))));
        }

        return partitions;
    }

    /// <summary>Type GUID → human label for the most common Android partitions.</summary>
    public static string DescribeType(Guid typeGuid) => typeGuid switch
    {
        var g when g == BasicData => "基本数据",
        var g when g == AndroidMeta => "Android 元数据",
        _ => typeGuid.ToString().ToUpperInvariant(),
    };
}
