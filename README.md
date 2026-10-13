# Codex 龙娘启动等待动画

在 Codex 桌面应用加载期间播放龙娘动画，继续使用官方 Codex 图标。后台监视器观察应用的启动事件，动画与 Codex 并行运行；主界面提前出现时仍播放完整片头，之后透明淡出进入应用。

![动画预览](docs/images/preview.png)

这是独立的 Windows 桌面辅助程序，不是 OpenAI 官方功能。源码基于 [mytjxs/codex-startup-animation](https://github.com/mytjxs/codex-startup-animation) 修改；来源、音频署名及尚待确认的发布许可见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

## 当前效果

- 8 秒、2560×1440、60 fps 视频播放一次；完整片头与欢迎语至少播放至 8.30 秒。
- 7.75 秒进入动态等待画面，保留眨眼、外侧发束轻摆、文字流光和光点，不从头循环。
- 目标窗口可见、能响应，并检测到主要页面内容连续稳定 0.5 秒后，在片头完成时用约 0.45 秒透明淡出交接。
- 总等待上限为 40 秒。Esc、Enter、空格和关闭按钮可随时跳过，只结束动画。
- 新启动的 Codex 使用普通 16:9 窗口，动画与其可见边界对齐；仅在启动交接阶段校准尺寸，之后可自由调整和最大化。
- 系统开启减少动画时使用静止、静音画面，页面就绪后直接交接。

## 使用 Windows 运行包

需要 Windows 10/11 64 位、.NET Framework 4.x、可用的交互式桌面和已安装的官方 Codex 桌面应用。正常使用无需 Python、FFmpeg 或 Node.js。

1. 完整解压 `codex-startup-animation-windows-*.zip` 到固定目录，保留 EXE、配置、`assets/` 和 `output/` 中的视频。
2. 在该目录打开 PowerShell，启用当前用户的后台监视和登录自启动：

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\Enable-Official-Codex-Animation.ps1
   ```

3. 结束当前 Codex 会话、关闭所有 Codex 主窗口，再点击原来的官方图标验证。首次启用不会为已经打开的 Codex 补播动画。

启用不需要管理员权限，也不改写官方快捷方式。登录入口通过当前桌面的 Explorer 启动监视器，使其在 Codex 退出后继续运行。移动项目后，在新目录重新运行启用脚本以更新本项目登录项。

这里的登录自启动只运行动画监视器，不会主动打开 Codex。官方 Codex 自身的登录项与监视器独立，在 Windows「启动应用」中可能显示为 `ChatGPT`。如果希望登录后保持 Codex 关闭、手动点击官方图标时再播放动画，可关闭该官方登录项并保留动画监视器。

```powershell
# 查看实际进程和心跳
powershell -NoProfile -ExecutionPolicy Bypass -File .\Get-Animation-WatcherStatus.ps1

# 仅退出本次后台监视，保留登录项
powershell -NoProfile -ExecutionPolicy Bypass -File .\Exit-Animation-Watcher.ps1

# 停用：停止本项目监视器并撤销本项目拥有的登录项
powershell -NoProfile -ExecutionPolicy Bypass -File .\Disable-Official-Codex-Animation.ps1
```

启停记录和本项目登录项备份保存在本机 `output/watcher-control/`。停用不会卸载 Codex。

## 预览与独立入口

| 入口 | 行为 |
| --- | --- |
| `Preview-Dragon.cmd` | 播放完整视频和当前音效，不启动 Codex |
| `Preview-Waiting-Dragon.cmd` | 预览约 16 秒的动态等待画面，不启动 Codex |
| `Start-Codex-Dragon.cmd` | 独立启动动画，同时主动启动 Codex |
| `Start-Codex-Dragon.exe --observe` | 只观察目标应用，禁止发出 Codex 激活请求 |
| `Start-Codex-Dragon.exe --preview --silent` | 静音预览，不启动 Codex |

日常从官方图标进入时，由监视器调用 `--observe`。

## 配置

编辑 [launcher.config.xml](launcher.config.xml)。所有素材路径默认相对于项目目录。

| 参数 | 默认值 | 用途 |
| --- | --- | --- |
| `startupTimeoutSeconds` | `40.0` | 整个启动等待的上限 |
| `windowReadyDelaySeconds` | `0.5` | 当前就绪状态需要连续稳定的时间 |
| `requirePageContent` | `true` | 检测主要页面内容，避免停在空白加载页 |
| `introDurationSeconds` | `8.30` | 正常交接前的完整片头下限 |
| `tailHoldSeconds` | `7.75` | 视频切入动态等待画面的时刻 |
| `readyFadeSeconds` | `0.45` | 就绪交接的透明淡出时间 |
| `blinkIntervalSeconds` | `4.0` | 眨眼间隔 |
| `blinkDurationSeconds` | `0.28` | 单次眨眼时长 |
| `soundEnabled` | `true` | 启用声音 |
| `soundVolume` | `0.44` | 本项目播放音量，范围 0～1 |
| `soundFile` | `assets/dragon-startup.wav` | 8.30 秒欢迎语混音 |
| `preferDetailTextures` | `true` | 使用温和增强的角色纹理 |
| `respectReducedMotion` | `true` | 遵循系统减少动画设置 |

当前 WAV 未烘焙整体 44% 衰减，播放时只应用一次 `0.44`。不要用已降低至 44% 的试听 WAV 直接替换，否则会重复降低音量。只调整本项目播放器，不修改 Windows 全局音量。

`durationSeconds`、`frameRate` 是素材渲染参数；改变 XML 不会重定时现有 MP4。增强纹理只提高局部对比，没有重建源图缺失细节，前段 MP4 的清晰度也未改变。

## 检测方式与边界

监视器按官方应用身份、安装路径及主窗口识别 Codex，排除 CLI 和 Electron 子进程。已有窗口的切换、最小化恢复及监视器重启不补播；所有主窗口关闭后重新待命，每个启动周期最多播放一次。

监视器观察进程和窗口事件，不拦截图标点击。冷启动通常能从进程出现时覆盖等待；已有进程内部处理打开请求时，只能从可观察事件出现后开始。WMI 订阅不可用时保留窗口钩子和低频扫描，并在状态中标明降级。

页面检测在本机内存中采样目标窗口的布局和对比；必要时只检查可访问控件的类型、数量和位置，不请求聊天文字、控件名称或输入值，不保存或上传截图。它判断主要界面已经画出且原生窗口能响应，不代表每项网络请求都已完成。版本、GPU 或取图兼容性可能影响检测；没有可靠信号时等到 40 秒上限，也可手动跳过。

视频到片尾的纹理在后台预热，绘制确认后再释放视频。正常退出用临时透明窗口整体淡出，露出下方应用；片尾初始化失败时保留已经解码的画面。媒体错误不会结束 Codex。

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

生成源码包和 Windows 运行包：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\package-release.ps1 -Version local
```

默认输出到项目旁的 `codex-startup-animation-packages/`，每次创建独立目录。源码包排除 EXE；Windows 包包含当前 EXE、配置和必要媒体。两类包都使用明确的文件清单，排除窗口位置、进程记录、登录项、历史试听及测试输出，并附带文件哈希。

Git 只跟踪 `output/` 中的正式 MP4；本机 EXE 和所有运行记录由 `.gitignore` 排除。上传前请阅读 [第三方来源与许可](THIRD_PARTY_NOTICES.md)，尤其是尚未明确授权的原项目与使用者提供素材。
