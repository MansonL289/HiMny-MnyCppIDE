# MnyC++ IDE

一个基于 **WinUI 3 / .NET 8 + Monaco Editor** 的轻量 C++ 集成开发环境（Windows x64），原生 WinUI 外壳 + WebView2 承载的 Monaco 编辑器。**内置 MinGW-w64 工具链**，装好即可编译、运行，不用另外搭环境；同时内置面向算法练习的刷题模式。

当前版本号见 `WinUI3Shell/WinUI3Shell.csproj` 的 `<Version>`；升级版本时需同步修改安装包的 `AppVersion`。

## 运行环境

- Windows 10 1809（17763）及以上，x64
- 无需预装 .NET（独立发布）、无需预装 MinGW（内置 g++）
- 编辑器与刷题功能依赖 **Microsoft Edge WebView2 运行时**：安装包会在缺失时自动补装，解压版需要系统已安装

## 安装

**方式一：安装包**。运行 `MnyCppIDESetup.exe`，安装程序会检测 WebView2 运行时并按需静默安装，安装完成后可直接启动。升级时沿用原安装目录，并自动关闭正在运行的程序。

**方式二：解压即用**。把发布包 zip 解压到任意目录，双击 `MnyMinecraftLauncher.exe`（文件名沿用程序集名，窗口标题为 MnyC++ IDE）。

设置与日志保存在 `%LOCALAPPDATA%\MnyC++`，卸载程序默认保留，可在卸载时勾选一并清除。

## 功能

### 编辑器
- **Monaco 0.52.2** 随包分发，完全离线可用；深色主题跟随系统明暗切换
- 按扩展名识别 C/C++、Python、JavaScript/TypeScript、Java、Rust、Go、JSON、XML、Shell、PowerShell、SQL、YAML 等
- C++ 专属补全：`#include <...>` / `#include "..."` 头文件名补全
- 代码补全、参数提示、悬浮文档、括号着色、折叠、大纲、转到定义、查找引用、重命名、格式化、多光标、迷你地图、自动换行、字号缩放
- **C++ 语义着色与头文件检查**：本文件声明的函数/类型、已包含头文件对应的库符号着色；使用了 `printf`、`cout` 等却没包含对应头文件时显示黄色波浪线
- **大文件模式**：超过 30 MB 自动关闭重型特性并用 256KB 分块传输；按 `Alt+F` 局部高亮当前视口

### 文件与工作区
- 左侧活动栏：文件管理器、代码检查器、代码大纲
- 打开文件夹作为工作区，树形浏览，自动跳过 `.git`、`bin`、`obj`、`node_modules` 等
- 多标签编辑，支持新建、打开、保存、另存为、关闭；关闭窗口时若有未保存文件会确认

### 编译与运行
- 内置 MinGW-w64（`WinUI3Shell\Tools\mingw64`，构建时复制为 `Assets\mingw64`）；找不到时回退系统 `PATH` 中的 `g++`
- 默认命令：`g++ -g -static -std=c++17 <源文件> -o <同名>.exe`，设置里追加的编译/连接器参数排在最后，可覆盖默认标准
- 编译日志实时回显到终端面板，超时 120 秒自动终止
- 运行会弹出**独立控制台窗口**，结束后显示退出码与耗时；运行按钮仅在当前文件是 C/C++ 源码时出现

### 终端
- 终端面板（`` Ctrl+` ``）运行真实 `cmd.exe` 会话，支持多标签、新建、清空、关闭全部

### 刷题模式
- 从主页进入，支持洛谷、AtCoder、牛客三个平台搜索与题面抓取，结果可排序、标星收藏
- 洛谷深度支持：题单三级导航、官方标签筛选、答题状态显示、题号直达、答题代码回填
- 题面转换为 Markdown + LaTeX 渲染，非中文题面可分段翻译（保留代码块与公式）
- 题目页内置 C++ 编辑器，可编译并逐样例本地评测（5 秒超时、256KB 输出上限），按用例卡片展示结果
- 登录支持网页登录与**从浏览器导入 Cookie**（Edge、Chrome、Brave、Vivaldi、Opera），导入失败自动回退网页登录

### 设置
- 左侧导航：通用 / 编辑器 / 编译选项 / 新文件模板 / 关于
- 通用页可调整界面缩放并自动匹配当前屏幕；编辑器设置改动即时生效
- 关于页显示软硬件信息、检查更新（优先读取 GitHub Release，其次 tags）、反馈入口与打开日志目录

## 快捷键

`Ctrl+N` `Ctrl+O` `Ctrl+S` `Ctrl+Shift+S` `Ctrl+W` `Ctrl+F` `Ctrl+H` `Ctrl+G` `Ctrl+Shift+O` `Ctrl+Space` `F2` `F10` `F11` `F12` `` Ctrl+` ``

`F12` 编译、`F10` 运行、`F11` 编译并运行；编辑器内支持 `Ctrl+滚轮`、`Ctrl+=`、`Ctrl+-`、`Ctrl+0` 缩放。

## 构建

前置要求：.NET 8 SDK，以及 WinUI 3 / Windows App SDK 开发环境。

```powershell
dotnet build WinUI3Shell/WinUI3Shell.csproj -c Debug -p:Platform=x64
python 直接运行用我.py
```

## 打包分发

一条命令打出包含 .NET 8 与 Windows App SDK 运行时的独立包：

```powershell
dotnet publish WinUI3Shell/WinUI3Shell.csproj -c Release -p:Platform=x64 -p:SelfContained=true -p:WindowsAppSDKSelfContained=true
```

或直接运行 `Tools\一键打包_C++.bat`，它会发布、刷新 `Tools\pub-cpp` 并生成 zip。之后在 Inno Setup 中打开 `Installer\MnyCppIDE.iss` 编译安装包。

发布产物约 700 MB，其中约 500 MB 是内置 MinGW 工具链，需要整体分发，不能只拷 exe。

## 数据位置

- 设置：`%LOCALAPPDATA%\MnyC++\settings.json`（目录不可写时回退到程序目录）
- 日志：`%LOCALAPPDATA%\MnyC++\logs\app-yyyyMMdd.log`，保留 7 天，单文件上限 4 MB
- 程序遇到未处理异常时会写日志并弹窗提示，可在设置「关于 → 反馈与意见 → 打开日志目录」中查看

## 说明

- 可执行文件名沿用程序集名 `MnyMinecraftLauncher`，窗口标题显示为 **MnyC++ IDE**
- 仓库根目录的 `一键推送.bat` 可交互式输入更新说明，完成 add → commit → push
- 发布 tag 建议使用 `v1.0.0` 形式，关于页检查更新时会自动忽略 `v` 前缀
