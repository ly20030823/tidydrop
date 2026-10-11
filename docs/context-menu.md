# Windows 11「复制文件路径」菜单

功能版本：v0.6.2。当前菜单组件支持 Windows 11 x64。

## 使用

点击归序侧边栏的「右键菜单管理」（归档设置里也有入口），在页面顶部点击「添加菜单」。在桌面或文件夹内的文件、文件夹上右键，直接点击一级菜单里的「复制文件路径」。粘贴得到完整路径，不带包裹路径的引号；多选时按选择顺序，每行一个路径。

菜单复制路径时不启动归序，不移动、读取或改写材料内容。空白处和「此电脑」等没有文件系统路径的虚拟项目不显示该命令。关闭归序后仍能使用。

在同一设置项点击「移除菜单」即可取消注册。移除只处理归序自己的组件与归序安装时新增的签名信任，不更改 Windows 自带命令或其他软件的菜单。

v0.6.2 在添加、移除和修复后主动发送 Windows 外壳刷新通知，并在 Explorer 尚未提供项目属性时延后查询，避免直接隐藏命令。原注册范围已经包含全部文件和文件夹；桌面出现、文件夹不出现可能与 Explorer 缓存有关，不能仅凭包注册成功认定界面已正常。点击「修复归序菜单」会重新注册同版本组件并刷新。添加或修复后请关闭菜单再重新右键，重开文件夹窗口；如果仍未出现，可退出并重新登录 Windows，使资源管理器重新加载注册。归序不会自动强制重启资源管理器。

外壳刷新遵循 [微软 SHChangeNotify 说明](https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/nf-shlobj_core-shchangenotify)。菜单属性暂时不可用时使用 [GetState 的延后状态查询](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-iexplorercommand-getstate)。

## 本地开发签名

此个人开发版使用自签名代码签名证书。添加菜单会将随附的公开证书加入 **LocalMachine / TrustedPeople**，证书信任对本机所有账户生效，菜单仍只注册给当前账户，不添加到根证书库，不改变开发者模式或执行策略设置。移除时清理由归序新增的信任；已有的同证书信任会保留。

首次添加时会明确询问信任签名与注册菜单的授权，随后请求管理员确认。管理员辅助步骤只操作本机信任；包注册由原账户执行。取消管理员确认不添加信任或注册菜单。其他账户仍使用同发布者菜单时保留信任，最后一个账户移除时才清理。包注册失败后可能保留已建立的信任，供重试或卸载时清理。

构建脚本将不可导出的私钥留在构建者的 CurrentUser / My 证书库，仅把公开 `.cer` 随安装包提供。源码和安装包不包含私钥。CI 会在临时构建环境中生成自己的本地签名。因此当前产物适合个人开发、试用；面向公众分发时应换成正式的代码签名证书，并更新对应发布者配置。

微软说明一级菜单扩展需要 `IExplorerCommand` 和应用身份，普通旧式注册表菜单进入「显示更多选项」。归序采用独立原生 COM 组件和带外部路径的 MSIX 身份包，主程序仍放在原来的目录。[微软菜单扩展说明](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/integrate-packaged-app-with-file-explorer)、[身份包说明](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps)、[本机签名信任要求](https://learn.microsoft.com/en-us/windows/msix/msix-troubleshooting-guide)。

## 文件和开发入口

- `Shell/TidyDrop.Shell.dll`：原生命令。只在实际点击时取所选项目的文件系统路径，写入 Unicode 剪贴板，短暂占用时重试。
- `Shell/TidyDrop.CopyPath.msix`：为文件 `*` 和文件夹 `Directory` 注册同一个命令，没有子菜单。
- `Shell/TidyDrop.CopyPath.cer`：当前构建的公开签名证书。
- `Shell/context-menu.ps1`：当前用户注册、状态查询、移除与信任清理。
- `HKCU/Software/TidyDrop/CopyPathMenu`：记录当前账户的组件绑定目录。
- `HKLM/Software/TidyDrop/CopyPathTrust`：记录归序新增的本机信任指纹，清理时只处理这些证书。与归档根目录、材料清单无关。

程序目录固定后再添加菜单。移动程序目录后，在新位置重新点击添加菜单以重新绑定。移动材料的归档目录不影响这个菜单。

编译需要 Visual Studio 2022 的 C++ 桌面开发工具和 Windows 11 SDK；菜单 DLL 使用静态 C++ 运行库。完整构建调用 `scripts/build.ps1`，包含构建原生组件、本地签名和独立复制检查，再发布主程序。

单独运行：

```powershell
./scripts/build-shell.ps1
./scripts/sign-shell.ps1
./scripts/check-shell.ps1
```

真实注册后可执行 `./scripts/check-shell.ps1 -Registered`，通过 Windows 注册的 COM 类在系统代理进程中验证复制，而非直接加载 DLL。检查使用独立生成的测试材料，检查完恢复原剪贴板。界面联动检查用模拟菜单状态验证添加、移除按钮与消息桥接，不改真实系统菜单。

若程序目录已丢失，需要手动取消当前用户的注册：

```powershell
Get-AppxPackage -Name TidyDrop.CopyPath | Remove-AppxPackage
```

后续升级组件时同步更新身份包版本与注册脚本的版本判断。不要复用本地开发签名进行公众发行。
