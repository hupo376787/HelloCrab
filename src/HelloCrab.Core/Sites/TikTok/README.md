# TikTok Web adapter

- 监听页面作者主页的 `/api/post/item_list/` Fetch/XHR。
- 使用响应中的 `hasMore` 与 `cursor` 判断分页。
- 视频从 `itemList[].video.bitrateInfo[].PlayAddr` 选择实际像素面积最大的档位，码率只作为同分辨率排序条件。
- 作者头像使用 `author.avatarLarger`，并回退到 Medium/Thumb。
- 页面滚动会同时触发 DOM 滚动、真实 wheel 和 End 键，适配 TikTok 的懒加载。
