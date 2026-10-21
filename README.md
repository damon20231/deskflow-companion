# Deskflow 增强控制台

一个面向 Windows + Mac 双机桌面的简单控制台，用于：

1. 查看 Deskflow 键鼠连接状态；
2. 在共享显示器上切换 Windows / Mac 输入；
3. 通过 LocalSend 发送文件。

![界面预览](assets/preview.png)

## 当前工作方式

- Windows 主机运行 Deskflow Server 和本控制台；
- Mac 只需安装 Deskflow Client；
- 键鼠通过 Deskflow 在两台电脑间共享；
- 共享显示器通过 DDC/CI 手动切换输入源；
- 文字、HTML 和图片使用 Deskflow 剪贴板同步；
- 文件使用 LocalSend 传输。

当前默认是手动固定模式，不会根据鼠标位置自动切换显示器。

## 快速使用

运行：

```text
dist/Deskflow增强控制台.exe
```

主页只有三个编号区域：

1. **键鼠连接**：显示连接和剪贴板状态；
2. **显示器**：切到 Windows、切到 Mac，或重新选择共享显示器；
3. **文件传输**：打开 LocalSend，或把文件拖入窗口。

详细说明：

- [Windows 使用说明](docs/Deskflow增强控制台使用说明.md)
- [Mac 接入说明](docs/Mac端接入说明.md)

## 下载

仓库的 `dist` 目录包含：

- `Deskflow增强控制台.exe`
- `Deskflow增强控制台-v1.0-win-x64.zip`

Mac 端 Deskflow 安装包请从 Deskflow 官方发布页下载。本仓库不包含 Deskflow 本身的源代码或安装包。

## 从源码编译

在 Windows PowerShell 中运行：

```powershell
.\build.ps1
```

项目使用系统自带的 .NET Framework 4.x C# 编译器，不需要额外安装 .NET SDK。

## 技术说明

- 程序使用 Windows DDC/CI 控制显示器输入源；
- 显示器必须在 OSD 中启用 DDC/CI；
- 某些显示器在未选中某个输入时可能不接受该输入的 DDC/CI 命令；
- 文件传输没有绕开 LocalSend，Deskflow 本身不直接传文件。

## 目录结构

```text
src/       C# 源码
dist/      可执行程序和发布压缩包
docs/      使用说明
assets/    截图
build.ps1  本地编译脚本
```
