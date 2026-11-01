using VioletToolBox.Core.Android;
using VioletToolBox.Core.Root;
using Xunit;

namespace VioletToolBox.ConsistencyTests;

/// <summary>
/// Parity tests derived from the upstream audit of 模块专区 / OnePlus AutoRoot /
/// BackupAssistant — the behaviors most easily distorted in a port.
///
/// Upstream ground truth (v3.7):
///   • Manager detection probes: ls /data/adb/ksud | apd | magisk.db (three-way)
///   • Module install: ksud module install | apd module install | magisk --install-module
///     pushed to /data/local/tmp/&lt;name&gt;, then su -c '&lt;cmd&gt; &lt;path&gt;', then rm.
///     Magisk retries with `su -s` when the first attempt fails.
///   • Contacts VCF: `content query --uri content://com.android.contacts/data/phones
///     --projection display_name:data1`, VCF assembled on the PC, pushed to
///     /sdcard/Download/contacts.vcf, pulled back, cleaned up.
///   • Backup: `adb pull "<phone>" "<local>"`, ExitCode==0 = success.
///   • Photo listing: ls /sdcard/DCIM/Camera/ filtered to jpg/jpeg/png.
/// </summary>
public class ModuleAndBackupParityTests : IDisposable
{
    private readonly CommandHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task ModuleZipInstall_DetectsManager_ThenPushesInstallsAndCleansUp()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "vtb-parity", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var zip = Path.Combine(tempDir, "MyModule.zip");
            await File.WriteAllBytesAsync(zip, [0x50, 0x4B, 0x03, 0x04]);

            var svc = new ModuleService();
            await svc.InstallModuleZipAsync("TESTSERIAL", zip);

            // Upstream chain: manager probe → push → su -c install → rm remote.
            _h.AssertScriptOrder(
                "adb -s TESTSERIAL shell \"su -c 'ls /data/adb/ksud 2>/dev/null'\"",
                $"adb -s TESTSERIAL push \"{zip}\" /data/local/tmp/MyModule.zip",
                "adb -s TESTSERIAL shell \"su -c 'ksud module install /data/local/tmp/MyModule.zip'\"",
                "adb -s TESTSERIAL shell \"su -c 'rm /data/local/tmp/MyModule.zip'\"");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ModuleSuccessMarkers_MatchUpstreamSet()
    {
        Assert.True(ModuleService.LooksLikeModuleSuccess("Successfully installed"));
        Assert.True(ModuleService.LooksLikeModuleSuccess("inflating: module.prop"));
        Assert.True(ModuleService.LooksLikeModuleSuccess("安装成功"));
        Assert.False(ModuleService.LooksLikeModuleSuccess("random noise"));
        Assert.True(ModuleService.LooksLikeShellFailure("Permission denied"));
        Assert.True(ModuleService.LooksLikeShellFailure("No such file or directory"));
        Assert.False(ModuleService.LooksLikeShellFailure("all good"));
    }


    [Fact]
    public async Task ContactsBackup_UsesContentQueryProjection()
    {
        var svc = new Core.Backup.BackupService();
        var dir = Path.Combine(Path.GetTempPath(), "vtb-parity-vcf");
        Directory.CreateDirectory(dir);
        try
        {
            await svc.BackupContactsAsync("TESTSERIAL", dir);
            // Upstream queries the phones projection with display_name:data1.
            // (AdbService.ShellAsync wraps the command in quotes for the shell.)
            _h.AssertScriptContains(
                "shell \"content query --uri content://com.android.contacts/data/phones --projection display_name:data1\"");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
