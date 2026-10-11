using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using TidyDrop.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace TidyDrop.App;

public partial class MainWindow : Window
{
    private readonly Store store;
    private readonly Organizer organizer;
    private readonly ArchiveCatalog catalog;
    private readonly Settings settings;
    private readonly List<HistoryEntry> history;
    private readonly List<string> queued = [];
    private IReadOnlyList<PlanItem> plan = [];
    private List<FileDisplay> items = [];
    private List<ArchiveItem> library = [];
    private string libraryRoot = "";
    private bool ready;
    private bool busy;
    private bool initialized;
    private bool isError;
    private string message = "";
    private ContextMenuStatus contextMenu = ShellIntegration.InitialStatus();
    private int contextMenuRevision;
    private readonly MenuService menuRegistry = new();
    private MenuAnalysis? menuAnalysis;
    private List<MenuEntry> systemMenus = [];
    private bool menusLoading;
    private string menusDetail = "";
    private readonly bool smokeTest;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public MainWindow(bool smokeTest = false)
    {
        this.smokeTest = smokeTest;
        store = new Store(Path.Combine(AppContext.BaseDirectory, smokeTest ? "smoke-data" : "data"));
        settings = smokeTest ? new Settings { RootFolder = Path.Combine(AppContext.BaseDirectory, "smoke-initial", Guid.NewGuid().ToString("N")) } : store.LoadSettings();
        catalog = new ArchiveCatalog(settings);
        organizer = new Organizer(store, catalog);
        history = smokeTest ? [] : store.LoadHistory();
        if (!smokeTest) organizer.Recover(history);
        InitializeComponent();
        if (smokeTest)
        {
            contextMenu = new(true, true, false, true, "界面检查用状态，未修改系统菜单");
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -20000;
            Top = -20000;
            history.Add(new HistoryEntry { Source = @"C:\桌面\项目说明.docx", Destination = @"D:\我的材料\文档\项目说明.docx", State = "已归档", RuleName = "文档" });
            history.Add(new HistoryEntry { Source = @"C:\桌面\参考照片.png", Destination = @"D:\我的材料\图片\参考照片.png", State = "已归档", RuleName = "图片" });
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (initialized) return;
        initialized = true;
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppContext.BaseDirectory, smokeTest ? "smoke-webview" : "data", "webview"));
            await Browser.EnsureCoreWebView2Async(environment);
            var core = Browser.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping("tidydrop.local", Path.Combine(AppContext.BaseDirectory, "Web"), CoreWebView2HostResourceAccessKind.DenyCors);
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = smokeTest;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.NavigationStarting += (_, args) => { if (!Trusted(args.Uri)) args.Cancel = true; };
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, args) =>
            {
                if (!Trusted(args.Request.Uri)) args.Response = environment.CreateWebResourceResponse(null, 403, "Blocked", "");
            };
            core.WebMessageReceived += WebMessage;
            core.NavigationCompleted += (_, args) =>
            {
                if (!args.IsSuccess) Fail(new IOException("本地界面加载失败：" + args.WebErrorStatus));
            };
            core.Navigate("https://tidydrop.local/index.html");
        }
        catch (Exception ex) { Fail(ex); }
    }

    private static bool Trusted(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host == "tidydrop.local";

    private async void WebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!Trusted(args.Source)) return;
        try
        {
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            var root = document.RootElement;
            var command = root.GetProperty("command").GetString();
            if (command == "ready")
            {
                ready = true;
                LoadingText.Visibility = Visibility.Collapsed;
                await RefreshLibrary();
                if (!smokeTest) _ = UpdateContextMenuStatus();
                if (smokeTest) _ = CheckWebInterface();
                else await ProcessQueued();
                return;
            }
            if (command == "drop")
            {
                HandleIncomingPaths(args.AdditionalObjects.OfType<CoreWebView2File>().Select(file => file.Path));
                return;
            }
            if (busy) { Notify("正在处理文件，请稍候。新拖入的文件会排队整理。"); return; }
            switch (command)
            {
                case "pickRoot":
                    var folderPicker = new OpenFolderDialog { Title = "选择归档总文件夹" };
                    if (Directory.Exists(settings.RootFolder)) folderPicker.InitialDirectory = settings.RootFolder;
                    if (folderPicker.ShowDialog(this) == true)
                    {
                        var old = settings.RootFolder;
                        settings.RootFolder = folderPicker.FolderName;
                        try { store.SaveSettings(settings); }
                        catch { settings.RootFolder = old; throw; }
                        Notify("归档位置已保存。之后把文件拖进来就好。");
                        await RefreshLibrary();
                        await Repreview();
                    }
                    break;
                case "pickFiles":
                    var filePicker = new OpenFileDialog { Title = "选择要整理的文件", Multiselect = true };
                    if (filePicker.ShowDialog(this) == true) HandleIncomingPaths(filePicker.FileNames);
                    break;
                case "pickFolder":
                    var sourcePicker = new OpenFolderDialog { Title = "选择要整包收纳的文件夹" };
                    if (sourcePicker.ShowDialog(this) == true) HandleIncomingPaths([sourcePicker.FolderName]);
                    break;
                case "refreshLibrary": await RefreshLibrary(rebuild: true); await ProcessQueued(); break;
                case "contextMenuStatus": await UpdateContextMenuStatus(); break;
                case "installContextMenu": await ChangeContextMenu(true); break;
                case "removeContextMenu": await ChangeContextMenu(false); break;
                case "repairContextMenu": await ChangeContextMenu(true, true); break;
                case "systemMenus": await RefreshSystemMenus(); break;
                case "analyzeMenuFile":
                    var fileDialog = new OpenFileDialog { Title = "选择需要分析右键菜单的文件", CheckFileExists = true };
                    if (fileDialog.ShowDialog(this) == true) { menuAnalysis = MenuService.Analyze(fileDialog.FileName); SendState(); }
                    break;
                case "analyzeMenuFolder":
                    var folderDialog = new OpenFolderDialog { Title = "选择需要分析右键菜单的文件夹" };
                    if (folderDialog.ShowDialog(this) == true) { menuAnalysis = MenuService.Analyze(folderDialog.FolderName); SendState(); }
                    break;
                case "clearMenuAnalysis": menuAnalysis = null; SendState(); break;
                case "copyMenuRegistration":
                    var registration = systemMenus.FirstOrDefault(e => e.Id == root.GetProperty("id").GetString())?.Registration;
                    if (!string.IsNullOrWhiteSpace(registration)) { Clipboard.SetText(registration); message = "菜单来源位置已复制。"; SendState(); }
                    break;
                case "toggleSystemMenu": await ToggleSystemMenu(root.GetProperty("id").GetString() ?? "", root.GetProperty("enabled").GetBoolean()); break;
                case "toggleSystemMenus": await ToggleSystemMenus(root.GetProperty("ids").EnumerateArray().Select(e => e.GetString() ?? "").Distinct().ToArray(), root.GetProperty("enabled").GetBoolean()); break;
                case "restoreSystemMenus": await RestoreSystemMenus(); break;
                case "reveal":
                    var archiveId = root.GetProperty("id").GetString();
                    var archive = library.FirstOrDefault(item => item.Id == archiveId);
                    if (archive is not null && Organizer.Exists(archive.Path))
                    {
                        await ExplorerNavigation.RevealAsync(archive.Path);
                    }
                    else Notify("这个项目已被移走，请点击「刷新」。", true);
                    break;
                case "saveSettings":
                    var requested = root.GetProperty("settings");
                    var oldAuto = settings.AutoOrganize;
                    settings.AutoOrganize = requested.GetProperty("autoOrganize").GetBoolean();
                    try { store.SaveSettings(settings); }
                    catch { settings.AutoOrganize = oldAuto; throw; }
                    SendState();
                    await Repreview();
                    break;
                case "organize": await RunPlan(); break;
                case "undo":
                    var id = root.GetProperty("id").GetString();
                    var entry = history.FirstOrDefault(item => item.Id == id);
                    if (entry is not null) await Undo(entry);
                    break;
                case "shortcut":
                    DesktopShortcut.Create();
                    Notify("桌面入口已就绪。把文件拖到「归序」图标上，即可自动整理。");
                    break;
                case "openRoot":
                    if (Directory.Exists(settings.RootFolder)) Process.Start(new ProcessStartInfo(settings.RootFolder) { UseShellExecute = true });
                    else Notify("首次整理时会建立归档文件夹；也可以现在更改归档位置。");
                    break;
                case "dropUnsupported":
                    Notify("当前 WebView2 版本不支持窗口拖入。可以使用「选择文件」或桌面快捷方式整理。", true);
                    break;
            }
        }
        catch (Exception ex) { Notify("操作未完成：" + ex.Message, true); }
    }

    public async void HandleIncomingPaths(IEnumerable<string> paths, bool desktop = false)
    {
        var incoming = paths.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
        if (incoming.Length == 0) return;
        if (!smokeTest && !desktop)
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }
        queued.AddRange(incoming);
        if (ready && !desktop) Browser.CoreWebView2.PostWebMessageAsJson("{\"type\":\"navigate\",\"page\":\"organize\"}");
        if (busy) { Notify("新的文件已收到，当前整理完成后会继续处理。"); return; }
        try { await ProcessQueued(); }
        catch (Exception ex) { queued.Clear(); Notify(ex.Message, true); }
        finally
        {
            if (!smokeTest && !IsVisible && !busy && queued.Count == 0)
            {
                if (isError) MessageBox.Show("请打开归序查看整理结果。\n" + message, "归序");
                Application.Current.Shutdown(isError ? 1 : 0);
            }
        }
    }

    private async Task ProcessQueued()
    {
        if (busy || queued.Count == 0) return;
        if (string.IsNullOrWhiteSpace(settings.RootFolder))
        {
            Notify($"已收到 {queued.Count} 个项目。请先选择一个归档总文件夹。");
            if (!IsVisible) Show();
            return;
        }
        if (!settings.AutoOrganize && !IsVisible) { Show(); return; }
        var paths = queued.ToArray();
        queued.Clear();
        if (items.Any(item => item.Status == "准备归档")) paths = plan.Select(item => item.Source).Concat(paths).ToArray();
        plan = organizer.Preview(paths, settings);
        items = plan.Select(item => new FileDisplay(item.Source, item.FileName, item.RuleName, item.Destination ?? "留在原位置", item.Message, item.CanMove)).ToList();
        message = "";
        isError = false;
        SendState();
        if (settings.AutoOrganize && plan.Count > 0) await RunPlan();
    }

    private async Task Repreview()
    {
        if (items.Any(item => item.Status == "准备归档"))
        {
            queued.AddRange(plan.Select(item => item.Source));
            plan = [];
            items = [];
        }
        await ProcessQueued();
    }

    private async Task RunPlan()
    {
        if (busy || plan.Count == 0) return;
        busy = true;
        Notify("正在整理文件，请稍候…");
        try
        {
            var results = await Task.Run(() => organizer.Execute(plan, history));
            for (var i = 0; i < items.Count; i++) items[i].Status = results[i].Message;
            var count = results.Count(result => result.Success);
            message = $"整理完成：{count} 个项目已归档，{results.Count - count} 个项目未移动。";
            isError = results.Any(result => !result.Success && !Organizer.ExpectedSkip(result.Message));
            plan = [];
        }
        finally { busy = false; await RefreshLibrary(); }
        await ProcessQueued();
    }

    private async Task Undo(HistoryEntry entry)
    {
        busy = true;
        Notify("正在核对文件，准备恢复原位置…");
        try
        {
            var result = await Task.Run(() => organizer.Undo(entry, history));
            message = entry.FileName + "：" + result.Message;
            isError = !result.Success;
        }
        finally { busy = false; await RefreshLibrary(); }
        await ProcessQueued();
    }

    private void Notify(string text, bool error = false)
    {
        message = text;
        isError = error;
        SendState();
    }

    private void SendState()
    {
        if (!ready) return;
        Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            type = "state",
            state = new { settings, items, history, library, contextMenu, systemMenus, menuAnalysis, menusLoading, menusDetail, busy, message, isError, pendingCount = queued.Count }
        }, JsonOptions));
    }

    private async Task RefreshLibrary(bool rebuild = false)
    {
        busy = true;
        if (rebuild) { message = "正在刷新材料…"; isError = false; }
        SendState();
        var records = history.ToArray();
        try
        {
            var loaded = await Task.Run(() => catalog.Load(records, rebuild));
            library = loaded;
            libraryRoot = settings.RootFolder;
            if (rebuild) message = $"已刷新，共 {library.Count} 份材料。";
        }
        catch (Exception ex)
        {
            if (!libraryRoot.Equals(settings.RootFolder, StringComparison.OrdinalIgnoreCase)) library = [];
            message = "材料清单读取未完成：" + ex.Message;
            isError = true;
        }
        finally { busy = false; SendState(); }
    }

    private async Task UpdateContextMenuStatus()
    {
        var revision = ++contextMenuRevision;
        try
        {
            var status = await RunContextMenuAction("status");
            if (revision == contextMenuRevision) contextMenu = status;
        }
        catch (Exception ex)
        {
            if (revision == contextMenuRevision) contextMenu = ShellIntegration.InitialStatus() with { Checked = true, Detail = "菜单状态未读取：" + ex.Message };
        }
        SendState();
    }

    private async Task ChangeContextMenu(bool install, bool repair = false)
    {
        if (!smokeTest && install && ShellIntegration.NeedsLocalTrust()
            && MessageBox.Show(this, "此个人开发版需要 Windows 信任随附的归序签名，才能添加一级右键菜单。\n\n继续会请求管理员确认，将公开证书加入本机的「受信任人」证书库。此信任对所有账户生效，也适用于同一私钥签名的其他安装包；菜单仅注册给当前账户。不会加入根证书库。\n\n移除菜单时会清理归序新增的信任；其他账户仍使用此菜单时保留。\n\n是否信任归序签名并添加菜单？", "添加复制路径菜单", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        ++contextMenuRevision;
        busy = true; isError = false;
        message = repair ? "正在重新注册并刷新归序菜单…" : install ? "正在添加右键菜单…" : "正在移除右键菜单…";
        SendState();
        try
        {
            contextMenu = await RunContextMenuAction(repair ? "repair" : install ? "install" : "remove");
            message = install ? "已注册「复制文件路径」并通知 Windows 刷新。若文件夹内仍不显示，请关闭并重开文件夹窗口；仍无效时退出并重新登录 Windows。" : "归序的右键菜单已移除。";
        }
        catch (Exception ex)
        {
            message = "右键菜单操作未完成：" + ex.Message; isError = true;
            await UpdateContextMenuStatus();
        }
        finally { busy = false; SendState(); await ProcessQueued(); }
    }

    private Task<ContextMenuStatus> RunContextMenuAction(string action) => smokeTest
        ? Task.FromResult(contextMenu with { Installed = action == "status" ? contextMenu.Installed : action is "install" or "repair", Checked = true })
        : ShellIntegration.RunAsync(action);

    private async Task RefreshSystemMenus()
    {
        if (menusLoading) return;
        menusLoading = true; menusDetail = ""; SendState();
        try
        {
            if (smokeTest)
            {
                if (systemMenus.Count == 0) systemMenus = [
                    new("fixture-menu", "图片转换", "WPS Office", "所有文件", "静态命令", true, true, false, "模拟项目，不修改系统。", "fixture-command", "*"),
                    new("fixture-menu-folder", "图片转换", "WPS Office", "文件夹", "静态命令", true, true, false, "模拟文件夹位置。", "fixture-command", "Directory"),
                    new("fixture-menu-desktop", "图片转换", "WPS Office", "桌面空白处", "静态命令", true, true, false, "模拟桌面位置。", "fixture-command", "DesktopBackground"),
                    new("fixture-other-command", "图片转换", "WPS Office", "特定类型 · PNG 文件", "静态命令", true, false, false, "同名的另一命令，仅查看。", "different-command", "fixture.type"),
                    new("fixture-baidu", "上传到百度网盘", "百度网盘", "文件夹", "扩展组件", true, false, false, "模拟只读项目，不修改系统。", "baidu-command", "Directory")];
                return;
            }
            var registry = Task.Run(menuRegistry.Scan);
            var packaged = ShellIntegration.InventoryAsync();
            // Await both, including failures, before publishing a complete or partial result.
            try { systemMenus = await registry; }
            catch (Exception ex) { systemMenus = []; menusDetail = "部分传统菜单无法读取：" + ex.Message; }
            try { var result = await packaged; systemMenus.AddRange(result.Entries); menusDetail += result.Detail; }
            catch (Exception ex) { menusDetail += "一级菜单暂未读取：" + ex.Message; }
            systemMenus = MenuMetadata.Read(systemMenus);
        }
        finally { menusLoading = false; SendState(); }
    }

    private async Task ToggleSystemMenu(string id, bool enabled)
        => await ToggleSystemMenus([id], enabled);

    private async Task ToggleSystemMenus(string[] ids, bool enabled)
    {
        if (menusLoading) return;
        if (ids.Length is 0 or > 512) throw new IOException("请选择需要调整的菜单功能。");
        busy = true; isError = false; message = enabled ? "正在恢复菜单…" : "正在停用菜单…"; SendState();
        try
        {
            var errors = new List<string>();
            if (smokeTest) systemMenus = systemMenus.Select(e => ids.Contains(e.Id) ? e with { Enabled = enabled, Managed = !enabled } : e).ToList();
            else
            {
                errors = await Task.Run(() => menuRegistry.SetManyEnabled(ids, enabled));
                ShellIntegration.NotifyShell();
            }
            await RefreshSystemMenus();
            isError = errors.Count > 0;
            message = errors.Count > 0 ? $"{errors.Count} 个位置未能调整：" + string.Join("；", errors.Distinct())
                : enabled ? "已撤销归序对此功能的停用设置。请重新打开右键菜单。" : "菜单功能已为当前账户停用，可在这里恢复。若仍显示，请重开文件夹窗口或退出并重新登录 Windows。";
        }
        catch (Exception ex) { message = "菜单调整未全部完成：" + ex.Message; isError = true; await RefreshSystemMenus(); }
        finally { busy = false; SendState(); await ProcessQueued(); }
    }

    private async Task RestoreSystemMenus()
    {
        if (menusLoading) return;
        busy = true; isError = false; message = "正在恢复归序停用的菜单…"; SendState();
        try
        {
            var errors = new List<string>();
            if (smokeTest) systemMenus = systemMenus.Select(e => e.Managed ? e with { Enabled = true, Managed = false } : e).ToList();
            else
            {
                errors = await Task.Run(() => menuRegistry.SetManyEnabled(menuRegistry.Scan().Where(e => e.Managed).Select(e => e.Id).ToArray(), true));
                ShellIntegration.NotifyShell();
            }
            await RefreshSystemMenus();
            isError = errors.Count > 0;
            message = errors.Count == 0 ? "归序停用的菜单已恢复。" : "部分项目未恢复：" + string.Join("；", errors);
        }
        finally { busy = false; SendState(); await ProcessQueued(); }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (busy) { e.Cancel = true; Notify("正在处理文件，完成后即可关闭。"); return; }
        ready = false;
        Browser.Dispose();
    }

    private void Fail(Exception exception)
    {
        if (smokeTest)
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-error.txt"), exception.ToString());
            Application.Current.Shutdown(1);
        }
        else
        {
            LoadingText.Text = "界面启动失败，请检查本地 Web 文件和 WebView2 运行环境。";
            MessageBox.Show(this, exception.Message, "归序", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async Task CheckWebInterface()
    {
        try
        {
            await WaitUntil(async () => (await Browser.ExecuteScriptAsync("document.querySelector('h1')?.textContent")).Contains("收下的每一份"));
            // Exercise the React -> native bridge with isolated files only.
            var testDirectory = Path.Combine(AppContext.BaseDirectory, "smoke-fixtures", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testDirectory);
            settings.RootFolder = Path.Combine(testDirectory, "archive");
            settings.AutoOrganize = false;
            history.Clear();
            SendState();
            Browser.CoreWebView2.PostWebMessageAsJson("{\"type\":\"navigate\",\"page\":\"settings\"}");
            await WaitUntil(async () => (await Browser.ExecuteScriptAsync("document.querySelector('[aria-label=\"拖入后自动整理\"]') !== null")) == "true");
            await Task.Delay(100);
            using (var output = File.Create(Path.Combine(AppContext.BaseDirectory, "smoke-settings.png")))
                await Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
            await Browser.ExecuteScriptAsync("document.querySelector('[aria-label=\"拖入后自动整理\"]').click()");
            await WaitUntil(() => Task.FromResult(settings.AutoOrganize));
            await Browser.ExecuteScriptAsync("document.querySelector('[aria-label=\"拖入后自动整理\"]').click()");
            await WaitUntil(() => Task.FromResult(!settings.AutoOrganize));
            await Browser.ExecuteScriptAsync("document.querySelector('[aria-label=\"打开右键菜单管理\"]').click()");
            await CheckScript("document.querySelector('#menu-title')?.textContent === '右键菜单管理' && document.querySelectorAll('.menu-software-toggle').length === 2");
            await CheckScript("document.querySelectorAll('.menu-command-toggle').length === 0 && document.querySelectorAll('.menu-location-row').length === 0");
            await Task.Delay(350);
            using (var output = File.Create(Path.Combine(AppContext.BaseDirectory, "smoke-menu-software.png")))
                await Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
            await Browser.ExecuteScriptAsync("document.querySelector('[aria-label=\"展开 WPS Office 的菜单\"]').click()");
            await CheckScript("document.querySelectorAll('.menu-command-toggle').length === 2 && document.querySelectorAll('.menu-location-row').length === 0");
            await Browser.ExecuteScriptAsync("document.querySelector('.menu-command-toggle').click()");
            await CheckScript("document.querySelectorAll('.menu-location-row').length === 3");
            await Browser.ExecuteScriptAsync("document.querySelector('.menu-position-toggle').click()");
            await CheckScript("document.querySelector('.menu-position-toggle')?.getAttribute('aria-checked') === 'false' && !document.querySelector('.menu-position-toggle').disabled");
            if (systemMenus.Single(e => e.Id == "fixture-menu").Enabled || systemMenus.Where(e => e.Id != "fixture-menu").Any(e => !e.Enabled))
                throw new IOException("Position toggle affected a different position or command.");
            await Browser.ExecuteScriptAsync("document.querySelector('.menu-position-toggle').click()");
            await CheckScript("document.querySelector('.menu-position-toggle')?.getAttribute('aria-checked') === 'true' && !document.querySelector('.menu-position-toggle').disabled");
            await Browser.ExecuteScriptAsync("document.querySelector('.menu-all-toggle').click()");
            await CheckScript("document.querySelector('.menu-all-toggle')?.getAttribute('aria-checked') === 'false' && !document.querySelector('.menu-all-toggle').disabled");
            if (systemMenus.Where(e => e.CommandKey == "fixture-command").Any(e => e.Enabled || !e.Managed)
                || systemMenus.Where(e => e.CommandKey != "fixture-command").Any(e => !e.Enabled || e.Managed))
                throw new IOException("Grouped toggle changed a same-name independent command or another software.");
            await Browser.ExecuteScriptAsync("document.querySelector('.menu-restore').click()");
            await CheckScript("document.querySelector('.menu-all-toggle')?.getAttribute('aria-checked') === 'true' && !document.querySelector('.menu-all-toggle').disabled");
            await Browser.ExecuteScriptAsync("(()=>{const input=document.querySelector('[aria-label=\"搜索右键菜单\"]');Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(input,'桌面');input.dispatchEvent(new Event('input',{bubbles:true}));})()");
            await CheckScript("document.querySelectorAll('.menu-software-toggle').length === 1 && document.querySelectorAll('.menu-command-toggle').length === 1 && document.querySelectorAll('.menu-location-row').length === 3");
            await Browser.ExecuteScriptAsync("(()=>{const input=document.querySelector('[aria-label=\"搜索右键菜单\"]');Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(input,'');input.dispatchEvent(new Event('input',{bubbles:true}));})()");
            await CheckScript("document.querySelectorAll('.menu-software-toggle').length === 2 && document.querySelectorAll('.menu-command-toggle').length === 2");
            await Browser.ExecuteScriptAsync("document.querySelector('.menu-section-heading button').click()");
            await CheckScript("document.querySelectorAll('.menu-position-toggle:not(:disabled)').length === 3 && document.querySelector('.menu-software-toggle[aria-expanded=true]') !== null");
            await Task.Delay(350);
            using (var output = File.Create(Path.Combine(AppContext.BaseDirectory, "smoke-menu-manager.png")))
                await Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
            await Browser.ExecuteScriptAsync("document.querySelector('[aria-label=\"添加复制路径菜单\"]').click()");
            await CheckScript("document.querySelector('[aria-label=\"移除复制路径菜单\"]') !== null");
            var beforeRepair = contextMenuRevision;
            await Browser.ExecuteScriptAsync("Array.from(document.querySelectorAll('button')).find(b => b.textContent.includes('修复归序菜单')).click()");
            await WaitUntil(() => Task.FromResult(contextMenuRevision > beforeRepair && !busy));
            await CheckScript("document.querySelector('[aria-label=\"移除复制路径菜单\"]') !== null");
            await Browser.ExecuteScriptAsync("document.querySelector('[aria-label=\"移除复制路径菜单\"]').click()");
            await CheckScript("document.querySelector('[aria-label=\"添加复制路径菜单\"]') !== null");
            await Browser.ExecuteScriptAsync("document.querySelector('[aria-label=\"关闭右键菜单管理\"]').click()");
            await CheckScript("document.querySelector('#menu-title') === null");
            var source = Path.Combine(testDirectory, "桥接 检查, 终稿.txt");
            File.WriteAllText(source, "React bridge check 中文");
            DesktopShortcut.Create(testDirectory);
            DesktopShortcut.Create(testDirectory);
            if (!File.Exists(Path.Combine(testDirectory, "归序.lnk"))) throw new IOException("Shortcut check failed.");
            await ((App)Application.Current).ForwardForCheck([source]);
            Browser.CoreWebView2.PostWebMessageAsJson("{\"type\":\"navigate\",\"page\":\"organize\"}");
            await WaitUntil(async () => (await Browser.ExecuteScriptAsync("Array.from(document.querySelectorAll('button')).some(b => b.textContent.includes('整理 1 个项目'))")) == "true");
            await Browser.ExecuteScriptAsync("Array.from(document.querySelectorAll('button')).find(b => b.textContent.includes('整理 1 个项目')).click()");
            await WaitUntil(() => Task.FromResult(!busy && history.Any(entry => entry.State == "已归档")));
            if (File.Exists(source) || File.ReadAllText(history[0].Destination) != "React bridge check 中文") throw new IOException("Bridge move check failed.");
            settings.AutoOrganize = true;
            var sourceFolder = Path.Combine(testDirectory, "项目 材料, v1");
            Directory.CreateDirectory(Path.Combine(sourceFolder, "空目录"));
            File.WriteAllText(Path.Combine(sourceFolder, "说明.md"), "folder check 中文");
            Browser.CoreWebView2.PostWebMessageAsJson("{\"type\":\"navigate\",\"page\":\"library\"}");
            await ((App)Application.Current).ForwardForCheck([sourceFolder]);
            await WaitUntil(() => Task.FromResult(!busy && library.Count == 2));
            await WaitUntil(async () => (await Browser.ExecuteScriptAsync("document.querySelectorAll('.archive-row').length")) == "2");
            if (!(await Browser.ExecuteScriptAsync("document.querySelector('h1')?.textContent")).Contains("收下的每一份")) throw new IOException("Desktop drop changed the current page.");
            // Several days, months, and types expose incorrect cross-filtering and counts.
            AddLibrarySample(DateTime.Today, "图片", "界面参考.png");
            AddLibrarySample(DateTime.Today.AddDays(-1), "文档", "阶段计划.txt");
            AddLibrarySample(DateTime.Today.AddDays(-1), "图片", "设计参考.png");
            AddLibrarySample(DateTime.Today.AddMonths(-1), "压缩包", "历史材料.zip");
            await Browser.ExecuteScriptAsync("document.querySelector('[aria-label=\"刷新\"]').click()");
            await CheckScript("document.querySelectorAll('.archive-row').length === 6 && document.querySelectorAll('.timeline-day').length === 3");
            await WaitUntil(() => Task.FromResult(!busy));
            if (new ArchiveCatalog(settings).Load(history).Count != 6) throw new IOException("Manual catalog update was not persisted.");
            await Task.Delay(150);
            using (var output = File.Create(Path.Combine(AppContext.BaseDirectory, "smoke-preview.png")))
                await Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
            await Browser.ExecuteScriptAsync("document.querySelector('.type-chips [data-category=\"文档\"]').click()");
            await CheckScript("document.querySelectorAll('.archive-row').length === 2 && Array.from(document.querySelectorAll('.archive-row')).every(row => row.dataset.category === '文档')");
            var yesterday = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd");
            await Browser.ExecuteScriptAsync($"document.querySelector('.date-rail [data-date=\"{yesterday}\"]').click()");
            await CheckScript("document.querySelectorAll('.archive-row').length === 1");
            await Browser.ExecuteScriptAsync("Array.from(document.querySelectorAll('.view-switch button')).find(b => b.textContent === '按类型').click()");
            await CheckScript("document.querySelectorAll('.type-card').length === 5 && document.querySelectorAll('.archive-row').length === 1 && document.querySelector('.type-card[data-category=\"文档\"]').getAttribute('aria-pressed') === 'true'");
            await Browser.ExecuteScriptAsync("const dateSelect = document.querySelector('[aria-label=\"筛选收纳日期\"]'); dateSelect.value = ''; dateSelect.dispatchEvent(new Event('change', {bubbles:true}))");
            await CheckScript("document.querySelectorAll('.archive-row').length === 2");
            await Browser.ExecuteScriptAsync("document.querySelector('.type-card[data-category=\"图片\"]').click()");
            await CheckScript("document.querySelectorAll('.archive-row').length === 2 && Array.from(document.querySelectorAll('.archive-row')).every(row => row.dataset.category === '图片')");
            await Browser.ExecuteScriptAsync("document.querySelector('.reset-filters').click()");
            await CheckScript("document.querySelectorAll('.archive-row').length === 6");
            await CheckViewAnimations();
            await Task.Delay(520);
            using (var output = File.Create(Path.Combine(AppContext.BaseDirectory, "smoke-types.png")))
                await Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
            // React-controlled input: dispatch a native edit so onChange observes the new value.
            await SetLibrarySearch("  终稿  ");
            await CheckScript("document.querySelectorAll('.archive-row').length === 1 && document.querySelector('.archive-name').textContent.includes('终稿')");
            await SetLibrarySearch("没有这样的材料");
            await CheckScript("document.querySelectorAll('.archive-row').length === 0 && document.querySelector('.empty-state') !== null");
            await Browser.ExecuteScriptAsync("document.querySelector('.empty-state button').click()");
            await CheckScript("document.querySelectorAll('.archive-row').length === 6 && document.querySelector('[aria-label=\"搜索归档空间\"]').value === ''");
            if (Environment.GetCommandLineArgs().Contains("--check-explorer"))
            {
                var file = history.Single(entry => !entry.IsDirectory);
                await SetLibrarySearch("终稿");
                await CheckScript("document.querySelectorAll('.archive-row').length === 1");
                await ExplorerSelectionCheck.VerifyAsync(file.Destination, async () => { await Browser.ExecuteScriptAsync("document.querySelector('.reveal-button').click()"); });
                await Browser.ExecuteScriptAsync("document.querySelector('.reset-filters').click()");
                await CheckScript("document.querySelectorAll('.archive-row').length === 6");
                await Browser.ExecuteScriptAsync("document.querySelector('.type-card[data-category=\"文件夹\"]').click()");
                await CheckScript("document.querySelectorAll('.archive-row').length === 1");
                await ExplorerSelectionCheck.VerifyAsync(history.Single(entry => entry.IsDirectory).Destination, async () => { await Browser.ExecuteScriptAsync("document.querySelector('.reveal-button').click()"); });
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-explorer-checks.txt"), "PASS real Explorer folder and selection from file button\nPASS real Explorer folder and selection from folder button\nPASS Chinese names, spaces, and commas\n");
            }
            Browser.CoreWebView2.PostWebMessageAsJson("{\"type\":\"navigate\",\"page\":\"history\"}");
            await WaitUntil(async () => (await Browser.ExecuteScriptAsync("Array.from(document.querySelectorAll('button')).some(b => b.textContent === '撤销')")) == "true");
            await Task.Delay(100);
            using (var output = File.Create(Path.Combine(AppContext.BaseDirectory, "smoke-history.png")))
                await Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
            await Browser.ExecuteScriptAsync("Array.from(document.querySelectorAll('button')).find(b => b.textContent === '撤销').click()");
            await WaitUntil(() => Task.FromResult(!busy && history[0].State == "已撤销"));
            if (File.ReadAllText(Path.Combine(sourceFolder, "说明.md")) != "folder check 中文") throw new IOException("Folder bridge undo check failed.");
            await WaitUntil(async () => (await Browser.ExecuteScriptAsync("Array.from(document.querySelectorAll('button')).some(b => b.textContent === '撤销')")) == "true");
            await Browser.ExecuteScriptAsync("Array.from(document.querySelectorAll('button')).find(b => b.textContent === '撤销').click()");
            await WaitUntil(() => Task.FromResult(!busy && history.All(entry => entry.State == "已撤销")));
            if (File.ReadAllText(source) != "React bridge check 中文") throw new IOException("Bridge undo check failed.");
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-checks.txt"), "PASS React rendering\nPASS settings bridge\nPASS context menu settings bridge (simulated installation)\nPASS three-level software menus, independent positions, same-name identity and complete search results\nPASS preview and organize bridge\nPASS history and undo bridge (file and folder)\nPASS shortcut creation and reuse\nPASS local IPC file forwarding without navigation\nPASS multi-day and month timeline\nPASS date and type cross-filtering and counts\nPASS filter preservation when switching views\nPASS search trimming, empty results, and reset\nPASS manual catalog update and persisted reopening\nPASS animated view switching, rapid clicks, reduced motion, and fallback\n");
            Application.Current.Shutdown(0);
        }
        catch (Exception ex)
        {
            try
            {
                var ui = await Browser.ExecuteScriptAsync("JSON.stringify({text:document.querySelector('.menu-manager')?.innerText,switches:Array.from(document.querySelectorAll('.menu-manager [role=switch]')).map(b=>({label:b.getAttribute('aria-label'),checked:b.getAttribute('aria-checked'),disabled:b.disabled}))})");
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-menu-diagnostics.json"), JsonSerializer.Serialize(new { busy, menusLoading, message, ui }));
                using var output = File.Create(Path.Combine(AppContext.BaseDirectory, "smoke-error.png"));
                await Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
            }
            catch { }
            Fail(ex);
        }
    }

    private void AddLibrarySample(DateTime date, string category, string name)
    {
        var folder = Path.Combine(settings.RootFolder, "归序", date.ToString("yyyy-MM"), date.ToString("yyyy-MM-dd"), category);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name), "Generated library check fixture");
    }

    private async Task CheckViewAnimations()
    {
        await Browser.ExecuteScriptAsync("""
            window.__motionCheck = {start: document.startViewTransition, media: window.matchMedia, started: 0, finished: 0, ready: false, directions: new Set()};
            window.matchMedia = function(query) { return query === '(prefers-reduced-motion: reduce)' ? {matches:false} : window.__motionCheck.media.call(window, query); };
            if (typeof document.startViewTransition === 'function') {
              document.startViewTransition = function(update) {
                const probe = window.__motionCheck;
                probe.started++;
                const active = probe.start.call(this, update);
                active.ready.then(() => {
                  const direction = document.querySelector('.view-type') ? 'left' : 'right';
                  const incoming = getComputedStyle(document.documentElement, '::view-transition-new(archive-content)');
                  const outgoing = getComputedStyle(document.documentElement, '::view-transition-old(archive-content)');
                  probe.ready = incoming.animationName === 'archive-enter-' + direction && outgoing.animationName === 'archive-leave-' + direction && incoming.animationDuration === '0.5s';
                  if (probe.ready) probe.directions.add(direction);
                }).catch(() => {});
                active.finished.catch(() => {}).then(() => { probe.finished++; });
                return active;
              };
            }
            Array.from(document.querySelectorAll('.view-switch button')).find(b => b.textContent === '按日期').click();
            """);
        try
        {
            await CheckScript("document.querySelector('.view-date') !== null && document.querySelectorAll('.archive-row').length === 6");
            if (await Browser.ExecuteScriptAsync("typeof window.__motionCheck.start === 'function'") == "true")
            {
                await CheckScript("window.__motionCheck.ready");
                using (var output = File.Create(Path.Combine(AppContext.BaseDirectory, "smoke-transition.png")))
                    await Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
                await CheckScript("window.__motionCheck.finished === window.__motionCheck.started");
                await Browser.ExecuteScriptAsync("""
                    const buttons = Array.from(document.querySelectorAll('.view-switch button'));
                    buttons.find(b => b.textContent === '按类型').click();
                    buttons.find(b => b.textContent === '按日期').click();
                    buttons.find(b => b.textContent === '按类型').click();
                    """);
                await CheckScript("document.querySelector('.view-type') !== null && document.querySelectorAll('.archive-row').length === 6");
                await CheckScript("window.__motionCheck.finished === window.__motionCheck.started");
                // Exercise a click while an animation is already showing snapshots.
                await Browser.ExecuteScriptAsync("Array.from(document.querySelectorAll('.view-switch button')).find(b => b.textContent === '按日期').click()");
                await CheckScript("document.querySelector('.view-date') !== null");
                await Browser.ExecuteScriptAsync("Array.from(document.querySelectorAll('.view-switch button')).find(b => b.textContent === '按类型').click()");
                await CheckScript("document.querySelector('.view-type') !== null");
                await CheckScript("window.__motionCheck.finished === window.__motionCheck.started");
                await CheckScript("window.__motionCheck.directions.has('left') && window.__motionCheck.directions.has('right')");
            }
            await Browser.ExecuteScriptAsync("""
                window.__motionCheck.beforeReduced = window.__motionCheck.started;
                window.matchMedia = function(query) { return query === '(prefers-reduced-motion: reduce)' ? {matches:true} : window.__motionCheck.media.call(window, query); };
                Array.from(document.querySelectorAll('.view-switch button')).find(b => b.textContent === '按日期').click();
                """);
            await CheckScript("document.querySelector('.view-date') !== null && window.__motionCheck.started === window.__motionCheck.beforeReduced");
            await Browser.ExecuteScriptAsync("""
                document.startViewTransition = undefined;
                window.matchMedia = window.__motionCheck.media;
                Array.from(document.querySelectorAll('.view-switch button')).find(b => b.textContent === '按类型').click();
                """);
            await CheckScript("document.querySelector('.view-type .view-fallback') !== null && document.querySelectorAll('.archive-row').length === 6");
        }
        finally
        {
            await Browser.ExecuteScriptAsync("document.startViewTransition = window.__motionCheck.start; window.matchMedia = window.__motionCheck.media; delete window.__motionCheck");
        }
    }

    private Task CheckScript(string predicate) => WaitUntil(async () => await Browser.ExecuteScriptAsync(predicate) == "true");

    private Task SetLibrarySearch(string value) => Browser.ExecuteScriptAsync($"Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(document.querySelector('[aria-label=\"搜索归档空间\"]'), {JsonSerializer.Serialize(value)}); document.querySelector('[aria-label=\"搜索归档空间\"]').dispatchEvent(new Event('input', {{bubbles:true}}))");

    private static async Task WaitUntil(Func<Task<bool>> predicate)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (await predicate()) return;
            await Task.Delay(60);
        }
        throw new TimeoutException("Web interface check timed out.");
    }

    private sealed class FileDisplay(string source, string fileName, string category, string destination, string status, bool canMove)
    {
        public string Source { get; } = source;
        public string FileName { get; } = fileName;
        public string Category { get; } = category;
        public string Destination { get; } = destination;
        public string Status { get; set; } = status;
        public bool CanMove { get; } = canMove;
    }
}
