# 美篇网页适配

支持作者专栏地址：

```text
https://www.meipian.cn/c/{userid}
```

采集流程：

1. 刷新作者专栏，解析 `div.content div.articlecontent` 中的首批文章；
2. 滚动到页面底部，捕获 `POST /static/action/load_columns_article.php?userid={userid}` 返回的分页 JSON；
3. 根据每条记录的 `mask_id` 请求 `https://www.meipian.cn/{mask_id}`；
4. 从详情文档的 `var ARTICLE_DETAIL = {...}` 中解析作者、标题、发布时间、封面、背景音乐和图集原图。

分页接口以最后一条文章数字 ID 作为 `maxid`，这个参数由美篇页面自身的滚动脚本维护，适配器只负责触发页面滚动并捕获响应。
