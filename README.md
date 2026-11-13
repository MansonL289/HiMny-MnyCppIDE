# MnyC++ IDE

一个基于 **WinUI 3 / .NET 8 + Monaco Editor** 的轻量 C++ 集成开发环境（Windows x64）。原生 WinUI 外壳 + WebView2 承载的 Monaco 编辑器，**内置 MinGW-w64 工具链**，装好即可编译运行 C++ 程序，不用另外搭编译环境。

## 运行环境

- Windows 10 1809 (17763) 及以上，x64
- 无需预装 .NET（独立发布）；无需预装 MinGW（内置 g++）

## 构建与运行

前置要求：安装 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) 和 Visual Studio 的「Windows 应用 SDK C# 模板」工作负载（或直接装 WinUI 3 开发环境）。

```powershell
dotnet build WinUI3Shell/WinUI3Shell.csproj -c Debug -p:Platform=x64
WinUI3Shell\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\MnyMinecraftLauncher.exe
```

仓库根目录的 `直接运行用我.py` 可直接启动已构建的程序：

```powershell
python 直接运行用我.py
```

> 可执行文件名沿用程序集名 `MnyMinecraftLauncher`，窗口标题显示为 **MnyC++ IDE**。

## 打包分发

一条命令打出完整独立包，内置 .NET 8 运行时和 Windows App SDK，对方电脑什么都不用装：

```powershell
dotnet publish WinUI3Shell/WinUI3Shell.csproj -c Release -p:Platform=x64 -p:SelfContained=true -p:WindowsAppSDKSelfContained=true
```

打包完成后：

1. 产物在 `WinUI3Shell\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\`，约 700 MB（其中约 470 MB 是内置的 MinGW 工具链）
2. 把整个 publish 文件夹压缩成 zip 即可分发。别只拷 exe，里面的 dll 都是运行必需的
3. 对方解压后双击 `MnyMinecraftLauncher.exe` 就能用

> MinGW 工具链（`Assets\mingw64`）随 publish 一起分发，对方无需安装编译环境。

> 说明：csproj 里已内置 `CopyXamlArtifactsToPublish` 目标，发布时自动把 `App.xbf` / `MainWindow.xbf` / `SettingsView.xbf` 和 `MnyMinecraftLauncher.pri` 补进 publish 目录，所以上面的命令不需要任何额外步骤，也不会出现发布版闪退的问题。

---

### 编辑器
- **Monaco 0.52.2**，随包分发，完全离线可用；自定义深色主题，跟随系统明暗自动切换
- 按扩展名自动识别语言：C/C++（`.cpp .h .cc .hpp`）、Python、JavaScript/TypeScript、Java、Rust、Go、JSON、XML、Shell、PowerShell、SQL、YAML 等
- **C++ 专属补全**：`#include <...>` / `#include "..."` 可补全头文件名
- 常用能力：代码补全、参数提示、悬浮文档、括号匹配与着色、代码折叠、大纲、转到定义、查找引用、重命名符号、格式化文档、快速修复、多光标、迷你地图、自动换行、字号增减
- **大文件模式**：文件超过 30 MB 自动切到轻量模式（关闭补全 / 悬浮 / 括号着色等重型特性），并分块传输避免卡死；此时按 `Alt+F` 高亮当前视口所在位置

### 文件与工作区
- 左侧活动栏 + 可收起侧栏，「资源管理器」可**打开文件夹**作为工作区，树形浏览目录（自动跳过 `.git`、`.vs`、`bin`、`obj`、`node_modules`）
- 多标签页编辑，支持新建、打开、保存、另存为、关闭文件
- 侧栏可整体收起，把空间让给编辑器

### 编译
- 内置 **MinGW-w64** 工具链（仓库里放在 `WinUI3Shell\Tools\mingw64`，构建时拷到 `Assets\mingw64` 随应用分发）；找不到内置版本时回退到系统 `PATH` 上的 `g++`
- 默认命令：`g++ -std=c++17 -g -static <源文件> -o <同名>.exe`
- `-static` 保证编译出的 exe 脱离 `Assets\mingw64` 也能直接运行
- 编译日志实时回显到终端面板；「运行」按钮只在当前文件是 C/C++ 源码时出现
- **老工程兼容**：设置里追加的编译 / 连接器命令接在默认参数**后面**，所以 `-std=c++11`、`-std=c++14` 这类旧标准可以直接覆盖默认的 C++17

### 运行
- 像 Dev-C++ 那样弹一个**独立控制台窗口**运行 exe，不阻塞 IDE
- 结束后控制台打印退出码和运行耗时（秒）
- 运行脚本按 **GBK（代码页 936）** 写入，中文 Windows 下不乱码

### 终端
- 内置终端面板（`` Ctrl+` ``），跑的是真实的 `cmd.exe` 会话（`/v:on` 延迟展开），支持多标签、新建、清空、关闭全部
- 编译输出与命令回显都走这个面板

### 界面
- 原生 WinUI 外壳：Mica 亚克力背景、内容延伸到标题栏、高标题栏（48px）
- 窗口初始大小为屏幕工作区的 **2/3** 并居中
- 菜单栏：**文件 / 编辑 / 选择 / 查看 / 终端**；常用快捷键 `Ctrl+N` `Ctrl+O` `Ctrl+S` `Ctrl+Shift+S` `Ctrl+W` `Ctrl+F` `Ctrl+H` `Ctrl+G` `Ctrl+Shift+O` `Ctrl+Space` `F2` `F10` `F11` `F12` `` Ctrl+` ``
- 活动栏四个入口：资源管理器（可用）、搜索 / 源代码管理 / 运行和调试（暂为占位面板）

## 说明

- 设置保存在 `%LOCALAPPDATA%\MnyC++\settings.json`（该目录不可写时退回程序目录的 `settings.json`），包含：
  - **编译器命令**：追加到 g++ 命令行
  - **连接器命令**：追加到 g++ 命令行
  - **新建文件模板**：新建 C++ 文件时自动插入的代码
- 编译器输出与终端输出按 **GBK（936）** 解码，适配中文 Windows 控制台
- 仓库根目录的 `一键推送.bat` 可交互式输入更新说明，一键完成 add → commit → push
