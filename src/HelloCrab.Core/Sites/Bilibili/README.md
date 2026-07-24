# 哔哩哔哩适配器

- 作者页格式：`https://space.bilibili.com/{mid}/upload/video`
- 列表接口：`/x/space/wbi/arc/search`
- 作者资料接口：`/x/space/wbi/acc/info`，从 `data.name` 和 `data.face` 获取作者名与头像
- 翻页方式：处理完当前页后，自动滚动到底部并点击“下一页”
- 详情解析：根据 `bvid` 请求 `https://www.bilibili.com/video/{bvid}/`
- 媒体来源：`window.__playinfo__.data.dash`
- 画质策略：先比较像素总数，再比较帧率、清晰度 ID 和带宽
- 音频策略：选择 `dash.audio` 中带宽最高的音频流
- 文件处理：视频流与音频流分别下载到临时文件，通过 FFmpeg 合并为最终视频；临时音频会自动删除，勾选“下载背景音乐”时才额外保留音频文件
- 历史兼容：作品已下载并跳过时，也会用最新 `data.face` 回写 `History.json`
