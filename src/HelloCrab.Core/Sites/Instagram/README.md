# Instagram 适配器

## 捕获接口

作者主页滚动时捕获：

```text
https://www.instagram.com/graphql/query
```

只解析响应中的：

```text
data.xdt_api__v1__feed__user_timeline_graphql_connection
```

`/graphql/query` 也会返回通知和弹窗数据，缺少上述根节点的响应会被静默忽略。

## 媒体类型

优先读取 `node.media_type`：

```text
1 = 单图
2 = 视频 / Reels
8 = 轮播图集
```

同时使用实际字段兜底：

- `video_versions` 非空：视频；
- `carousel_media` 非空：轮播图集；
- `image_versions2.candidates`：同一张图片或视频封面的多种尺寸，不是图集。

轮播图集可能混合图片和视频，下载层按 `carousel_media` 原顺序保存为 `_01`、`_02`……。

## 清晰度选择

`video_versions.type` 是 Instagram 内部 rendition 标识，并没有稳定公开的质量等级语义。样例中的
`101`、`102`、`103` 可能拥有完全相同的宽高和 URL，因此适配器不按 `type` 排序，而是：

1. 按 `width × height` 从大到小排序；
2. 去除重复 URL；
3. 最大像素地址优先，其余不同地址作为下载失败回退。

图片候选同样按像素面积选择。

## 文件名

优先使用 `caption.text`，其次尝试 `title`、`headline`。三者都为空时只使用发布时间：

```text
2026-07-20 14-14-16.mp4
2026-07-20 14-14-16_01.jpg
```
