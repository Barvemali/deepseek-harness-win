# Agent Note: Windows App SDK 客户端壳层

Status: implemented

[English](2026-08-14-windows-app-sdk-client-shell.md) | 中文

## 问题

dsh Web GUI 只能从终端启动：运行 `dsh web`，再在浏览器中打开打印出的 URL。没有桌面入口；此前唯一的桌面方案——Electron 通过 `file://` 加载 dist 并经 IPC 桥承载 fetch——从未落地，且需要自建传输层。

## 决策

`apps/windows/DshShell` 是一个非打包的 WinUI 3 壳层（Windows App SDK 2.4、.NET 10（目标为 `net10.0-windows10.0.26100.0`）、x64、自包含 WinAppSDK 运行时），用 WebView2 控件承载现有的 `dsh web` 服务器输出。启动时先探测配置的 URL（默认 `http://127.0.0.1:3080`），没有服务在监听时再运行配置的启动命令，等待 URL 就绪后加载页面。默认命令是根 `dsh` 脚本自身的调用——`node --import tsx/esm apps/cli/src/bin.ts web --no-open`——在仓库根目录（通过从可执行文件向上查找 `pnpm-lock.yaml` 定位）运行；它刻意绕开 pnpm，因为 pnpm 的依赖状态检查与 corepack shim 都会在无控制台拉起时提示并挂起，并传入 `--no-open`，因为壳层取代了浏览器。自 dsh 0.1.3-alpha.1 起，服务器要求就绪行上打印的每次启动的进程 token（`dsh web: http://…/?token=…`）来签发浏览器 cookie，因此壳层从服务器输出捕获该 token，并在拉起服务器时导航到带鉴权的 URL；挂接模式依赖之前 spawn 在持久化 WebView2 配置中留下的 cookie。关闭窗口只结束壳层自己启动的进程树；挂接到外部已运行的服务器时则不加干预。

页面始终由 `dsh web` 提供：只有该服务器注入 `window.__DSH_BOOT__`，因此 WebView2 加载的是服务端页面，信任栅栏、`/api` 桥与启动注入都保持不变。每次启动壳层都会清除 WebView2 的 HTTP 缓存（`Network.clearBrowserCache`）：`dsh web` 提供的 `index.html` 没有缓存校验头，否则 git pull 重建 dist 后持久化配置会一直提供旧 GUI——旧 bundle rev 调用旧 RPC 契约。WebView2 渲染进程也可能停止绘制并停止响应输入，而窗口其余部分照常工作，于是留下一个看上去还活着、却对任何点击都无反应的窗口；`Reload` 修不好它，因为冻结的正是要执行重载的那个进程。因此壳层运行一个 10 秒看门狗：调用 `ExecuteScriptAsync` 并在 5 秒内要应答，且把刚开始的导航视为进行中；一次无应答就重新加载页面，连续三次就用同一服务器 URL 关闭旧控件并新建控件来重建视图；`CoreWebView2.ProcessFailed` 在渲染、GPU 或帧进程失败后重新加载，在 `BrowserProcessExited` 后重建。重建不触碰服务器进程，因此不会丢掉正在运行的服务器所持有的会话；工具栏"Rebuild view"按钮与 Ctrl+Shift+R 可随时触发同一次重建；导航、进程失败与重建都会与服务器输出一起记入日志。设置持久化在 `%LocalAppData%\DshShell\settings.json`；壳层与服务器输出共用 `%LocalAppData%\DshShell\server.log`；单实例互斥体防止两个窗口争抢同一个服务器进程。`apps/windows` 刻意不是 pnpm workspace 成员（无 `package.json`），因此发布成员与约束门禁不会看到它。

拉起的服务器以非交互方式运行：壳层设置 `COREPACK_ENABLE_DOWNLOAD_PROMPT=0`（PATH 上的 corepack shim 在缓存缺失固定 pnpm 版本时会在 "Do you want to continue? [Y/n]" 上挂起）与 `npm_config_verify_deps_before_run=false`（pnpm 10+ 在运行脚本前会做依赖状态检查，git pull 改变 lockfile 后提示 "The modules directories will be removed and reinstalled from scratch. Proceed?"；`confirmModulesPurge` 不覆盖该检查）。就绪前失败的启动会在状态栏给出退出码，并在对话框中给出修复命令，依据是壳层保留的最后 60 行服务器输出：`MissingClientBundleError` 指向 `pnpm install`、`pnpm run build:lib` 与 `pnpm run build:web`；无法解析的模块指向 `pnpm install`；绑定或写入失败则指明端口或数据目录。另外，`dotnet build` 会把 `resources.pri` 的 MSBuild 任务解析到 .NET SDK 目录，而那里没有 `Microsoft.Build.Packaging.Pri.Tasks.dll`；`DshShell.csproj` 中的 `AppxMSBuildToolsPath` 回退把该路径指向携带此任务程序集的 Visual Studio 安装（用 Visual Studio 的 `MSBuild.exe` 构建则无需回退）。

## 曾考虑的替代方案

**Electron 走 `file://` + IPC fetch 桥。** 不予采纳：该桥并不存在，需要新建并信任一套传输层，而且代码库中关于 Electron 的提及从未落地。

**Tauri 或其他 WebView2 包装。** 不予采纳：C# + WinUI 保持单一 Windows 原生技术栈，WebView2 本就是 Windows 平台组件，额外工具链在此并无收益。

**保留终端加浏览器的工作流。** 不予采纳：壳层的意义就在于无需终端的桌面启动。

**MSIX 打包。** 首版不予采纳：非打包加自包含 WinAppSDK 保持 `dotnet build` 与直接运行 exe 的路径；打包可后续叠加。

## 后果

壳层需要装有 WebView2 运行时的 Windows、用于 `dotnet build` 下 `resources.pri` 生成的 Visual Studio 安装（或改用 VS 的 `MSBuild.exe`），以及已构建的前端 `dist/`——这与 `dsh web` 自身的先决条件相同。两个 lib 面都必需且须按序执行：`pnpm run build:lib` 的 host 阶段生成 client 侧类型检查所消费的 Typert 声明（`lib/typert.host.d.ts`、`lib/typert.remote-client.d.ts`），因此只跑 `pnpm run build:lib:client` 会以无法解析的 `/remote` 模块失败，根本到不了打包阶段。若某次 pull 删除了包，该包残留的 `lib/` 会留在没有 `package.json` 的目录中，打包阶段仍会读取并以 `MISSING_EXPORT` 失败，直到 `pnpm run clean` 将其删除。壳层启动服务器时拥有该进程树的生命周期；已经自行运行 `dsh web` 的用户会得到一个纯挂接窗口。非交互启动标志意味着 git pull 之后的首次启动可能较慢（pnpm 需要重装变化的依赖）。
