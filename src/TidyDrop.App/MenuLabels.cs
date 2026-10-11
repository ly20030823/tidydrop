using System.Text.RegularExpressions;

namespace TidyDrop.App;

internal static partial class MenuLabels
{
    public static string Software(string value)
    {
        var lower = value.ToLowerInvariant();
        if (lower.Contains("wps") || lower.Contains("kingsoft") || lower.Contains("金山")) return "WPS Office";
        if (lower.Contains("bandizip") || lower.Contains("bandisoft")) return "Bandizip";
        if (lower.Contains("baidu") || lower.Contains("百度")) return "百度网盘";
        if (lower.Contains("qqextension") || lower is "qq" or "qqshell" || lower.Contains("tencent.qq")) return "QQ";
        if (lower.Contains("weixin") || lower.Contains("wechat")) return "微信";
        if (lower.Contains("7-zip") || lower.Contains("7zip")) return "7-Zip";
        if (lower.Contains("winrar")) return "WinRAR";
        if (lower.Contains("microsoftofficehub") || lower.Contains("m365") || lower.Contains("copilot")) return "Microsoft 365 / Copilot";
        if (lower.Contains("clipchamp")) return "Clipchamp";
        if (lower.Contains("windows.photos")) return "Microsoft 照片";
        if (lower.Contains("microsoft.paint")) return "Microsoft 画图";
        if (lower.Contains("notepad")) return "记事本";
        if (lower.Contains("openai.codex")) return "Codex";
        if (lower.Contains("powertoys")) return "Microsoft PowerToys";
        if (lower.Contains("screensketch")) return "截图工具";
        if (lower.Contains("onedrive")) return "OneDrive";
        if (lower.Contains("visual studio code") || lower.Contains("vscode")) return "Visual Studio Code";
        if (lower.Contains("windows operating system") || lower.Contains("windows 操作系统")) return "Windows";
        if (Guid.TryParse(value, out _) || value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) return "未识别的软件";
        return value;
    }

    public static string Command(string value, string software, bool packaged = false)
    {
        var lower = value.ToLowerInvariant();
        var common = lower switch { "open" => "打开", "openas" => "打开方式", "edit" => "编辑", "print" => "打印", "printto" => "打印到", "runas" => "以管理员身份运行", "properties" => "属性", "explore" => "浏览", "opennewwindow" => "在新窗口中打开", "play" => "播放", "preview" => "预览", _ => "" };
        if (common.Length > 0) return common;
        if (lower.Contains("askm365copilot")) return "询问 Copilot";
        if (lower.Contains("openprojectincodex")) return "在 Codex 中打开文件夹";
        if (lower.Contains("powerrename")) return "PowerRename 批量重命名";
        if (lower.Contains("filelocksmith")) return "查看占用文件的进程";
        if (packaged) return software + " · " + value;
        if (Guid.TryParse(value, out _) || value.StartsWith('@') || value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)
            || TechnicalName().IsMatch(value)) return "菜单组件 · " + value;
        return value;
    }
    public static string Component(string value, string software, string clsid)
        => Guid.TryParse(value, out _) || TechnicalName().IsMatch(value) ? software + " 右键菜单扩展 · " + clsid.Trim('{', '}')[..8] : value;
    public static string FileType(string value)
    {
        value = value.Replace(@"SystemFileAssociations\", "", StringComparison.OrdinalIgnoreCase);
        var lower = value.ToLowerInvariant();
        if (lower.StartsWith("word.") || lower.Contains("docx")) return "Word 文档";
        if (lower.StartsWith("excel.") || lower.Contains("xlsx")) return "Excel 表格";
        if (lower.StartsWith("powerpoint.") || lower.Contains("pptx")) return "PowerPoint 演示文稿";
        if (lower.Contains("pdf")) return "PDF 文档";
        if (lower.StartsWith('.') && lower.Length < 12) return value.TrimStart('.').ToUpperInvariant() + " 文件";
        return lower switch { "image" => "图片", "audio" => "音频", "video" => "视频", "text" => "文本", "compressed" => "压缩文件", _ => "软件关联的文件" };
    }
    [GeneratedRegex(@"(^[A-Za-z0-9_.-]*(ContextMenu|ShellExt|ExplorerCommand|Handler)[A-Za-z0-9_.-]*$)|(^[A-Za-z]*[0-9]+$)", RegexOptions.IgnoreCase)]
    private static partial Regex TechnicalName();
}
