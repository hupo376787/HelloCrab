人像检测模型放置目录

请将使用 COCO 类别的目标检测 ONNX 模型放入本目录。程序不会自动下载模型。

支持以下命名：
1. person-detection.onnx（优先使用）
2. yolo11?.onnx，其中 ? 表示 yolo11 后可没有字母，或带任意一个字母
   例如：yolo11.onnx、yolo11n.onnx、yolo11m.onnx

不支持 yolo11n-seg.onnx、yolo11n-pose.onnx、yolo11n-cls.onnx 等非普通目标检测模型。

开启人像检测后，图片先以“原文件名.扩展名.pending”保存并进入后台检测队列：
- 检测到人物：恢复正式文件名；
- 未检测到人物：删除图片；
- 模型缺失、解码或推理失败：恢复正式文件名并保留图片。

未开启人像检测时不会生成 .pending 文件。
