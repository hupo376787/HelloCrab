# Kuaishou adapter

快手两个网页入口使用独立平台选项和解析器，避免接口结构互相干扰：

- `快手网页版`（`kuaishou`）：`KuaishouProfileFeedSiteAdapter`，主页 `https://www.kuaishou.com/profile/{profileId}`
- `快手网页版Live`（`kuaishou-live`）：`KuaishouSiteAdapter`，主页 `https://live.kuaishou.com/profile/{principalId}`
- 两个选项使用独立历史平台标识，以便重新采集时选回正确解析器；媒体文件和完成索引仍共用 `kuaishou` 目录，避免跨入口重复下载
- 主站作品接口：捕获 `GET /rest/v/profile/feed?...`
- Live 站作品接口：捕获 `GET /live_api/profile/public?...`
- 兼容旧接口：`visionProfilePhotoList` GraphQL 响应
- 主站 REST JSON：根节点 `pcursor`、`feeds[]`；作品位于 `feeds[].photo`，作者位于 `feeds[].author`
- Live REST JSON：作品位于 `data.list[]`，视频使用 `playUrl`，图集使用 `imgUrls[]`，封面使用 `poster`，作者位于 `author`
- `data.live` 只表示直播状态，普通作品采集不会下载直播流
- 下载内容：视频、图集、封面，以及响应中存在的音乐资源
- 安全隔离：两种 REST 响应都以本批作品中占多数的 `author.id` 锁定作者，并逐条过滤少数混入的其他作者作品
- 注意：主页 `profileId/principalId` 与响应中的 `author.id` 可能不是同一种 ID，不能直接相等校验
