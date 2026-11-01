# 紫罗兰工具箱 · Linux 移植版（全量移植）

基于 [Smart-Paocai/VioletToolBox](https://github.com/Smart-Paocai/VioletToolBox)（`main` 分支 v3.7）的 **Linux 全量移植**。目标环境为 Fedora 44（KDE Plasma）/ x86_64，兼容主流 X11/Wayland 桌面。

UI 使用 **Avalonia 12** 重写（WPF 无法运行于 Linux），全部业务引擎复用/移植上游纯 C# 代码，外部 Windows 二进制全部替换为 Linux 原生等价物（随包捆绑或系统包提供）。

## 构建

```bash
cd linux
./build-linux.sh                    # 需要 .NET 10 SDK；产物在 dist/VioletToolBox
./build-linux.sh --self-contained   # 自带运行时，无需安装 .NET
```

运行：

```bash
./dist/VioletToolBox/VioletToolBox              # 主界面
./dist/VioletToolBox/VioletToolBox --page EDL   # 直达页面（支持全部 16 页名称）
```

## 系统依赖

```bash
sudo dnf install android-tools scrcpy aria2 p7zip unzip python3 bash
```

工具解析顺序：`$VIOLET_TOOLBOX_TOOLS` → `tools/`（程序目录旁）→ `~/.local/share/VioletToolBox/tools` → 系统 `PATH`。「关于」页内置环境检查。

### 随包捆绑的 Linux 工具（`tools/`，构建时自动拷入）

| 工具 | 来源 | 用途 |
| --- | --- | --- |
| `magiskboot` | Magisk v31.1（libmagiskboot.so，静态 ELF） | boot 解包/重打包、卡刷转线刷 |
| `kptools` + `kpimg-android` | KernelPatch 0.13.9 官方 Linux 版 | APatch 脱机修补 |
| `ksud-arm64` / `ksud-x86_64` | KernelSU v3.3.1（设备端静态） | LKM / KSU 模块链 |
| `lpmake` | lpunpack-lpmake-mirror（静态） | super 制作 / EDL 动态 super |
| `ocdt_tool.py` | 上游原版 | OCDT 分析（python3） |

Windows 版的 `fh_loader.exe` / `QSaharaServer.exe`（高通 EDL）**无 Linux 版**，由纯托管的 [SharpEDL](https://github.com/Asgore-Dreemur/SharpEDL) 完整替代（Sahara + Firehose 走 `/dev/ttyUSB*`）。

## 功能对照（16 页全量）

| 页面 | 上游功能 | Linux 版状态 |
| --- | --- | --- |
| 设备 | 设备识别/信息、重启全家桶（系统/Recovery/Bootloader/FastbootD/Sideload/EDL）、槽位切换、环境检查 | ✅ |
| 投屏 | scrcpy 投屏：清晰度/码率/帧率/息屏/保持唤醒/显示触控 | ✅（scrcpy 官方 Linux 版；上游魔改的虚拟按键条为 Windows 专属，桌面快捷键投屏窗口内可用） |
| 基本刷入（刷入页） | boot/init_boot/dtbo/vendor_boot/vbmeta 刷写、vbmeta 关校验、擦除分区、自定义分区名 | ✅ |
| 小米官方线刷（欧加页） | .tgz 解包 + flash_all 脚本族执行 | ✅ 原生跑官方 Linux 脚本（bash） |
| 可视刷写（分区页） | ADB(root)/Fastboot 双模式分区表、读取→文件、写入镜像、GPT 解析 | ✅ |
| 欧加线刷 | 普通线刷、强力线刷、AB 通刷、仅 FastbootD、修复 FastbootD、ARB 熔断检测、售后 FB/FBD（XML 散表） | ✅（引擎按 oujiaflash.cs 序列重写） |
| EDL 刷写 | Sahara/Firehose 连接、设备信息、分区表、读/写/擦、重启、备份基带指纹/GPT、恢复出厂、清 FRP、格式化 LUN、动态 super | ✅（SharpEDL，实验性） |
| 降级助手 | ColorOS 助手 APK 安装、降级包查询/下载/推送 | ✅（DowngradeTool 原码移植） |
| 模块专区 | Magisk/KernelSU/SukiSU/APatch 管理器快捷安装（GitHub 拉最新）、模块 zip 安装（ksud/magisk CLI）、隐藏 ROOT | ✅ |
| 应用与文件 | 第三方应用列表/冻结/解冻/卸载/装 APK、文件推送/拉取/删除 | ✅ |
| 脱机修补 | magiskboot 重打包、APatch kptools+kpimg 内核修补（SuperKey）、LKM insmod | ✅（Linux 原生 magiskboot/kptools） |
| 安卓通用 | 卡刷转线刷（zip→解包+payload 展开+flash 脚本）、TWRP 临时引导、OCDT 分析、修复 ADB、无线调试连接 | ✅ |
| Payload（解包页） | payload.bin/zip/URL 流式解包、清单勾选 | ✅ |
| 欧加固件（解包页） | .ofp 提取、.ops 提取、分段 super 合并、lpmake 制作 super | ✅ |
| 备份助手 | 照片/视频备份、通讯录 VCF 导出 | ✅ |
| 下载专区 | 联想查包（Y700 二/三/四/五代预设）、violettool Rom 获取、页面直链抓取 + aria2 下载 | ✅ |
| 断点续传（下载页） | aria2 多连接下载、进度/速度/ETA | ✅ |
| 关于 | 版本/许可 + 环境检查明细 | ✅ |

**未移植**（上游依赖 Windows 专属二进制且无 Linux 等价物，已在对应页面注明）：
- 魔改 scrcpy 的屏上虚拟按键条（可用 scrcpy 快捷键替代）
- KernelPatch/LKM 的 Windows 注入器（Linux 用 kptools + 设备端 ksud 已覆盖主流程）
- `NDM.exe`/`Neko.exe`（联想救砖专用 Win 工具）：其流程中的 fastboot/firehose 部分已由欧加线刷/EDL 页覆盖

## 目录结构

```
linux/
├── src/VioletToolBox.Core/      跨平台引擎（net10.0，无 UI 依赖）
│   ├── Android/                 Adb/Fastboot/分区/GPT/应用管理/Scrcpy
│   ├── Flash/                   欧加线刷、小米线刷、ADB 工具箱
│   ├── Root/                    脱机修补（magiskboot/kptools/LKM）、模块专区
│   ├── Extraction/              OFP/OPS 提取、Super 合并/制作（上游原码）
│   ├── Payload/                 payload.bin 解包引擎（上游原码）
│   ├── Backup/                  备份助手
│   ├── Download/                aria2 下载、联想查包、Rom 获取（上游原码）
│   ├── Edl/                     SharpEDL 会话 + 全操作
│   ├── DowngradeTool.cs         降级助手（上游原码）
│   ├── ToolLocator.cs           工具解析
│   └── ProcessRunner.cs         统一进程封装
├── src/VioletToolBox.Linux/     Avalonia 12 界面（16 页）
│   └── Assets/tools/            magiskboot / kptools / kpimg / ksud / lpmake
├── packaging/                   .desktop
└── build-linux.sh
```

## 许可

继承上游 [GPL-3.0](../LICENSE)。刷机有风险，操作前请自行备份。
