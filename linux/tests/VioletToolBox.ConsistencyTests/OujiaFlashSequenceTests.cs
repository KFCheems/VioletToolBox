using VioletToolBox.Core.Flash;
using Xunit;

namespace VioletToolBox.ConsistencyTests;

/// <summary>
/// End-to-end command-sequence parity for the Oujia engine's Normal mode.
/// Upstream sequence (audited):
///   devices → (getvar is-userspace) → flash partitions ordered by size →
///   ensure FastbootD → erase frp → [clear-data: erase userdata → erase metadata →
///   flash misc] → reboot.
/// The misc image only appears during clear-data and comes from tools/tmp/misc.img.
/// </summary>
public class OujiaFlashSequenceTests : IDisposable
{
    private readonly CommandHarness _h = new();

    public OujiaFlashSequenceTests()
    {
        // Default fastboot surface: unlocked, A/B device, not in fastbootd initially.
        _h.Device
            .WithVar("unlocked", "yes")
            .WithVar("current-slot", "a")
            .WithVar("is-userspace", "no");
    }

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task NormalFullFlash_FollowsUpstreamPostFlashOrder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vtb-oujia-run", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "boot.img"), new byte[512]);
            File.WriteAllBytes(Path.Combine(dir, "system.img"), new byte[4096]);

            var engine = new OujiaFlashService();
            await engine.RunAsync("TESTSERIAL", dir, OujiaMode.NormalFull,
                clearData: true, autoReboot: true);

            // Partitions ordered by size ascending, then upstream post-flash order:
            // FRP erase (in FastbootD) BEFORE clear-data, which precedes the final reboot.
            _h.AssertScriptOrder(
                "fastboot -s TESTSERIAL flash boot",
                "fastboot -s TESTSERIAL flash system",
                "fastboot -s TESTSERIAL reboot fastboot",
                "fastboot -s TESTSERIAL erase frp",
                "fastboot -s TESTSERIAL erase userdata",
                "fastboot -s TESTSERIAL erase metadata",
                "fastboot -s TESTSERIAL reboot");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task StrongFull_DoesNotErasePartitions_Upfront()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vtb-oujia-run", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "boot.img"), new byte[512]);

            var engine = new OujiaFlashService();
            await engine.RunAsync("TESTSERIAL", dir, OujiaMode.StrongFull,
                clearData: false, autoReboot: false);

            // Upstream Force mode rebuilds super via logical partitions, never erase userdata
            // before flashing. The first erase in the script must be the post-flash frp.
            var script = _h.Script;
            var firstErase = script.FirstOrDefault(l => l.Contains(" erase ")) ?? "";
            Assert.True(firstErase.Contains("erase frp"),
                $"强力线刷不应前置擦除分区，首个 erase 应为收尾 FRP。实际首个: {firstErase}\n序列:\n{string.Join('\n', script)}");

            // Super surgery commands present (upstream create size 40).
            _h.AssertScriptContains("fastboot -s TESTSERIAL delete-logical-partition system_a");
            _h.AssertScriptContains("fastboot -s TESTSERIAL create-logical-partition system_a 40");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task RepairFastbootD_FlashesBothSlots_NoClearDataNoFrp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vtb-oujia-run", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var p in new[] { "boot", "dtbo", "vbmeta" })
                File.WriteAllBytes(Path.Combine(dir, $"{p}.img"), new byte[512]);

            _h.Device.WithVar("is-userspace", "no");
            var engine = new OujiaFlashService();
            await engine.RunAsync("TESTSERIAL", dir, OujiaMode.RepairFastbootD);

            // Upstream repair: each image flashed to _a then _b, ending with reboot fastboot.
            _h.AssertScriptOrder(
                "fastboot -s TESTSERIAL flash boot_a",
                "fastboot -s TESTSERIAL flash boot_b",
                "fastboot -s TESTSERIAL flash vbmeta_a",
                "fastboot -s TESTSERIAL flash vbmeta_b",
                "fastboot -s TESTSERIAL reboot fastboot");

            // Repair does NOT do clear-data or FRP erase.
            Assert.DoesNotContain(_h.Script, l => l.Contains("erase frp"));
            Assert.DoesNotContain(_h.Script, l => l.Contains("erase userdata"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
