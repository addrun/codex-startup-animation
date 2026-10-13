# Codex 龙娘启动等待动画

给 Codex 桌面应用的启动等待加一段龙娘动画：继续点击官方图标，Codex 与动画同时运行；完整片头和欢迎语结束后，根据页面就绪情况从动态片尾透明淡出，进入主界面。

![动画预览](docs/images/preview.png)

[下载与版本列表](https://github.com/mysterious-shhr/codex-startup-animation/releases) · [安装与启用](#安装与启用) · [配置](#配置) · [常见问题](#常见问题) · [开发说明](docs/DEVELOPMENT.md) · [验证记录](VALIDATION.md) · [公开前隐私检查](docs/PRIVACY_REVIEW.md)

这是独立的 Windows 桌面辅助程序，未由 OpenAI 发布或背书。源码基于 [mytjxs/codex-startup-animation](https://github.com/mytjxs/codex-startup-animation) 修改；第三方代码、角色和音频来源见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。当前项目没有统一开源许可证，不能将整个仓库视为 MIT 授权。

## 项目特点

- **保留官方入口，并行启动。** 监视器观察 Codex 进程与窗口事件，不改官方快捷方式。不等待动画播完才启动 Codex，也不重复发送启动请求。
- **完整片头，按需等待。** 正式视频为 2560×1440、60 fps、8 秒；正常交接至少播放至 8.30 秒，覆盖完整欢迎语。页面提前就绪仍保留片头，片头之后的等待可随页面情况结束。
- **片尾持续有动效，不重播开头。** 约 7.75 秒、原视频淡出前转入固定镜头的等待场景：眼睛眨动、外侧发束轻摆，左侧文字保留流光和光点。背景、面部和文字布局保持固定；音频结束后不循环播放。
- **页面检测与透明淡出。** 主窗口可见、能响应，并检测到主要页面内容连续稳定 0.5 秒后，在片头完成时用约 0.45 秒整体透明淡出，露出下方的 Codex。
- **窗口边界衔接。** 新启动使用普通 16:9 窗口，动画与 Codex 的可见边界对齐。记录普通窗口位置和高度，考虑显示器工作区、DPI 与不可见边框；只在启动交接阶段校准，之后可自由调整和最大化。
- **声音、跳过与减少动画。** 自带 8.30 秒欢迎语混音，默认音量 44%。Esc、Enter、空格和关闭按钮可随时跳过；总等待上限为 40 秒。遵循系统减少动画设置时使用静止、静音画面，页面就绪后直接交接。
- **每次启动只显示一次。** 官方桌面应用与 CLI 分开识别，Electron 子进程归并处理。已有窗口切换、最小化恢复及监视器重启不补播；所有主窗口关闭后重新待命。
- **检测与状态留在本机。** 页面检测不做 OCR、不请求聊天文字或输入值，不保存或上传目标窗口截图；没有云端识图或遥测接口。本机运行状态默认排除在 Git 和发布包之外。

## 一次启动如何交接

| 阶段 | 默认行为 |
| --- | --- |
| 点击官方图标 | 官方应用自行启动；监视器观察到新的启动事件后，以被动 `--observe` 模式显示动画 |
| 片头播放 | 视频播放一次，独立 WAV 与片头时钟同步，页面检测同时进行 |
| 约 7.75 秒 | 转到动态等待画面，避免停在原视频淡出后的黑屏，不返回视频开头 |
| 至少 8.30 秒 | 正常交接保留完整片头与欢迎语；提前检测到就绪也会等待到这里 |
| 就绪稳定 0.5 秒 | 窗口边界已对齐、窗口可见且响应、页面内容信号稳定，片头完成后开始交接 |
| 约 0.45 秒 | 整个动画窗口透明淡出，随后释放视频、音频、窗口和计时器 |
| 等待达到 40 秒 | 收起动画，Codex 继续运行；也可随时手动跳过 |

页面检测与片头播放并行，表中的时间并非逐项累加。手动跳过、超时、媒体故障和系统减少动画设置可以提前结束展示；“完整播放”指正常就绪交接流程。

这里的“就绪”表示**主要界面已画出且原生窗口能响应**，不保证所有网络请求、聊天同步或后台任务都已完成。不同版本、GPU 和 Windows 取图兼容性可能影响判断，具体边界见 [检测方式与隐私](#检测方式与隐私)。

## 下载哪个文件

在 [Release 列表](https://github.com/mysterious-shhr/codex-startup-animation/releases) 中选择较新的版本，并从它的 Assets 下载。当前运行版本为 [v2026.10.13](https://github.com/mysterious-shhr/codex-startup-animation/releases/tag/v2026.10.13)，标记为预发布，已包含眼部竖线修复：

| 文件 | 用途 |
| --- | --- |
| `codex-startup-animation-windows-*.zip` | 直接使用：包含两个 EXE、启停脚本、配置、必要素材、正式视频和说明 |
| `codex-startup-animation-source-*.zip` | 修改或自行编译：包含源码、测试和素材工具，不含 EXE |
| `SHA256SUMS.txt` | 核对各发布 ZIP 的 SHA-256 |
| GitHub 自动提供的 `Source code (zip/tar.gz)` | 对应版本仓库快照；本机 EXE 不进入 Git，需自行构建 |

运行包需要 **Windows 10/11 64 位、.NET Framework 4.x、可用的交互式桌面和已安装的官方 Codex 桌面应用**。正常使用无需 Python、FFmpeg、Node.js 或管理员权限，视频使用 Windows/WPF 媒体播放组件。

```powershell
# 与 SHA256SUMS.txt 中对应文件的哈希比较
Get-FileHash .\codex-startup-animation-windows-*.zip -Algorithm SHA256
```

## 安装与启用

1. 完整解压 `codex-startup-animation-windows-*.zip` 到固定且可写的目录，保留 EXE、配置、`assets/` 和 `output/` 中的正式视频。不要只复制一个 EXE，也不要在 ZIP 内直接运行。双击 `Preview-Dragon.cmd` 可先查看完整视频和当前音效，不打开 Codex。
2. 在该目录打开 PowerShell，启用当前用户的后台监视和登录自启动：

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\Enable-Official-Codex-Animation.ps1
   ```

3. 结束当前 Codex 会话、关闭所有 Codex 主窗口，再点击原来的官方图标验证。首次启用不会为已经打开的 Codex 补播动画。

启用不需要管理员权限，也不改写官方快捷方式。登录入口通过当前桌面的 Explorer 独立启动监视器，使其在 Codex 退出后继续运行。通知区域会出现“Codex 龙娘动画监视器”图标，可查看状态、打开项目目录或退出。

这里的登录自启动只运行动画监视器，不会主动打开 Codex。官方 Codex 自身的登录项与监视器独立，在 Windows「启动应用」中可能显示为 `ChatGPT`。如果希望登录后保持 Codex 关闭、手动点击官方图标时再播放动画，可关闭该官方登录项并保留动画监视器。

```powershell
# 查看实际进程和心跳
powershell -NoProfile -ExecutionPolicy Bypass -File .\Get-Animation-WatcherStatus.ps1

# 仅退出本次后台监视，保留登录项
powershell -NoProfile -ExecutionPolicy Bypass -File .\Exit-Animation-Watcher.ps1

# 停用：停止本项目监视器并撤销本项目拥有的登录项
powershell -NoProfile -ExecutionPolicy Bypass -File .\Disable-Official-Codex-Animation.ps1
```

退出监视器后，重新运行启用脚本或重新登录 Windows 才会继续监视。停用只处理本项目拥有的监视器与登录快捷方式，不卸载 Codex，也不改变官方应用和其他程序的登录项。启停记录与相关快捷方式备份保存在本机 `output/watcher-control/`。

## 升级与移动目录

1. 在旧目录运行 `Exit-Animation-Watcher.ps1`，确认监视器退出，并等待当前动画结束。
2. 将新版运行包解压到新目录，保留旧目录作为回退副本。按需迁移自己的 `launcher.config.xml`，并与新版新增参数核对。
3. 如需保留窗口位置，可复制自己的 `output/window-geometry.xml`。运行日志不需要迁移或分发。
4. 在新目录运行 `Enable-Official-Codex-Animation.ps1`，更新登录项，再检查状态和预览。

移动现有目录时也先退出监视器，移动后从新位置重新启用。启用脚本会识别本项目拥有的旧登录项、保存备份并更新为当前目录；脚本和默认媒体路径从自身目录解析，无需固定盘符。

已运行的监视器会占用自己的 EXE，出现覆盖或编译失败时先请求退出再重试。停用完成且不再需要配置或备份后，可删除项目目录。

## 预览与独立入口

| 入口 | 行为 |
| --- | --- |
| `Preview-Dragon.cmd` | 播放完整视频和当前音效，不启动 Codex |
| `Preview-Waiting-Dragon.cmd` | 预览约 16 秒的动态等待画面，不启动 Codex |
| `Start-Codex-Dragon.cmd` | 独立启动动画，同时主动启动 Codex |
| `Start-Codex-Dragon.exe --observe` | 只观察目标应用，禁止发出 Codex 激活请求 |
| `Start-Codex-Dragon.exe --preview --silent` | 静音预览，不启动 Codex |
| `Start-Codex-Dragon.exe --preview --reduced-motion` | 预览减少动画模式，不启动 Codex |

日常继续从官方图标进入，由监视器调用 `--observe`；独立入口适合单独使用或排查问题，无需将它替换为默认快捷方式。预览、离屏渲染和被动观察均不激活 Codex；观察模式的激活请求数在模拟验证中为 **0**。

## 配置

编辑 [launcher.config.xml](launcher.config.xml) 后，下次启动的动画进程会读取新设置。所有素材路径默认相对于项目目录；改变目标应用身份或路径时，还需退出并重新启用监视器。

| 参数 | 默认值 | 用途 |
| --- | --- | --- |
| `startupTimeoutSeconds` | `40.0` | 整个启动等待的上限，支持 1～120 秒 |
| `windowReadyDelaySeconds` | `0.5` | 就绪状态需连续稳定的时间，支持 0～5 秒且应小于超时 |
| `requirePageContent` | `true` | 检测主要页面内容，减少交接后仍显示加载页的情况 |
| `introDurationSeconds` | `8.30` | 正常交接前的完整片头下限 |
| `tailHoldSeconds` | `7.75` | 视频切入动态等待画面的时刻 |
| `readyFadeSeconds` | `0.45` | 就绪交接的透明淡出时间，支持 0.1～1 秒 |
| `blinkIntervalSeconds` | `4.0` | 眨眼间隔，支持 1～30 秒 |
| `blinkDurationSeconds` | `0.28` | 单次眨眼时长，支持 0.1～1 秒且应小于间隔 |
| `soundEnabled` | `true` | 启用声音 |
| `soundVolume` | `0.44` | 本项目播放音量，范围 0～1 |
| `soundFile` | `assets/dragon-startup.wav` | 8.30 秒欢迎语混音 |
| `preferDetailTextures` | `true` | 使用温和增强的角色纹理 |
| `respectReducedMotion` | `true` | 遵循系统减少动画设置 |

`appUserModelId` 默认识别官方打包应用。`executablePath` 和 `executableArguments` 留空，通常无需修改；自定义 EXE 配置适用于开发、模拟测试或明确知道目标路径的情况。不要填入 Codex CLI。

当前 WAV 未烘焙整体 44% 衰减，播放时只应用一次 `0.44`。不要用已降低至 44% 的试听 WAV 直接替换，否则会重复降低音量。只调整本项目播放器，不修改 Windows 全局音量。

`durationSeconds`、`frameRate` 是素材渲染参数，改变 XML 不会重定时已有 MP4。替换视频时需核对时长和片尾切点；替换声音时需核对片头下限及音轨长度。增强纹理主要改善局部对比，不能重建源图缺失细节。

窗口位置与高度保存在本机 `output/window-geometry.xml`。首次以 830 像素高度换算为约 1476×830 的 16:9 可见边界，超出显示器工作区时等比缩小并调整位置。后续使用记录的普通窗口高度；即使上次关闭时最大化，下次启动也按普通 16:9 边界交接，完成后仍可自由调整和最大化。

## 检测方式与隐私

监视器按官方应用身份、安装路径及主窗口识别 Codex，排除 CLI 和 Electron 子进程。已有窗口的切换、最小化恢复及监视器重启不补播；所有主窗口关闭后重新待命，每个启动周期最多播放一次。

监视器观察进程和窗口事件，不拦截图标点击。冷启动通常能从进程出现时覆盖等待；已有进程内部处理打开请求时，只能从可观察事件出现后开始。WMI 订阅不可用时保留窗口钩子和低频扫描，并在状态中标明降级。

页面检测在本机内存中取得目标窗口画面，缩小后分析界面内容的分布与对比，区分空白/单一加载图形与已绘出的主要页面。取图不兼容时，只检查可访问控件的类型、数量和位置，不请求控件名称、聊天文字、文本模式或输入值，不做 OCR 或云端识图。

像素画面可能包含当前页面内容，但检测只在内存中分析布局特征，不保存或上传目标窗口截图。取图和可访问检测在有超时约束的独立辅助进程中运行，避免接口阻塞动画关闭。它判断主要界面已经画出且窗口能响应，不代表每项网络请求都已完成；没有可靠信号时等到 40 秒上限，也可手动跳过。

本项目保存窗口几何、监视状态、启动事件、计时与错误信息，不读取聊天数据库、登录凭据或 Codex 全局状态文件。日志可能包含本机绝对路径、PID、窗口句柄和设备边界，应作为本机诊断资料处理。默认 Git 规则和发布清单排除这些状态、登录项备份及测试输出，只保留 `output/` 中的正式 MP4。分享故障报告前应去除本机身份信息，不要直接上传整个运行中的 `output/` 目录。开发测试的屏幕采样只针对本项目模拟窗口，公开验证图也来自模拟场景。

视频到片尾的纹理在后台预热，绘制确认后再释放视频。正常退出用临时透明窗口整体淡出，露出下方应用；片尾初始化失败时保留已经解码的画面。媒体错误不会结束 Codex。

## 常见问题

**启用后点击官方图标，为什么没有动画？**

运行状态脚本，确认监视器进程和心跳有效；再确认已关闭所有 Codex 主窗口。首次启用、已有窗口切换和最小化恢复不会触发。检查两个 EXE 和正式 MP4 是否完整，移动目录后重新启用。事件监视降级时扫描仍会补漏，但动画出现时机可能更晚。不要强行结束正在使用的 Codex 来测试。

**为什么 Windows 登录时 Codex 自己打开了？**

本项目自启动只运行监视器。检查 Windows“启动应用”中的官方 Codex 项，它可能显示为 `ChatGPT`。关闭官方应用的登录项不影响手动点击图标，也无需停用动画监视器。

**页面已经出现，动画为什么还继续？**

默认保留完整 8.30 秒片头和欢迎语，片头之后才按页面情况交接；片尾不会重播视频和音频。想立即进入时可按 Esc、Enter、空格或关闭动画窗口。

**动画退出后，页面仍在加载怎么办？**

检测主要界面和窗口响应，不保证全部后台任务完成。达到 40 秒上限、手动跳过或媒体错误也会结束动画。先查看本机 `output/startup-timing.log` 中的结束原因；明确是慢启动超时时，可适当增加 `startupTimeoutSeconds`。将 `requirePageContent=false` 会只检查窗口响应，可能更早露出加载页。

**没有声音，或音量不合适？**

用 `Preview-Dragon.cmd` 检查，确认 WAV 存在、`soundEnabled=true`、`soundVolume` 大于 0，并检查系统输出设备和该应用音量混合器。`--silent` 与减少动画模式会静音。避免重复降低 WAV 和播放器音量；本项目不修改 Windows 全局音量。

**视频不能播放，或片头到片尾出现明显卡顿？**

确认完整使用同版本包及可用的 Windows 媒体组件。程序在后台预热片尾，确认绘制后再释放视频；片尾初始化失败时保留已解码画面，视频缺失时尝试 WPF 场景降级。不同设备仍可能出现解码或绘制延迟，可用预览和本机错误日志定位。关闭动画或媒体错误不会结束 Codex。

**为什么每次启动会恢复普通 16:9 窗口？**

这是为动画与主窗口对齐而采用的启动行为，包括上次退出时最大化的情况。仅在本次交接阶段校准，之后可以自由调整；下次按记录的普通高度重新换算为 16:9，显示器断开时按可用工作区回退。

**怎样完全关闭或卸载？**

运行 `Disable-Official-Codex-Animation.ps1`，确认监视器退出，再删除不需要的项目目录。官方 Codex 图标继续正常使用，停用脚本不修改其他启动应用。

## 源码、构建与发布包

```text
assets/       运行素材、制作关键帧和生成来源
src/          WPF 动画、启动协调、页面检测和后台监视器
tests/        模拟应用与自动检查源码
tools/        素材制作、解释器查找和发布打包脚本
docs/         开发说明、预览与公开验证摘要
LICENSES/     已确认的第三方许可证原文
output/       正式 MP4，以及仅在本机保存的状态与日志
```

在源码目录构建，使用 Windows 自带的 .NET Framework C# 编译器：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

已启用的监视器会占用其 EXE。需要更新运行版本时，先退出监视器，构建后再启用；仅验证源码时可在隔离目录构建。完整测试和可选素材制作见 [开发说明](docs/DEVELOPMENT.md)，验证范围见 [VALIDATION.md](VALIDATION.md)。

当前记录包含 2026-10-12 的 90 项功能验证基线及 2026-10-13 眼部修复的实际回归范围；当前修复未重跑全部历史套件。模拟测试不代表所有真实硬件、Codex 版本、混合 DPI 或登录场景都已验收。共享动画单实例锁的套件应顺序运行；超时、挂起和生命周期测试使用模拟宿主，不以正在使用的真实 Codex 为目标。

生成源码包和 Windows 运行包：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\package-release.ps1 -Version local
```

默认输出到项目旁的 `codex-startup-animation-packages/`，每次创建独立目录。源码包排除 EXE；Windows 包包含当前 EXE、配置和必要媒体。两类包都使用明确的文件清单，排除窗口位置、进程记录、登录项、历史试听及测试输出，并附带文件哈希。

Git 只跟踪 `output/` 中的正式 MP4；本机 EXE 和所有运行记录由 `.gitignore` 排除。上传前请阅读 [第三方来源与许可](THIRD_PARTY_NOTICES.md)，尤其是尚未明确授权的原项目与使用者提供素材。

## 来源与许可

- 原启动动画代码来自 [mytjxs/codex-startup-animation](https://github.com/mytjxs/codex-startup-animation)，来源版本与哈希已记录；现有记录未找到该基准版本的明确许可证。
- 当前欢迎语使用 [lxj5820/dsh-boot-animation](https://github.com/lxj5820/dsh-boot-animation) 音轨，经处理后加入混音，对应来源的 MIT 原文保存在 [LICENSES/dsh-boot-animation-MIT.txt](LICENSES/dsh-boot-animation-MIT.txt)。
- 背景混音由使用者提供，角色与眼睑素材使用图像生成工具制作。背景音公开分发授权和部分角色参考来源的权利尚未在项目记录中确认。

完整说明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。来源音轨的 MIT 条款只覆盖对应来源，不能自动扩展到整个项目；公开可下载也不代表取得所有代码、音频和角色素材的再发布授权。
