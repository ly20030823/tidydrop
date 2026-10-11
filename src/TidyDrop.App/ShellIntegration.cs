using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace TidyDrop.App;

internal sealed record ContextMenuStatus(bool Supported, bool Available, bool Installed, bool Checked, string Detail);

internal static class ShellIntegration
{
    public static bool NeedsLocalTrust()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Shell", "TidyDrop.CopyPath.cer");
        if (!File.Exists(path)) return false;
        using var certificate = X509CertificateLoader.LoadCertificateFromFile(path);
        using var trusted = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
        trusted.Open(OpenFlags.ReadOnly);
        return trusted.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, false).Count == 0;
    }

    public static ContextMenuStatus InitialStatus()
    {
        var supported = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) && RuntimeInformation.OSArchitecture == Architecture.X64;
        var available = supported && new[] { "TidyDrop.Shell.dll", "TidyDrop.CopyPath.msix", "TidyDrop.CopyPath.cer", "context-menu.ps1" }.All(name => File.Exists(Path.Combine(AppContext.BaseDirectory, "Shell", name)));
        return new(supported, available, false, false, supported ? "正在检查右键菜单…" : "此功能需要 Windows 11 的 x64 系统");
    }

    public static async Task<ContextMenuStatus> RunAsync(string action)
    {
        if (action is not ("status" or "install" or "remove")) throw new ArgumentException("未知菜单操作。");
        var script = Path.Combine(AppContext.BaseDirectory, "Shell", "context-menu.ps1");
        if (!File.Exists(script)) return InitialStatus() with { Checked = true, Detail = "此版本未包含菜单组件" };
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-ExecutionPolicy", "Bypass", "-File", script, "-Action", action }) start.ArgumentList.Add(argument);
        // Installation callers obtain explicit consent before the script requests UAC elevation.
        if (action == "install") start.ArgumentList.Add("-TrustConsent");
        using var process = Process.Start(start) ?? throw new IOException("无法启动菜单组件注册。");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var text = await output;
        var errors = await error;
        if (string.IsNullOrWhiteSpace(text)) throw new IOException(string.IsNullOrWhiteSpace(errors) ? "Windows 未返回菜单注册结果。" : errors.Trim());
        using var result = JsonDocument.Parse(text);
        if (!result.RootElement.GetProperty("ok").GetBoolean()) throw new IOException(result.RootElement.GetProperty("error").GetString());
        return result.RootElement.GetProperty("status").Deserialize<ContextMenuStatus>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new IOException("无法读取菜单状态。");
    }
}
