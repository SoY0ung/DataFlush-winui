# DataFlush WinUI

DataFlush WinUI 是一个面向大规模人脸图像数据清洗的 Windows 原生风格桌面工具。项目使用 WinUI 3 构建，适合对 1 万到 10 万级图片数据集进行人工分类、AI 辅助筛选、帽子数据细化和模型阈值过滤。

## 主要功能

- 清洗分类：按 15 个人脸质量类别管理图片，包括正常、遮挡、角度、口罩、过亮、过暗、模糊、非人脸等。
- CSV 驱动分类：不移动真实图片文件，所有分类结果都通过 CSV 文件记录图片文件名。
- 多区块浏览：支持三个图片区块、多选、框选、Ctrl/Shift 选择、拖拽移动、撤销和预览窗口。
- 本地 ONNX 推理：使用 Microsoft.ML.OnnxRuntime 和 OpenCvSharp4 在本机执行批量推理。
- AI 结果缓存：推理结果保存到 CSV 目录下的 `AI_landmark` 文件夹，支持断点继续。
- 模型分类浏览：按 ONNX 模型 9 类结果浏览、统计和快捷移动。
- 阈值过滤：根据置信度阈值和预测正确性生成 train/valid 列表，用于辅助调整训练数据。
- 帽子分类：扩展细化帽子相关数据，将正常、部分遮挡、中间角度等类别进一步拆分为帽子子类。
- 设置页：配置数据集目录、CSV 目录、本地 ONNX 模型、自动推理、预处理方式、类别映射和操作提示。

## 分类体系

核心清洗分类包含 16 个 CSV：

- `00` 到 `14`：对应 15 个清洗类别。
- `15_unprocessed.csv`：未处理数据。

核心类别：

| 编号 | 类别 |
| --- | --- |
| 00 | 正常 |
| 01 | 部分遮挡 |
| 02 | 双眼遮挡 |
| 03 | 墨镜遮挡 |
| 04 | 帽子双眼 |
| 05 | 中间角度 |
| 06 | 大角度上 |
| 07 | 大角度下 |
| 08 | 大角度左 |
| 09 | 大角度右 |
| 10 | 戴口罩 |
| 11 | 过亮 |
| 12 | 过暗 |
| 13 | 模糊 |
| 14 | 非人脸 |
| 15 | 未处理数据 |

帽子分类启用后会额外创建：

- `h0` 帽子正常
- `h1` 帽子遮挡
- `h5` 帽子中间角度

## 数据原则

DataFlush WinUI 不移动真实图片文件。初始化 CSV 时会扫描数据集目录，将图片文件名写入未处理 CSV。后续分类、撤销、阈值过滤和帽子细化都只修改 CSV 内容。


## AI 与本地推理

当前推荐使用本地 ONNX 推理：

- Microsoft.ML.OnnxRuntime
- OpenCvSharp4
- OpenCvSharp4.runtime.win

本地推理在后台任务中运行，模型加载后会复用同一个 `InferenceSession`。预处理逻辑使用 OpenCV 风格读取和缩放，支持配置是否使用 ImageNet mean/std 标准化。

项目中仍保留 Dify Workflow API 相关代码，但公开仓库中不包含默认 API 地址和 API KEY。需要使用 API 时，请在应用设置页自行填写 Base URL 和 API KEY。

## 使用的 AI 编程工具

本项目由人工需求设计与 OpenAI Codex 协作开发。Codex 主要用于：

- WinUI 3 页面和交互实现
- CSV 数据逻辑与阈值过滤流程整理
- 本地 ONNX 推理服务封装
- UI 调整、问题排查和构建验证
- README 与工程说明维护

## 开发环境

建议环境：

- Windows 11
- .NET 10 SDK
- Windows App SDK 1.8
- Visual Studio 2026 或支持 .NET 10/WinUI 3 的开发环境

项目目标框架：

```xml
net10.0-windows10.0.26100.0
```

最低 Windows 目标平台：

```xml
10.0.17763.0
```

## 编译运行

还原依赖：

```powershell
dotnet restore
```

编译：

```powershell
dotnet build
```

运行：

```powershell
dotnet run
```

如果需要指定架构，可以使用：

```powershell
dotnet build -r win-x64
```

发布自包含目录：

```powershell
dotnet publish -c Release -r win-x64 --self-contained true
```

发布后可将输出目录打包为 zip，在普通用户权限的 Windows 11 电脑上运行。目标机器仍需要满足 Windows App SDK/WinUI 运行环境要求；如果使用非 MSIX 方式分发，请优先测试目标机器是否能正常启动。

