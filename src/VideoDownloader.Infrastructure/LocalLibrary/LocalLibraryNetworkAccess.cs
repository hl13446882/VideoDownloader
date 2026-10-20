using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;

namespace VideoDownloader.Infrastructure.LocalLibrary;

internal static class LocalLibraryNetworkAccess
{
    internal static string GetPrefix(int port) => $"http://+:{port}/";

    // Check the real OS configuration each time, rather than trusting a marker that can become stale.
    internal static string BuildScript(int port, string sid, bool configure)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _ = new SecurityIdentifier(sid); // Only a Windows SID is interpolated into the elevated script.
        var prefix = GetPrefix(port);
        var rule = $"VideoDownloader-LocalLibrary-{port}";
        var common = $$"""
            $ErrorActionPreference = 'Stop'
            $netsh = Join-Path $env:SystemRoot 'System32\netsh.exe'
            $acl = (& $netsh http show urlacl 'url={{prefix}}' 2>&1 | Out-String)
            $aclReady = $acl.Contains('{{sid}}')
            """;
        if (!configure)
            return common + "\n" + $$"""
                $rule = Get-NetFirewallRule -Name '{{rule}}' -ErrorAction SilentlyContinue
                if (-not $aclReady -or -not $rule -or $rule.Enabled -ne 'True' -or
                    $rule.Direction -ne 'Inbound' -or $rule.Action -ne 'Allow' -or $rule.Profile -ne 'Any') { exit 1 }
                $ports = $rule | Get-NetFirewallPortFilter
                $addresses = $rule | Get-NetFirewallAddressFilter
                if ($ports.Protocol -notin @('TCP', '6') -or $ports.LocalPort -ne '{{port}}' -or
                    @($addresses.RemoteAddress).Count -ne 1 -or $addresses.RemoteAddress -ne 'LocalSubnet' -or
                    @($addresses.LocalAddress).Count -ne 1 -or $addresses.LocalAddress -ne 'Any') { exit 1 }
                exit 0
                """;
        return common + "\n" + $$"""
            if (-not $aclReady) {
                & $netsh http add urlacl 'url={{prefix}}' 'sddl=D:(A;;GX;;;{{sid}})'
                if ($LASTEXITCODE -ne 0) { exit 1 }
            }
            $rule = Get-NetFirewallRule -Name '{{rule}}' -ErrorAction SilentlyContinue
            if ($rule) { $rule | Remove-NetFirewallRule }
            New-NetFirewallRule -Name '{{rule}}' -DisplayName 'Video Downloader local library ({{port}})' `
                -Direction Inbound -Action Allow -Enabled True -Protocol TCP -LocalPort {{port}} `
                -LocalAddress Any -RemoteAddress LocalSubnet -Profile Any | Out-Null
            exit 0
            """;
    }

    internal static async Task<bool> EnsureConfiguredAsync(int port, ILogger logger, CancellationToken ct)
    {
        try
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value
                ?? throw new InvalidOperationException("Cannot determine the current Windows user.");
            if (await RunAsync(BuildScript(port, sid, configure: false), elevated: false, ct) == 0)
                return true;

            logger.LogInformation("Configuring local library URL permission and LAN firewall rule for port {Port}; Windows administrator consent is required", port);
            if (await RunAsync(BuildScript(port, sid, configure: true), elevated: true, ct) != 0)
                throw new InvalidOperationException("Windows network configuration failed.");
            return await RunAsync(BuildScript(port, sid, configure: false), elevated: false, ct) == 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "LAN access on port {Port} could not be configured; local library will use loopback only", port);
            return false;
        }
    }

    private static async Task<int> RunAsync(string script, bool elevated, CancellationToken ct)
    {
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " +
                Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
            UseShellExecute = elevated,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            Verb = elevated ? "runas" : string.Empty
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start network configuration.");
        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }
}
