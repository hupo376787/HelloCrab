# 小红书作者主页解析

同一个 `XiaohongshuSiteAdapter` 处理三段数据：

1. `/user/profile/{userId}` 文档中的 `window.__INITIAL_STATE__` 首屏列表；
2. `edith.xiaohongshu.com/api/sns/web/v1/user_posted` 后续分页；
3. 根据每条 `noteId + xsecToken` 构造 `/explore/{noteId}`，解析详情文档中的真实视频或图集地址。

平台 ID 固定为 `xiaohongshu`，历史与下载目录不会按接口拆分。
