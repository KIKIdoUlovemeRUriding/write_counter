# 墨流 · InkFlow

[![Release](https://img.shields.io/github/v/release/KIKIdoUlovemeRUriding/write_counter?display_name=tag)](https://github.com/KIKIdoUlovemeRUriding/write_counter/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

> 写给写作者的 Windows 码字助手：安静的编辑器 + 一只常驻浮窗。

![墨流主界面与浮窗](docs/screenshots/hero-dark.png)

墨流把码字拆成四个数字：**码字时间 / 休息时间 / 本节字数 / 速率**，常驻在浮窗里；不接管你的写作软件，也不上传任何内容。

## 功能

- **浮窗**：四项实时指标、当前文章名（点击可改成固定标题）、今日目标、最近节奏；悬停显示最小化 / 最大化 / 关闭，最小化贴边成可拖动的小长条。
- **编辑器**：Markdown / TXT，自动保存，打开 / 另存 / 拖放，字体字号、专注模式；中文输入法上屏后才计数。
- **统计**：今日字数与时长、目标进度、近 14 天、连续天数；数据保存在本地。
- **Word / TXT**：Word 桌面版精确字数；记事本等纯文本编辑器字数追踪（VS Code 暂不支持）；其他软件可选按键估算（默认关闭）。
- **桌面**：托盘、单实例、全局快捷键、深浅主题、浮窗透明度 / 缩放 / 鼠标穿透。

## 下载

[GitHub Releases](https://github.com/KIKIdoUlovemeRUriding/write_counter/releases/latest)

- `InkFlow-selfcontained.exe`：自带 .NET 运行时，下载即用。
- `InkFlow.exe`：框架依赖版，需要 [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)。

Windows 10/11 x64。下载后双击运行。

## 从源码运行

```bash
git clone https://github.com/KIKIdoUlovemeRUriding/write_counter.git
cd write_counter
dotnet run --project src/InkFlow/InkFlow.csproj
```

需要 .NET 9 SDK。

## 常用快捷键

| 快捷键 | 作用 |
| --- | --- |
| `Ctrl + Alt + W` | 显示 / 隐藏浮窗 |
| `Ctrl + Alt + Space` | 暂停 / 继续计时 |
| `Ctrl + Alt + T` | 浮窗鼠标穿透 |
| `Ctrl + Alt + M` | 打开主界面 |
| `Ctrl + S` | 保存文稿 |

## 数据与隐私

- 无账号、无云端、无遥测；只在本机统计，不上传正文。
- 设置与统计：`%AppData%\InkFlow`；默认草稿：`%Documents%\墨流`。
- 可用 `INKFLOW_DATA_DIR` 自定义数据目录。

## 许可证

[MIT](LICENSE)
