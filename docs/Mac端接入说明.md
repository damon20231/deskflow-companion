# Mac 端接入说明

Windows 这台电脑已经装好 Deskflow Server 和增强控制台。Mac 只需要：

- 安装 **Deskflow Client**，用于键鼠共享；
- 使用 Safari 或 Chrome 打开 Windows 控制台提供的局域网地址，用于文件传输和文字剪贴板桥接。

Mac 不需要安装增强控制台，也不需要额外安装文件传输程序。

## 1. 下载并安装 Deskflow Client

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

## 5. 文件传输

确认 Mac 和 Windows 连接同一个可信局域网，并且 Windows 控制台正在运行。

### 在 Mac 上打开传输页

1. 在 Windows 控制台第 3 区点击 **复制 Mac 地址**；
2. 回到 Mac，在 Safari 或 Chrome 地址栏粘贴并打开；
3. 地址完整包含 `http://`、端口和随机令牌，不要只复制 IP。

如果 Mac 当前显示的是 Windows 画面，需要先点击 **切到 Mac**，再在 Mac 上操作浏览器。

### 下载 Windows 文件

1. Windows 端把文件拖到控制台第 3 区，或点击 **添加文件**；
2. Mac 浏览器刷新“从 Windows 下载”列表；
3. 点击文件右侧的 **下载**。

### 把 Mac 文件发送到 Windows

1. 在 Mac 浏览器找到“发送文件到 Windows”；
2. 把文件拖入虚线区域，或点击选择文件；
3. 文件会保存到 Windows 的：

```text
下载\Deskflow Companion
```

4. Windows 端可在控制台点击 **打开接收文件夹**。

## 6. 复制粘贴

Deskflow 正常时会自动同步：

- 文字；
- HTML / 富文本；
- 图片。

用法：

- Windows 复制，Mac 粘贴：`Command + V`
- Mac 复制，Windows 粘贴：`Ctrl + V`

如果自动同步没有生效，可以在 Mac 浏览器传输页使用“文字剪贴板”区域：

- **取 Windows 剪贴板**：读取 Windows 当前文字；
- **复制到 Mac 剪贴板**：把文本框内容写入 Mac 剪贴板；
- **发送到 Windows 剪贴板**：把文本框内容写入 Windows 剪贴板。

浏览器可能因 HTTP 局域网页面限制而不能自动写入系统剪贴板。此时页面会选中文字，按 `Command + C` 即可。

文字桥接只处理纯文字；HTML、图片和文件不走这个文本框。

## 7. 连接不上时

### Deskflow 键鼠连接不上

按顺序检查：

1. Mac 的 Deskflow 是否使用 Client 模式；
2. 服务器地址是否为 Windows 当前的局域网 IPv4 地址，端口是否为 `24800`；
3. 屏幕名是否为 `MacBook`；
4. macOS 是否允许 Deskflow 的辅助功能和本地网络权限；
5. Windows 和 Mac 是否在同一局域网。

### Mac 打不开文件传输页

按顺序检查：

1. Windows 控制台第 3 区是否显示传输服务已开启；
2. 地址是否完整复制，尤其是末尾的随机令牌；
3. Mac 和 Windows 是否在同一局域网；
4. Windows 防火墙是否允许程序访问“专用网络”；
5. Windows 当前 Wi-Fi / 网线网络是否被识别为“专用网络”；
6. 在 Windows 高级设置中复制诊断信息，确认当前使用的局域网 IP 是否正确。

请只在可信局域网中使用传输页，不要把包含随机令牌的完整地址发布到公网。

Windows 增强控制台和本说明位于仓库的 `dist/` 和 `docs/` 目录。
