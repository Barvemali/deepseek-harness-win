# DeepSeek Harness Windows 壳层

[English](README.md) | 中文

一个 WinUI 3（Windows App SDK）桌面壳层，用 WebView2 控件承载 dsh Web GUI，让 harness 像普通应用一样启动，而不是从终端启动。

## 工作原理

壳层原样嵌入现有的 `dsh web` 服务器：启动时先探测配置的 URL，没有服务在监听时再运行配置的启动命令，等待 URL 就绪后在 WebView2 中加载页面。页面始终由 `dsh web` 提供——只有它能注入 `window.__DSH_BOOT__`——因此协议、bundle 与信任栅栏都无需改动。自 dsh 0.1.3-alpha.1 起，`dsh web` 要求就绪行上打印的**每次启动的进程 token**（`dsh web: http://…/?token=…`）来签发浏览器 cookie；壳层拉起服务器时会捕获该 token 并导航到带鉴权的 URL。挂接到外部已启动的服务器则依赖之前在持久化 WebView2 配置中留下的 cookie。关闭窗口只结束壳层自己启动的进程树；外部已运行的服务器不受影响。

WebView2 渲染进程可能停止绘制并停止响应输入，而窗口其余部分仍然正常——看上去还活着，但点击毫无反应。因此壳层每 10 秒向已加载页面要一次应答；连续三次无应答就用全新的 WebView2 控件重建视图；WebView 进程失败时先重新加载页面，加载无法恢复时再重建。重建只是再次加载服务器 URL，不触碰服务器进程，因此不会丢掉正在运行的服务器所持有的会话；工具栏的"Rebuild view"按钮（Ctrl+Shift+R）可随时手动触发同样的重建。每次导航、进程失败与重建都会记入日志。

## 前置条件

- Windows 10 22H2 或 Windows 11，并装有 [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)（Windows 11 内置）。
- .NET SDK 10（项目目标为 `net10.0-windows10.0.26100.0`）。
- 安装了 Visual Studio（17/18 或 Build Tools）及其 MSBuild AppxPackage 工具——用于生成 `resources.pri`——否则构建会因加载不到 `Microsoft.Build.Packaging.Pri.Tasks.dll` 而失败。
- dsh 仓库至少构建过一次：`pnpm install` 与 `pnpm run build`（`dsh web` 需要前端 `dist/`）。

## 构建与运行

```sh
pnpm shell:windows          # build (if needed) and launch the shell
# or
dotnet build apps/windows/DshShell
# publish a standalone copy:
pnpm shell:windows:publish  # outputs dist-exe/dsh-shell
```

首次启动会在仓库根目录直接运行 `dsh` 脚本的命令——`node --import tsx/esm apps/cli/src/bin.ts web --no-open`（通过从可执行文件向上查找 `pnpm-lock.yaml` 定位仓库根），等待 `http://127.0.0.1:3080` 就绪后挂接窗口。默认命令绕开 pnpm，因为 pnpm 的依赖状态检查与 corepack shim 都会在无控制台拉起时提示并挂起；并传入 `--no-open`，因为壳层取代了浏览器。第二个实例挂接同一服务器；单实例互斥体防止两个窗口争抢同一个服务器进程。

## 配置

齿轮按钮打开服务器设置，持久化到 `%LocalAppData%\DshShell\settings.json`：

| 键 | 默认值 | 含义 |
|---|---|---|
| `Url` | `http://127.0.0.1:3080` | 要探测并加载的 GUI 地址 |
| `Command` | `node --import tsx/esm apps/cli/src/bin.ts web --no-open` | `Url` 无人监听时运行的命令；留空禁用拉起 |
| `WorkingDirectory` | 自动 | 命令的工作目录；留空自动定位仓库根目录 |
| `StartTimeoutSeconds` | `90` | 等待服务器可达的超时秒数 |

壳层与服务器输出都会追加到 `%LocalAppData%\DshShell\server.log`（失败状态下会出现"Open log"按钮）。

## 已知限制

- 拉起的服务器以非交互方式运行：壳层设置 `COREPACK_ENABLE_DOWNLOAD_PROMPT=0` 与 `npm_config_verify_deps_before_run=false`，配置了 pnpm/corepack 命令也不会因提示而挂起启动。
- 壳层既不安装也不构建 harness。`git pull` 之后请先在仓库中运行 `pnpm install`、`pnpm run build:lib` 与 `pnpm run build:web`，再重新启动。两个 lib 面都必需：host 阶段生成 client 侧类型检查所消费的 Typert 声明（`lib/typert.host.d.ts`、`lib/typert.remote-client.d.ts`），因此只跑 `pnpm run build:lib:client` 会以无法解析的 `/remote` 模块失败。若某次 pull 删除了包，该包残留的 `lib/` 输出仍会被打包阶段读取（其 `package.json` 已不存在），并以 `MISSING_EXPORT` 失败；重建前请删除这些无清单目录（`pnpm run clean` 正是为此而设，但在 dsh 0.1.7-rc.2 上它会先在 `tsconfig.desktop-keyboard-tests.json` 的 `lib/desktop-keyboard-test-types` 上报错退出，什么都不会删）。这两种原因导致的拉起失败，状态栏显示退出码，对话框给出修复命令，完整输出留在 `server.log`。
- `dotnet build` 通过 `DshShell.csproj` 中的 `AppxMSBuildToolsPath` 回退从 Visual Studio 安装解析 Pri 生成所需的 MSBuild 任务（支持 VS 18 Community 与 VS 2022 Build Tools 路径）；两者皆无的机器需要 `-p:AppxMSBuildToolsPath=...` 指向其 AppxPackage 任务目录。用 Visual Studio 的 `MSBuild.exe` 构建则无需回退。
- 非打包且自包含 Windows App SDK 运行时（`WindowsPackageType=None`、`WindowsAppSDKSelfContained=true`），无需 MSIX 打包或运行时安装程序；因捆绑了 WinAppSDK 运行时，输出目录较大（约 100 MB）。
