using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DnnManager.Infrastructure.Diagnostics;

/// <summary>This PC as DNN Manager and its sites run on it: Windows, and the runtimes IIS hosts DNN with.</summary>
public static class MachineInfo
{
    /// <summary>"Windows 11 Pro 24H2 (build 26100.4061)".</summary>
    public static string Windows
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = key?.GetValue("ProductName") as string ?? RuntimeInformation.OSDescription;
            var build = key?.GetValue("CurrentBuild") as string;
            // Windows 11 still says "Windows 10" in ProductName - its build number tells.
            if (int.TryParse(build, out var number) && number >= 22000) product = product.Replace("Windows 10", "Windows 11");
            var display = key?.GetValue("DisplayVersion") as string;
            var ubr = key?.GetValue("UBR") is int u ? $".{u}" : "";
            return $"{product}{(display is null ? "" : $" {display}")}{(build is null ? "" : $" (build {build}{ubr})")}";
        }
    }

    public static string Architecture => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant() switch
    {
        "x64" => "x64",
        "arm64" => "ARM64",
        "x86" => "x86",
        var other => other
    };

    /// <summary>"4.8.1" - the .NET Framework DNN runs on in IIS.</summary>
    public static string? NetFramework
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full");
            if (key?.GetValue("Release") is not int release) return null;
            var version = release switch
            {
                >= 533320 => "4.8.1",
                >= 528040 => "4.8",
                >= 461808 => "4.7.2",
                >= 461308 => "4.7.1",
                >= 460798 => "4.7",
                >= 394802 => "4.6.2",
                _ => "4.6.1 or older"
            };
            return $"{version} (release {release})";
        }
    }

    /// <summary>"IIS 10.0"; null when IIS isn't installed.</summary>
    public static string? Iis
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\InetStp");
            return key?.GetValue("MajorVersion") is int major ? $"IIS {major}.{key.GetValue("MinorVersion") as int? ?? 0}" : null;
        }
    }
}
