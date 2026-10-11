# 右键菜单管理：源码对照与改造方案

记录日期：2026-10-11。归序对照版本：v0.6.3（`fc64812`）。

本文保留源码研究时的方案。v0.7.0 已实现出现位置/软件视图、命令身份分组、私有与公共子菜单读取、对象来源分析、新建/发送到/打开方式管理，以及独立进程中的动态名称和子命令查询。系统基础组件、共享子命令与第三方一级菜单组件仍仅查看；不提供未验证的通用一级菜单禁用策略。当前能力与恢复方式见 [使用说明](menu-manager.md)。

v0.7.1 修正：主动创建第三方 COM 组件可能弹出所属软件的初始化界面，动态标题与子命令查询已取消。以下相关方案保留作研究记录，当前仅通过注册表、资源和应用包清单读取名称，不再激活第三方组件。

## 参考范围

参考项目：[BluePointLilac/ContextMenuManager](https://github.com/BluePointLilac/ContextMenuManager)。本地研究版本为 `55507155dd8e49c7ab4606da97f2af192d590dfe`，该提交日期为 2024-05-05。阅读了菜单枚举、名称解析、场景分析、子菜单和各类隐藏操作的源码，未运行或安装该工具。

关键源码：

- [ShellList.cs](https://github.com/BluePointLilac/ContextMenuManager/blob/55507155dd8e49c7ab4606da97f2af192d590dfe/ContextMenuManager/Controls/ShellList.cs)：场景选择、文件关联、给定文件或目录的菜单来源分析。
- [GuidInfo.cs](https://github.com/BluePointLilac/ContextMenuManager/blob/55507155dd8e49c7ab4606da97f2af192d590dfe/ContextMenuManager/Methods/GuidInfo.cs)：用户/在线/内置字典、本地化资源、组件说明和文件信息。
- [ShellItem.cs](https://github.com/BluePointLilac/ContextMenuManager/blob/55507155dd8e49c7ab4606da97f2af192d590dfe/ContextMenuManager/Controls/ShellItem.cs)：静态命令名称、隐藏标志、子菜单入口。
- [ShellSubMenuDialog.cs](https://github.com/BluePointLilac/ContextMenuManager/blob/55507155dd8e49c7ab4606da97f2af192d590dfe/ContextMenuManager/Controls/ShellSubMenuDialog.cs)：CommandStore 公共子菜单、私有子菜单的分别处理。
- [ShellExItem.cs](https://github.com/BluePointLilac/ContextMenuManager/blob/55507155dd8e49c7ab4606da97f2af192d590dfe/ContextMenuManager/Controls/ShellExItem.cs)：扩展组件及通过移动注册项隐藏的实现。
- `ShellNewList/Item`、`SendToList/Item`、`OpenWithList/Item`：分别处理新建模板、SendTo 目录、打开方式注册。
- [UwpModeItemsDic.xml](https://github.com/BluePointLilac/ContextMenuManager/blob/55507155dd8e49c7ab4606da97f2af192d590dfe/ContextMenuManager/Properties/Resources/Texts/UwpModeItemsDic.xml) 与 `UwpModeItem/UwpHelper`：预设 UWP GUID、PackagedCom 查询和 Blocked 操作。

参考仓库采用 GPL-3.0。本次未将其源码或菜单字典复制到归序；后续实现应记录代码和数据来源。

## 当前问题的根源

| 归序 v0.6.3 的做法 | 对用户的影响 | 参考项目提供的思路 |
| --- | --- | --- |
| `MenuLabels.Command` 把许多动态名称改成“某软件菜单”；`groupMenus` 随后仅以软件、标题、种类合并 | 同一软件的不同功能会混成一行，主开关可能调整多个不同命令 | 标题解析与菜单身份分别保存；没有可靠标题时保留组件信息 |
| 从多种位置一次扫描后，以软件为默认入口 | 与用户刚刚右键看到的菜单难对应 | 先选择文件、目录、目录空白处、桌面空白处等场景 |
| HKCR 注册、扩展组件和应用包 Verb 被放进同一种扁平数据模型 | 注册组件看起来像一条具体菜单命令，动态组件可能生成多个命令 | Shell、ShellEx、UWP 和特殊菜单分别建模 |
| 读取 `SubCommands` 仅参与指纹，不读取其子命令；未覆盖 `ExtendedSubCommandsKey` | 菜单结构丢失，无法理解父项和子项的关系 | 分别解析 CommandStore 引用和私有子菜单 |
| 隐藏状态主要判断 LegacyDisable、ProgrammaticAccessOnly、CLSID 前缀和 Blocked | 别的管理器采用其他隐藏方式时，显示状态可能失真 | 识别不同隐藏标志及备份位置，但恢复规则需独立设计 |
| 未枚举新建、发送到、打开方式 | “整个系统右键菜单管理”的覆盖范围不完整 | 为特殊菜单设置独立读取和修改策略 |

现有分组保留了原始注册 ID，因此没有丢失后台恢复记录；问题在于展示时将不一定相同的功能合并了。不能用增加几条名称映射掩盖这个问题。

## 页面应该怎样组织

默认按出现位置浏览：文件、文件夹、文件夹空白处、桌面空白处、磁盘、特定文件类型。Windows 的 Folder 与 Directory 是不同注册来源；可以使用用户易懂的场景名称，但详情中需保留来源区别。

“按软件查看”作为另一个视图。同一软件下仍分开显示不同命令，不能把所有 WPS 功能统一成一条“WPS 菜单”。同一命令跨场景注册，可以展开查看其位置，开关旁说明修改的范围。

增加“选择一个文件/文件夹分析”入口：显示与该对象相关的通用来源、当前关联、类型来源和注册条件。这是来源分析；没有运行时查询结果时，不能宣称完全复现 Explorer 菜单。

可解析的子菜单保留父子结构。动态扩展无法枚举子项时，显示“整个扩展组件”，说明关闭后可能影响该组件生成的多条菜单。

“新建”“发送到”“打开方式”各自有独立页面，不挤进普通命令列表。

## 数据模型和读取

将注册记录、逻辑命令、展示名称和修改能力分开：

- 注册记录保留来源类型、注册路径/包身份、CLSID、Verb、注册表视图、当前账户或机器来源，以及归序的恢复记录。
- 逻辑命令保留命令身份、场景和父子关系。显示标题不参与身份判断；同一 CLSID 也可能根据不同 Verb 初始化为不同命令，不能仅凭 CLSID 合并。
- 名称保留原始值、解析后的标题、解析来源及可信程度。优先解析真实资源/注册标题，再使用明确的已知组件映射；未知组件提供可区分的名称和原始详情，不猜造功能名。
- 状态分别保存注册状态、发现的隐藏方式、运行时查询状态。无对应证据时显示“已注册”“条件显示”或“状态未知”，不能直接等同于当前菜单可见。
- 修改能力明确描述可以调整的是某场景的静态命令、整个扩展组件或某特殊菜单。只读项说明具体原因。

拆分读取器：StaticVerbReader、ShellExtensionReader、PackagedCommandReader、NewMenuReader、SendToReader、OpenWithReader。不同读取器分别实现名称解析、状态判断与修改能力，不共享一个通用开关策略。

先修正现有静态注册与组件枚举：补全标题来源、LocalServer32 等服务器信息、子菜单结构和其他工具的隐藏状态识别。识别出别的工具隐藏的项目，不意味着归序有权移除它们的隐藏标志。

读取过程保持只读。参考项目的 ShellList 在加载时调用注册表所有权处理；归序不应为了显示列表修改权限或所有权。

## Win11 一级菜单需要独立验证

在本次检查的源码中，UWP 菜单通过预设 GUID 字典查找 PackagedCom，再通过 Blocked 路径屏蔽；没有发现通用 `windows.fileExplorerContextMenus` 清单解析及 `IExplorerCommand` 枚举实现。源码注释说明兼容 Vista–Win11，但不能据此推断其完整管理所有 Win11 一级菜单。

微软的 [IExplorerCommand 文档](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-iexplorercommand) 将 GetTitle、GetState、EnumSubCommands 分别定义为标题、状态和子命令查询接口。归序现有包清单枚举拿到的 Verb.Id 不能保证就是实际显示文字。

后续如需查询动态标题/状态，应在独立辅助进程中进行，设超时并处理崩溃；第三方扩展不载入主界面进程，不调用 Invoke，不执行菜单命令。某些扩展需要具体选择对象和 Explorer 站点，即使接口查询成功也不能保证完全复现当前 Explorer 的菜单。

没有逐项验证过的一级菜单禁用策略继续只读。不能为了获得一个开关就卸载软件包，或把全局 CLSID 屏蔽伪装成仅关闭一个位置。若某策略影响整个组件，其影响范围必须显示清楚。

## 修改和恢复的要求

保留现有当前账户覆盖、写入前记录、原值类型保留、软件更新冲突检测及恢复机制。重构不丢失 `HKCU/Software/TidyDrop/MenuOverrides` 中已有记录。

静态命令、扩展组件、子菜单、新建模板、SendTo 项目、打开方式分别制定操作策略；不把 LegacyDisable 当成适用于所有项目的通用方法。

默认逐项调整，批量操作明确列出影响项目。保护打开、属性等基础功能；恢复只撤回归序自己的修改。管理员操作和系统级操作如以后需要，应与普通当前账户操作明确区分。

## 实施次序和验收

1. **修正身份、名称和页面**：出现位置作为默认入口；拆开同名不同命令；保留旧恢复记录；明确组件与命令、已注册与可见的区别。
2. **补全传统菜单**：子菜单、其他隐藏状态、新建、发送到、打开方式，按不同来源实现读取与可恢复操作。
3. **验证 Win11 动态组件**：辅助进程查询能力、具体软件的禁用与恢复，逐项验证后再开放能力。

最低验收要求：

- 同一软件的两个不同命令即使同名也不会被主开关误合并；同一组件不同 Verb 仍可区分。
- 一个命令跨文件与目录注册时，关闭目录位置不影响文件位置；批量操作呈现真实影响范围。
- 类型分析能解释通用文件、AllFilesystemObjects、当前 ProgID、SystemFileAssociations 与 PerceivedType 的来源关系。
- 私有子菜单和 CommandStore 引用均正确显示父子关系；共享子命令的影响范围明确。
- 不同隐藏标志、其他工具备份位置、无效残留项、访问受限项都有明确状态。
- v0.6.3 产生的恢复记录在重构后仍可恢复；软件更新或其他工具改动时拒绝覆盖。
- 新建、SendTo、打开方式分别使用临时测试项验证实际菜单和恢复，不修改真实第三方设置来做自动测试。
- Win11 一级与“显示更多选项”分开实际验证；无法获取动态标题时显示诚实且可区分的回退名称。
- 刷新列表不修改注册表、权限或文件；第三方扩展超时/崩溃不会拖垮主窗口。

以上为研究阶段的方案和验收目标；v0.6.3 不具有新增能力。v0.7.0 的实现和验证范围见 [版本记录](releases/v0.7.0.md)。目录模板、应用包禁用策略以及更多系统场景仍可按实际需求扩展。
