# 第三方来源与发布许可

本项目包含已有启动动画的修改、角色素材和音频混音。以下记录用于保留来源；没有为整个项目添加统一开源许可证。

## 原启动动画项目

- 仓库：[mytjxs/codex-startup-animation](https://github.com/mytjxs/codex-startup-animation)
- 基准提交：`9ea3b32b4e53f2d9db5d54265f6341b63aee5c04`
- 参考版本：`v1.0.0`
- 原 Release ZIP SHA-256：`682928d2e6ce85bfb3d83acadb4e85da9efe4948a593a63e4dc2e605a08ff524`
- 来源记录：[docs/source-record.json](docs/source-record.json)

2026-10-12 核对 GitHub 仓库元数据及基准提交的 `LICENSE` 路径，未找到明确许可证。公开可下载不等于已经获得再发布或再许可授权。公开发布其代码和派生素材前，发布者需要确认原作者授权或另行处理相关内容；不能直接宣称整个项目采用 MIT。

原版 EXE、README 和历史验证已移入项目外本地归档，不随上传包重复分发。

## 欢迎语音频：dsh-boot-animation

- 仓库：[lxj5820/dsh-boot-animation](https://github.com/lxj5820/dsh-boot-animation)
- 基准提交：`a3219aba003639572ffcf19772d0028b6cccd360`
- 使用部分：`assets/videos/1.mp4` 中的音轨，经过人声处理、剪辑和混响，加入当前欢迎语混音。
- 原署名：`Copyright (c) 2026 dsh-boot-animation contributors`
- 许可证：MIT，原文和上游媒体说明保存在 [LICENSES/dsh-boot-animation-MIT.txt](LICENSES/dsh-boot-animation-MIT.txt)。

没有取回或运行上游插件源码，正式项目也不依赖上游安装脚本。当前 WAV 的格式、混音时序、音量和哈希见 [assets/dragon-startup-sound.json](assets/dragon-startup-sound.json)。

## 使用者提供的背景音与角色素材

背景音由使用者提供的两段原始音频剪辑混合而成，其原作者及公开分发许可尚未在现有项目记录中确认。完整原稿、分层音轨、试听和制作脚本保存在项目外本地归档，没有放入上传包；当前正式 WAV 仍含有这些音频的混音结果，发布者需要确认其分发授权。

角色图和中间眼睑姿态使用图像生成工具制作，生成提示保存在 `assets/`。其中部分提示参考使用者提供的角色视频截图；生成过程本身不证明参考角色、造型或原视频的权利归属。现有记录不将这些素材声明为公共领域或赋予未经确认的第三方许可。

## 应用名称

Codex 和 OpenAI 的名称用于说明适配对象。本项目为独立辅助程序，未由 OpenAI 发布或背书。
