using System.Diagnostics;
using System.Text;
using VideoDownloader.Infrastructure.LocalLibrary;

namespace VideoDownloader.Infrastructure.Tests;

public sealed class LocalLibraryNetworkAccessTests
{
    private const string UserSid = "S-1-5-21-100-200-300-1001";

    [Fact]
    public void Prefix_DoesNotDependOnAdapterAddress()
    {
        Assert.Equal("http://+:17890/", LocalLibraryNetworkAccess.GetPrefix(LocalLibraryHost.PreferredPort));
    }

    [Fact]
    public void Configuration_GrantsOnlyCurrentUserUrlAccessAndLocalSubnetFirewallAccess()
    {
        var script = LocalLibraryNetworkAccess.BuildScript(17890, UserSid, configure: true);
        Assert.Contains($"sddl=D:(A;;GX;;;{UserSid})", script);
        Assert.Contains("-RemoteAddress LocalSubnet -Profile Any", script);
        Assert.Contains("-Protocol TCP -LocalPort 17890", script);
        Assert.DoesNotContain("portproxy", script);
        Assert.DoesNotContain("Restart-Service", script);
    }

    [Fact]
    public void Check_ValidatesActualAclAndFirewallWithoutChangingThem()
    {
        var script = LocalLibraryNetworkAccess.BuildScript(17890, UserSid, configure: false);
        Assert.Contains("$acl.Contains('" + UserSid + "')", script);
        Assert.Contains("Get-NetFirewallPortFilter", script);
        Assert.Contains("Get-NetFirewallAddressFilter", script);
        Assert.Contains("$rule.Enabled -ne 'True'", script);
        Assert.Contains("$rule.Action -ne 'Allow'", script);
        Assert.DoesNotContain("New-NetFirewallRule", script);
        Assert.DoesNotContain("add urlacl", script);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void Configuration_RejectsInvalidPort(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LocalLibraryNetworkAccess.BuildScript(port, UserSid, true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedScript_IsValidWindowsPowerShell(bool configure)
    {
        var script = LocalLibraryNetworkAccess.BuildScript(17890, UserSid, configure);
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var parse = " $source = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('" + encoded + "')); " +
            "$tokens = $null; $errors = $null; " +
            "[void][System.Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors); " +
            "if ($errors.Count) { $errors | Out-String | Write-Output; exit 1 }; exit 0";
        Assert.Equal(0, await RunPowerShellAsync(parse));
    }

    [Fact]
    public async Task Check_ReturnsFailureForUnconfiguredEndpointWithoutPromptingOrMutatingWindows()
    {
        Assert.Equal(1, await RunPowerShellAsync(LocalLibraryNetworkAccess.BuildScript(65432, UserSid, false)));
    }

    private static async Task<int> RunPowerShellAsync(string script)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments = "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        return process.ExitCode;
    }
}
