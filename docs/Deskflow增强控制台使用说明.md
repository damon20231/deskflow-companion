# Deskflow 一键连接 使用说明

适用环境：Windows 主机 `Z790UD` + Mac `MacBook`。  
当前版本：2026-10-21  
程序：`dist\Deskflow增强控制台.exe`

## 1. 默认页面只有三件事

打开后会看到三个编号大卡片，按 1 → 2 → 3 使用即可：

1. **键鼠连接**：确认 Deskflow 是否正常。
2. **显示器**：选择共享显示器，并一键切到 Windows 或 Mac。
3. **文件传输**：打开 LocalSend，把文件拖进去发送到 Mac。

右上角有“高级设置”。日常使用不需要打开，DDC/CI、自动切换、诊断和剪贴板测试都收在里面。

## 2. 当前显示器和输入

| 显示器 | 信号 | 用途 |
|---|---|---|
| 左侧 `S2722QC / DISPLAY1` | HDMI 1，`0x11` | Windows |
| 右侧 `S2722QC / DISPLAY5` | HDMI 2，`0x12` | Mac，同时作为共享显示器 |

当前是**手动固定模式**：

- 不自动换源；
- 点击“切到 Mac”，右侧共享显示器使用 HDMI 2；
- 点击“切到 Windows”，右侧共享显示器使用 HDMI 1；
- 左侧 Windows 显示器保持不变。

如果以后换了显示器或设备标识变化，点击“选择共享显示器”，选中实际同时连接 Windows 和 Mac 的那一台。

## 3. 日常操作

### 键鼠

主页面显示“键鼠连接正常”时即可使用：

- Windows 键鼠跨到 Mac 后，由 Deskflow 继续控制 Mac；
- 从 Mac 跨回 Windows 后，键鼠回到 Windows；
- 不需要在 Mac 上安装增强控制台，Mac 只安装 Deskflow Client。

如果显示“键鼠连接未就绪”，点击“检查键鼠连接”，按提示打开 Deskflow。

### 切换显示器

在“显示器”卡片中：

- `切到 Windows`：共享显示器切换到 Windows 输入；
- `切到 Mac`：共享显示器切换到 Mac 输入；
- `选择共享显示器`：更换要控制的显示器。

显示器换源通常需要 1–3 秒，这是显示器硬件重新同步画面，不是软件卡顿。

### 复制粘贴

“剪贴板同步已开启”表示 Deskflow 已启用文字、HTML 和图片剪贴板同步：

- Windows 复制，Mac 粘贴：`Command + V`
- Mac 复制，Windows 粘贴：`Ctrl + V`

文件本身不能直接通过 Deskflow 剪贴板传输，请使用 LocalSend。

### 传输文件

Windows 已安装 LocalSend：

- 点击“打开 LocalSend”；或
- 把文件拖到“把文件拖到这里”区域，程序会打开 LocalSend。

然后在 LocalSend 中选择 `MacBook`，点击发送，再在 Mac 端确认接收。Mac 也必须安装 LocalSend，并与 Windows 处于同一局域网。

## 4. 高级设置

只有排查问题时才需要进入，包含：

- 共享显示器扫描和输入映射；
- 自动切换和边缘切换；
- 10 秒测试切换；
- 剪贴板测试；
- Deskflow 日志和诊断信息；
- 开机启动。

当前配置为 `autoSwitch=False`、`edgeSwitch=False`，即固定手动切换。不要打开自动切换，除非确实希望鼠标边缘触发显示器换源。

## 5. 关闭和退出

窗口右上角关闭按钮会把控制台最小化到系统托盘，程序继续工作。  
要完全退出，右键托盘图标，选择“退出”。

## 6. 常见问题

### 按钮切换失败

1. 确认“共享显示器”选中的是同时连接两台电脑的那台屏幕；
2. 在显示器 OSD 中开启 DDC/CI；
3. 进入高级设置，点击“测试此显示器（10 秒后恢复）”；
4. 某些显示器在未选中输入时不响应 DDC/CI，此时可在显示器按钮上手动换源。

### 能控制 Mac，但剪贴板不能粘贴

检查 Windows 的 `Deskflow.conf` 中是否有：

```ini
[server]
enableClipboard=true
```

当前 Windows 已开启此项。若仍不可用，重启 Deskflow 后再试。

### LocalSend 找不到 Mac

确认：

- Mac 已安装并打开 LocalSend；
- 两台电脑在同一局域网；
- macOS 的“本地网络”权限已允许 LocalSend。


