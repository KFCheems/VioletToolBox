using VioletToolBox.Core.Flash;
using Xunit;

namespace VioletToolBox.ConsistencyTests;

/// <summary>
/// Oujia (OPLUS) line-flash parity against the audited upstream engine (oujiaflash.cs).
///
/// Upstream ground truth highlights:
///   • No hardcoded partition order — every mode flashes images ordered by FILE SIZE
///     ascending (OrderBy FileInfo.Length).
///   • Force (强力线刷) does NOT erase userdata/metadata/frp up front; it rebuilds
///     super via delete-logical-partition {name}_{a,b} + create-logical-partition
///     {target} 40, targeting slot "a", and requires tmp/my_company.img +
///     tmp/my_preload.img additional images.
///   • FRP is erased once at the END (in FastbootD), not per-mode.
///   • Clear-data order: erase userdata → erase metadata → (Qualcomm only) -w →
///     flash misc "tmp/misc.img" (misc only during clear-data, never in the normal
///     flash sequence).
///   • vbmeta gets NO --disable-verity flags in the Oujia engine.
///   • FastbootD detection: getvar is-userspace (yes/true/1), fallback getvar all
///     → super-partition-name.
/// </summary>
public class OujiaFlashParityTests
{
    [Fact]
    public void ScanImages_OrdersByFileSizeAscending()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vtb-oujia", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "vendor.img"), new byte[2048]);   // small
            File.WriteAllBytes(Path.Combine(dir, "system.img"), new byte[8192]);   // large
            File.WriteAllBytes(Path.Combine(dir, "boot.img"), new byte[512]);      // smallest

            var engine = new OujiaFlashService();
            var plan = engine.ScanImages(dir);

            Assert.Equal("boot.img", Path.GetFileName(plan[0].ImagePath));
            Assert.Equal("vendor.img", Path.GetFileName(plan[1].ImagePath));
            Assert.Equal("system.img", Path.GetFileName(plan[2].ImagePath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ScanImages_SkipsPayloadAndGpt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vtb-oujia", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "payload.bin"), new byte[16]);
            File.WriteAllBytes(Path.Combine(dir, "gpt_main0.bin"), new byte[16]);
            File.WriteAllBytes(Path.Combine(dir, "boot.img"), new byte[16]);

            var plan = new OujiaFlashService().ScanImages(dir);
            Assert.Single(plan);
            Assert.Equal("boot", plan[0].Partition);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LogicalPartitionNames_MatchUpstreamList()
    {
        // Upstream OujiaLogicalPartitionNames (oujiaflash.cs:42-50).
        var expected = new[]
        {
            "system", "odm", "vendor", "product", "system_ext", "system_dlkm",
            "vendor_dlkm", "odm_dlkm", "my_bigball", "my_carrier", "my_company",
            "my_engineering", "my_heytap", "my_manifest", "my_preload",
            "my_product", "my_region", "my_stock",
        };
        Assert.Equal(expected.OrderBy(n => n), OujiaFlashService.LogicalPartitionNames.OrderBy(n => n));
    }

    [Fact]
    public void ModeTable_MatchesUpstreamModes()
    {
        // Upstream OujiaFlashMode { Normal, Force, Ab, PureFastbootd } + 售后 FB/FBD.
        Assert.Contains(OujiaFlashService.ModeTable, m => m.Mode == OujiaMode.NormalFull);
        Assert.Contains(OujiaFlashService.ModeTable, m => m.Mode == OujiaMode.StrongFull);
        Assert.Contains(OujiaFlashService.ModeTable, m => m.Mode == OujiaMode.AbFull);
        Assert.Contains(OujiaFlashService.ModeTable, m => m.Mode == OujiaMode.FastbootDOnly);
    }
}
