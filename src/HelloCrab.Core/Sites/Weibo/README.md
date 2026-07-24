# 微博作者主页解析

`WeiboSiteAdapter` 监听作者主页滚动时产生的：

```text
https://weibo.com/ajax/statuses/mymblog?uid={UID}&page={page}&feature=0
```

解析规则：

- 作者：`data.list[].user.idstr / screen_name / avatar_hd`
- 作品 ID：`data.list[].idstr`
- 标题：优先 `text_raw`
- 时间：`created_at`
- 图集顺序：`pic_ids[]`
- 原图地址：`pic_infos[picId].largest.url`
- 分页标识：`data.since_id`

仅解析当前微博自身的 `pic_infos`，不会递归下载 `retweeted_status` 中其他作者的媒体。
