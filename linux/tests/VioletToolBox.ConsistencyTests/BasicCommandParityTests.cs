using VioletToolBox.Core.Android;
using Xunit;

namespace VioletToolBox.ConsistencyTests;

/// <summary>
/// Command-level parity: our AdbService/FastbootService must emit the same
/// adb/fastboot invocations the upstream Windows build issues for each feature.
///
/// Upstream ground truth (Smart-Paocai/VioletToolBox v3.7, audited from
/// MainWindow.xaml.cs / oujiaflash.cs):
///   • adb devices -l                    — device detection
///   • fastboot devices                  — ditto
///   • fastboot -s S getvar VAR          — single var reads (result on stderr)
///   • fastboot -s S flash P "PATH"      — plain flash, quoted path
///   • fastboot -s S erase P             — plain erase (userdata/metadata/frp…)
///   • fastboot -s S set_active a|b      — slot switch
///   • fastboot -s S reboot [target]     — reboot system/recovery/bootloader/fastbootd/edl
///   • adb -s S shell getprop            — device info (ro.product.model etc.)
/// </summary>
public class BasicCommandParityTests : IDisposable
{
    private readonly CommandHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Adb_DeviceDetection_MatchesUpstream()
    {
        var adb = new AdbService();
        await adb.EnsureServerAsync();
        var devices = await adb.DevicesAsync();

        Assert.Single(devices);
        Assert.Equal("TESTSERIAL", devices[0].Serial);
        // Upstream runs `adb devices -l` for detection; start-server precedes it.
        _h.AssertScriptOrder("adb start-server", "adb devices -l");
    }

    [Fact]
    public async Task Fastboot_DeviceDetection_MatchesUpstream()
    {
        var fb = new FastbootService();
        var devices = await fb.DevicesAsync();

        Assert.Single(devices);
        Assert.Equal("TESTSERIAL", devices[0].Serial);
        _h.AssertScriptContains("fastboot devices");
    }

    [Fact]
    public async Task Fastboot_Flash_UsesPlainFlashWithQuotedPath()
    {
        var fb = new FastbootService();
        var result = await fb.FlashAsync("TESTSERIAL", "boot", "/tmp/images/boot.img");

        Assert.True(result.Success);
        // Upstream: fastboot flash boot "path" (serial via -s). Same shape modulo exe path.
        _h.AssertScriptContains("fastboot -s TESTSERIAL flash boot \"/tmp/images/boot.img\"");
    }

    [Fact]
    public async Task Fastboot_Erase_UsesPlainErase_NoFlags()
    {
        var fb = new FastbootService();
        await fb.EraseAsync("TESTSERIAL", "frp");

        _h.AssertScriptContains("fastboot -s TESTSERIAL erase frp");
    }

    [Fact]
    public async Task Fastboot_SetActive_MatchesUpstream()
    {
        var fb = new FastbootService();
        await fb.SetActiveAsync("TESTSERIAL", "a");

        _h.AssertScriptContains("fastboot -s TESTSERIAL set_active a");
    }

    [Theory]
    [InlineData("", "reboot")]
    [InlineData("recovery", "reboot recovery")]
    [InlineData("bootloader", "reboot bootloader")]
    [InlineData("fastbootd", "reboot fastbootd")]
    [InlineData("edl", "reboot edl")]
    public async Task Fastboot_RebootTargets_MatchUpstream(string target, string expectedArgs)
    {
        var fb = new FastbootService();
        await fb.RebootAsync("TESTSERIAL", target);

        _h.AssertScriptContains($"fastboot -s TESTSERIAL {expectedArgs}");
    }

    [Theory]
    [InlineData("", "reboot")]
    [InlineData("recovery", "reboot recovery")]
    [InlineData("bootloader", "reboot bootloader")]
    public async Task Adb_RebootTargets_MatchUpstream(string target, string expectedArgs)
    {
        var adb = new AdbService();
        await adb.RebootAsync("TESTSERIAL", target);

        _h.AssertScriptContains($"adb -s TESTSERIAL {expectedArgs}");
    }

    [Fact]
    public async Task Fastboot_GetVar_ReadsSingleVariable()
    {
        _h.Device.WithVar("current-slot", "a");
        var fb = new FastbootService();
        var value = await fb.GetVarAsync("TESTSERIAL", "current-slot");

        Assert.Equal("a", value);
        _h.AssertScriptContains("fastboot -s TESTSERIAL getvar current-slot");
    }

    [Fact]
    public async Task Fastboot_GetAllVars_AggregatesFromStderr()
    {
        _h.Device
            .WithVar("current-slot", "b")
            .WithVar("unlocked", "yes");
        var fb = new FastbootService();
        var vars = await fb.GetAllVarsAsync("TESTSERIAL");

        Assert.Equal("b", vars["current-slot"]);
        Assert.Equal("yes", vars["unlocked"]);
        _h.AssertScriptContains("fastboot -s TESTSERIAL getvar all");
    }

    [Fact]
    public async Task Adb_DeviceInfo_UsesGetprop()
    {
        _h.Device
            .WithProp("ro.product.model", "PJD110")
            .WithProp("ro.product.brand", "OnePlus");
        var adb = new AdbService();
        var props = await adb.GetPropsAsync("TESTSERIAL");

        Assert.Equal("PJD110", props["ro.product.model"]);
        Assert.Equal("OnePlus", props["ro.product.brand"]);
        _h.AssertScriptContains("adb -s TESTSERIAL shell getprop");
    }

    [Fact]
    public async Task Adb_RootCheck_UsesSuId()
    {
        var adb = new AdbService();
        var hasRoot = await adb.HasRootAsync("TESTSERIAL");

        Assert.True(hasRoot);
        _h.AssertScriptContains("adb -s TESTSERIAL shell su -c id");
    }
}
