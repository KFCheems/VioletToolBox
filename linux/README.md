# 紫罗兰工具箱 · Linux 移植版

基于 [Smart-Paocai/VioletToolBox](https://github.com/Smart-Paocai/VioletToolBox)（`main` 分支 v3.7）的 **Linux 原生移植**，目标环境为 Fedora 44（KDE Plasma）/ x86_64，兼容主流 X11/Wayland 桌面。

UI 使用 **Avalonia 12** 重写（WPF 无法运行于 Linux），业务引擎直接复用上游纯 C# 代码。

## 构建

```bash
cd linux
./build-linux.sh                    # 需要 .NET 10 SDK；产物在 dist/VioletToolBox
./build-linux.sh --self-contained   # 自带运行时，无需安装 .NET
```

运行：

```bash
./dist/VioletToolBox/VioletToolBox            # 主界面
./dist/VioletToolBox/VioletToolBox --page EDL # 直达指定页面（设备/刷入/分区/解包/EDL/下载）
```

## 系统依赖

```bash
sudo dnf install android-tools scrcpy aria2 p7zip
```

- `adb` / `fastboot`：设备识别、刷入、可视分区读写（必备）
- `scrcpy`：投屏基础能力
- `aria2`：固件多连接下载
- `lpmake`（AOSP super image 工具）：「制作 super.img」功能需要，放入 `tools/` 目录或加入 PATH 即可

工具解析顺序：`$VIOLET_TOOLBOX_TOOLS` → `tools/`（程序目录旁）→ `~/.local/share/VioletToolBox/tools` → 系统 `PATH`。「设备」页内置环境检查，可逐项查看工具可用性。

## 功能对照（相对 Windows 版）

| 页面 | 功能 | 状态 |
| --- | --- | --- |
| 设备 | ADB/Fastboot 设备识别、设备信息、重启（系统/Recovery/Bootloader/FastbootD/Sideload/EDL）、槽位切换、工具环境检查 | ✅ |
| 刷入 | boot / init_boot / dtbo / vendor_boot / vbmeta 等镜像刷写（fastboot flash 或 root dd）、vbmeta 关闭校验、分区擦除 | ✅ |
| 分区 | 可视分区表（ADB root GPT 解析 / fastboot getvar）、分区读取→文件、写入镜像、进度显示 | ✅ |
| 解包 | payload.bin（本地 / zip / **URL 流式**）解析清单与按分区解出、.ofp 提取、.ops 提取、分段 super 合并、lpmake 制作 super | ✅ |
| EDL | 高通 9008：基于 **SharpEDL 纯托管实现**（Sahara + Firehose），无需 fh_loader.exe / QSaharaServer.exe；读设备信息、分区表、读/写/擦分区、重启 | ✅（实验性） |
| 下载 | aria2 多连接下载 ROM/OTA/降级包，进度与速度显示 | ✅ |
| — | scrcpy 二次开发投屏（虚拟按键/自定义标题）、小米官方线刷、KernelPatch/LKM 等注入类工具、售后包全自动救砖 | ⏳ 未移植 |

### 与 Windows 版的关键差异

- **EDL 不再依赖 Windows PE 工具**：Windows 版通过 `fh_loader.exe` / `QSaharaServer.exe` 通信；Linux 版使用纯托管的 [SharpEDL](https://github.com/Asgore-Dreemur/SharpEDL)，串口直接使用 `/dev/ttyUSB*`（需要 `qcserial` 内核模块绑定 9008 设备）。
- **ADB 分区读写需要 root**：读取走 `su -c dd` 流式回传；与 Windows 版逻辑一致，但 Linux 下 adb 设备节点权限由 udev 决定，必要时安装 `android-tools` 并配置 udev 规则。
- 未移植项均依赖 Windows 专属组件（修改版 scrcpy、NDM、KernelPatch 注入器等），详见上表。

## 目录结构

```
linux/
├── src/VioletToolBox.Core/      跨平台引擎（net10.0，无 UI 依赖）
│   ├── Android/                 AdbService / FastbootService / PartitionService / GptParser
│   ├── Extraction/              OFP / OPS 提取、Super 合并 / 制作（复用上游代码）
│   ├── Payload/                 payload.bin 解包引擎（复用上游 Payload_Dumper_C#）
│   ├── Download/                aria2c 下载服务（复用上游代码）
│   ├── Edl/                     SharpEDL 会话封装（Linux 串口）
│   ├── ToolLocator.cs           外部工具解析（tools/ 目录、PATH）
│   └── ProcessRunner.cs         统一进程封装（超时/取消/日志）
├── src/VioletToolBox.Linux/     Avalonia 12 界面（net10.0）
├── packaging/                   .desktop 文件
└── build-linux.sh               构建脚本
```

## 许可

继承上游 [GPL-3.0](../LICENSE) 许可证。本移植版仅修改与新增 Linux 相关代码，上游 WPF 源码未做改动。
