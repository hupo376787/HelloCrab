# FFmpeg 音轨检测与自动安装

## 视频音轨检测

“检测视频是否有声音，并自动修复无声视频”默认关闭。

- 关闭：视频下载完成后不调用 `ffprobe`，也不会自动合并背景音乐。
- 开启：使用 `ffprobe` 检查视频是否包含音频轨。
- 检测到无音轨视频且作品提供背景音乐时，临时下载音乐并使用 `ffmpeg` 合并。
- 同时开启“下载背景音乐”时，修复过程中下载的音乐会直接保留，不会重复下载。
- FFmpeg 不可用或检查失败时保留原视频，不删除文件。
- Pinterest 的部分视频（尤其 Idea Pin/故事 Pin）只提供 HLS `.m3u8`，程序会使用 FFmpeg 下载并无损封装为 MP4；若同时存在普通 MP4 直链，FFmpeg 不可用时会自动尝试直链。

## Windows 自动安装

桌面端设置区域提供“下载 FFmpeg”按钮。点击后：

1. 后台访问 `https://www.gyan.dev/ffmpeg/builds/`；
2. 解析 latest release 的 `ffmpeg-release-essentials.zip`；
3. 下载 ZIP，并尽可能使用 gyan.dev 提供的 SHA-256 校验值验证文件；
4. 安全解压并复制到：

```text
程序目录/ffmpeg/bin/ffmpeg.exe
程序目录/ffmpeg/bin/ffprobe.exe
```

程序会在每次调用时重新解析 FFmpeg 路径，因此安装完成后不需要重启。

如果程序位于只读目录（例如受保护的 Program Files），自动安装会提示无法写入；可把程序移到可写目录或以管理员身份运行。
