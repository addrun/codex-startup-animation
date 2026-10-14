# 开发与素材制作

本项目针对 Windows 10/11 64 位桌面。运行程序使用 WPF 与 .NET Framework；测试使用隔离的模拟应用，不能用真实 Codex 做超时、挂起或强制终止测试。

## 构建

从项目根目录运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

使用系统 .NET Framework 4.x 的 `csc.exe`，无需 .NET SDK 或在线恢复包。生成根目录的两个 EXE。若正在运行此目录的监视器，先用 `Exit-Animation-Watcher.ps1` 请求退出，构建后再启用；也可复制源码到独立目录验证编译。

## 测试

共享动画单实例锁的套件需要顺序执行。普通自动检查使用全零 PCM 或静音参数；有声音的测试明确单列。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\run-integration.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\run-page-integration.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\run-page-probe-lifetime.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\run-watcher-integration.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\run-watcher-lifecycle.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\run-sound-integration.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\run-sound-overlay.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\run-intro-fade.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\run-lifetime-regression.ps1
```

测试生成文件写入 `tests/output/`，仅本机保存，不放入 Git 或发布包。生命周期回归使用自己的模拟宿主和进程，不结束真实 Codex。

`run-watcher-lifecycle.ps1` 用不含媒体的假观察器检查托盘隐藏/恢复、原生窗口销毁/重建、已有隐藏进程基线、子进程、临时激活与完全退出后快速重开。可用 `-WatcherPath <待测EXE>` 指向隔离构建；它不播放声音或读取真实 Codex。完整媒体和淡出仍由 `run-watcher-integration.ps1` 分别验证。

**以下补充测试会播放正式音效一次：**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\run-watcher-audio-output.ps1
```

只检查该测试动画进程的音频会话电平，不录制麦克风、不修改系统音量。

## 屏幕与离屏检查

屏幕采集检查需要未被遮挡的交互式桌面，只采样明确归属本项目的模拟窗口区域。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\run-intro-fade.ps1 -Cases early -CaptureScreen
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\run-transition-screen.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\run-tail-screen.ps1 -Mode both
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\render-waiting-preview.ps1
```

前两项分析需要已有 Python 环境中的 Pillow 9.1+、NumPy，可加 `-PythonPath <解释器完整路径>`。自动查找只使用 PATH 中的真实解释器，排除 Microsoft Store 占位程序；脚本不会安装包。片尾采集结果可用 `tests/verify_tail_visuals.py` 与离屏导出目录进一步核对。运行 `Start-Codex-Dragon.exe --tail-test` 可检查片尾的眼睛、光点、头发、减少动画与资源释放，不启动 Codex。

## 可选 Python 工具

正常运行动画和编译 C# 无需 Python。仅素材制作或图像分析需要 Python 3.9+ 与第三方库，使用 Pillow 的工具要求版本 9.1 或更新：

| 工具 | 已安装环境中需要的包 | 结果 |
| --- | --- | --- |
| `build-eye-motion.ps1` / `tools/build_eye_motion.py` | NumPy、OpenCV | 对齐中间眼睑关键帧，重建运行图集 |
| `tools/build_portrait_detail.py` | Pillow、NumPy | 温和增强人物与眼睑纹理 |
| `tools/pack_waiting_preview.py` | Pillow | 将导出帧制作成预览 GIF |
| 图像分析脚本 | Pillow、NumPy | 分析自有模拟窗口的采样结果 |

依赖名称列在 `tools/requirements-art.txt`，安装应由使用者在自己的工具环境中管理。不要向运行工程混入另一解释器的 `site-packages`。

```powershell
# 先在选定解释器中核对依赖
& '<Python解释器完整路径>' -B -c 'import PIL, numpy, cv2'

# 此操作会重新生成素材；请在制作副本中运行
powershell -NoProfile -ExecutionPolicy Bypass -File .\build-eye-motion.ps1 -PythonPath '<Python解释器完整路径>'
& '<Python解释器完整路径>' -B .\tools\build_portrait_detail.py
```

`dragon-eye-quarter.png`、`dragon-eye-half.png`、`dragon-eye-threequarter.png` 是图集重建输入，不能当作运行无用文件删除。原图和生成提示也用于素材来源记录。

## 重新编码片头视频

已有 MP4 可以直接运行。只有修改视频内容并重新导出时才需要 PATH 中已有的 FFmpeg 与 FFprobe：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\render-preview.ps1
```

此脚本会生成逐帧 PNG，再写入正式视频位置；应在制作副本中操作。视频没有内置音轨，正式欢迎语由独立 WAV 播放。历史音频合成工具和未选用音轨已归档，不用于重建当前混音。

## 发布打包

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\package-release.ps1 -Version local
# 仅源码：无需 EXE
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\package-release.ps1 -Version local -Kind Source
# 仅 Windows 包：必须已有构建的 EXE
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\package-release.ps1 -Version local -Kind Windows
```

可用 `-OutputDirectory <输出目录>` 指定包的位置，输出必须在项目目录之外。脚本不编译、不操作监视器、不触发 Codex，也不上传 GitHub。每次生成独立目录和 `SHA256SUMS.txt`；每个 ZIP 内有逐文件清单，排除运行日志、窗口几何、登录项、测试输出、历史试听和本机备份。

源码包应作为仓库内容；Windows ZIP 可用于 Release 附件。公开发布之前，按 `THIRD_PARTY_NOTICES.md` 处理尚未明确的原项目和素材授权。
