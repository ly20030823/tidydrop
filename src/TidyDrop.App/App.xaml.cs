using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace TidyDrop.App;

public partial class App : Application
{
    private Mutex? instance;
    private Mutex? installationGuard;
    private readonly CancellationTokenSource cancellation = new();
    private string pipeName = "";
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.SequenceEqual(["--menu-read-check"]))
        {
            try
            {
                var entries = Task.Run(async () => {
                    var found = new MenuService().Scan();
                    var packaged = await ShellIntegration.InventoryAsync(); found.AddRange(packaged.Entries);
                    return await DynamicMenuProbe.Enrich(found);
                }).GetAwaiter().GetResult();
                Console.Write(JsonSerializer.Serialize(entries)); Shutdown(0);
            }
            catch { Shutdown(1); }
            return;
        }
        if (e.Args.FirstOrDefault() == "--menu-probe") { Shutdown(DynamicMenuProbe.RunHelper(e.Args)); return; }
        if (e.Args.FirstOrDefault() == "--menu-template-change") { Shutdown(MenuService.RunTemplateHelper(e.Args)); return; }
        if (e.Args.SequenceEqual(["--shortcut-check"]))
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "shortcut-check");
            Directory.CreateDirectory(fixture);
            try { DesktopShortcut.Create(fixture); DesktopShortcut.Create(fixture); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(fixture, "error.txt"), ex.ToString()); Shutdown(1); }
            return;
        }
        var smokeTest = e.Args.Contains("--smoke-test");
        pipeName = "TidyDrop-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName + AppContext.BaseDirectory)))[..20];
        if (smokeTest) pipeName += "-checks";
        if (!smokeTest)
        {
            // The installer asks the user to close all copies before updating or uninstalling.
            installationGuard = new Mutex(false, "Local\\TidyDrop.App.Active");
            instance = new Mutex(true, "Local\\" + pipeName, out var first);
            if (!first)
            {
                try
                {
                    using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                    pipe.Connect(3000);
                    using var writer = new StreamWriter(pipe, new UTF8Encoding(false));
                    writer.WriteLine(JsonSerializer.Serialize(e.Args));
                }
                catch (Exception ex) { MessageBox.Show("程序已经在运行，但无法接收这次拖入。请把文件拖入已打开的窗口。\n" + ex.Message, "归序"); }
                Shutdown();
                return;
            }
        }
        try
        {
            var window = new MainWindow(smokeTest);
            MainWindow = window;
            if (smokeTest)
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-args.json"), JsonSerializer.Serialize(e.Args));
                window.Show();
                _ = Listen(window);
                return;
            }
            _ = Listen(window);
            if (e.Args.Length == 0) window.Show();
            else window.HandleIncomingPaths(e.Args, desktop: true);
        }
        catch (Exception ex)
        {
            if (smokeTest) File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-error.txt"), ex.ToString());
            else MessageBox.Show("启动失败。设置和记录已保留，请检查程序所在文件夹的权限或数据文件。\n" + ex.Message, "归序");
            Shutdown(1);
        }
    }

    private async Task Listen(MainWindow window)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellation.Token);
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                var line = await reader.ReadLineAsync(cancellation.Token);
                if (line is not null)
                {
                    var paths = JsonSerializer.Deserialize<string[]>(line) ?? [];
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (paths.Length == 0) { window.Show(); if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal; window.Activate(); }
                        else window.HandleIncomingPaths(paths, desktop: true);
                    });
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { if (cancellation.IsCancellationRequested) return; }
        }
    }

    internal async Task ForwardForCheck(string[] paths)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(3000);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false));
        await writer.WriteLineAsync(JsonSerializer.Serialize(paths));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        cancellation.Cancel();
        instance?.Dispose();
        installationGuard?.Dispose();
        base.OnExit(e);
    }
}
