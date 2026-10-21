# Mac 端接入说明

Windows 这台电脑已经装好 Deskflow Server。Mac 只需要安装 **Deskflow Client**，不需要安装增强控制台。

## 1. 下载并安装

先确认 Mac 芯片：

- Apple 芯片（M1 / M2 / M3 / M4 等）：安装 `deskflow-1.27.0-macos-arm64.dmg`
- Intel 芯片：安装 `deskflow-1.27.0-macos-x86_64.dmg`

打开 dmg 后，把 Deskflow 拖进“应用程序”。

如果 macOS 提示无法打开：

```bash
xattr -c /Applications/Deskflow.app
```

然后重新打开。

## 2. 连接到 Windows

在 Mac 的 Deskflow 中选择 **Client（客户端）**：

| 项目 | 填写内容 |
|---|---|
| 服务器地址 | Windows 当前的局域网 IPv4 地址 |
| 端口 | `24800` |
| 屏幕名 | `MacBook` |

如果界面里不能改屏幕名，编辑：

```text
~/Library/Deskflow/Deskflow.conf
```

把 `[core]` 中的 `computerName=` 改成：

```ini
computerName=MacBook
```

保存后重启 Deskflow。

## 3. 打开 macOS 权限

进入“系统设置 → 隐私与安全性”：

1. 在“辅助功能”中勾选 **Deskflow** 和 **deskflow**；
2. 在“本地网络”中允许 **Deskflow**；
3. 如果弹出控制电脑的提示，选择允许。

首次连接时如果出现“信任服务器”，点击信任。

## 4. 当前显示器接法

| 位置 | 显示器 | Windows 信号 | Mac 信号 |
|---|---|---|---|
| 左侧 | Windows 专用屏 `DISPLAY1` | HDMI 1 | 不连接 |
| 右侧 | 共享屏 `DISPLAY5` | HDMI 1 | HDMI 2 |

日常在 Windows 的增强控制台里：

- 点击 **切到 Windows**：右侧共享屏显示 Windows；
- 点击 **切到 Mac**：右侧共享屏显示 Mac；
- 切换通常需要 1–3 秒，这是显示器重新同步信号。

当前使用手动固定模式，不会根据鼠标位置自动切换。

## 5. 复制粘贴

Windows 端已开启 Deskflow 剪贴板同步，可以互相复制：

- 文字；
- HTML / 富文本；
- 图片。

用法：

- Windows 复制，Mac 粘贴：`Command + V`
- Mac 复制，Windows 粘贴：`Ctrl + V`

文件不能直接通过 Deskflow 剪贴板传输。

## 6. 传输文件

两台电脑都安装并打开 LocalSend，并连接到同一局域网。

Windows 端：

1. 打开“Deskflow 增强控制台”；
2. 点击 **打开 LocalSend**，或把文件拖到文件区；
3. 在 LocalSend 中选择 `MacBook`；
4. 点击发送。

Mac 端确认接收后，文件会保存到默认下载目录。

## 7. 连接不上时

按顺序检查：

1. Mac 的 Deskflow 是否使用 Client 模式；
2. 服务器地址是否为 Windows 当前的局域网 IPv4 地址，端口是否为 `24800`；
3. 屏幕名是否为 `MacBook`；
4. macOS 是否允许 Deskflow 的辅助功能和本地网络权限；
5. Windows 和 Mac 是否在同一局域网；
6. 文件传输时，LocalSend 是否已在两端打开。

Windows 增强控制台和本说明位于：

仓库中的 `dist/` 目录

