# WebView2 开发依赖

`nuget/Microsoft.Web.WebView2.1.0.4258.31.nupkg` 来自微软发布的官方 NuGet 包：

https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4258.31

项目通过本地 NuGet 源引用此包，避免桌面应用的 C# 构建步骤依赖网络。运行时使用电脑上安装的 Microsoft Edge WebView2 Runtime，不下载页面或远程脚本。

许可信息和第三方说明位于包内。React、React DOM、Lucide 等前端依赖通过锁定文件管理，构建时打包进本地页面。
