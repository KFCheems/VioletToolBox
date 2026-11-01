# 移植一致性报告（Linux 版 vs Windows 上游 v3.7）

本报告基于对上游源码的命令级审计（三路并行：欧加线刷引擎 / 基本刷入+EDL / root·模块·备份·下载），
并通过 **27 项命令级一致性测试**（`tests/VioletToolBox.ConsistencyTests/`）固化断言。
测试通过假设备脚本（`FakeDevice` + `ProcessRunner.Intercept` 记录拦截）比对
"我们发出的命令序列"与"上游实际命令序列"，无需真机。

运行方式：`cd linux/tests/VioletToolBox.ConsistencyTests && dotnet test`

---

## 1. 已验证一致的命令序列（测试固化）

| 领域 | 上游行为 | 移植状态 |
| --- | --- | --- |
| 设备检测 | `adb devices -l` / `fastboot devices` | ✅ 测试固化 |
| 基本刷入 | `fastboot -s S flash P "path"`（引号包裹路径） | ✅ 测试固化 |
| 分区擦除 | `fastboot -s S erase P`（无 flags） | ✅ 测试固化 |
| 槽位切换 | `set_active a\|b` | ✅ 测试固化 |
| 重启全家桶 | adb/fastboot `reboot [recovery/bootloader/fastbootd/edl]` | ✅ 测试固化 |
| getvar 单变量 | fastboot 结果在 stderr | ✅ 测试固化 |
| 设备信息 | `adb shell getprop` 解析 | ✅ 测试固化 |
| Root 探测 | `adb shell su -c id` → uid=0 | ✅ 测试固化 |
| su 命令风格 | `shell "su -c 'cmd'"`（单引号） | ✅ 已对齐 |
| 通讯录导出 | `content query --uri content://com.android.contacts/data/phones --projection display_name:data1` → PC 端拼 VCF → push/pull 往返 | ✅ 已按上游重写 |
| 模块安装 | 管理器三探针（ksud/apd/magisk.db）→ push → `su -c '<CLI> <path>'` → rm；Magisk 失败回退 `su -s` | ✅ 已对齐 |
| 成功/失败关键字 | success/done/installed/安装成功/inflating:…；permission denied/not found/… | ✅ 测试固化 |
| 欧加镜像排序 | **按文件大小升序**（无硬编码分区顺序表） | ✅ 已对齐 |
| 逻辑分区清单 | 上游 OujiaLogicalPartitionNames 18 项 | ✅ 测试固化 |
| 欧加收尾序列 | FastbootD 内 **统一擦 FRP** → 清数据（userdata→metadata→QC `-w`→misc）→ 自动重启 | ✅ 测试固化 |
| 强力线刷 | **不 erase 任何分区**；`delete-logical-partition {name}_{a,b}` + `create-logical-partition {name}_a 40` 重构 super | ✅ 测试固化 |
| 修复 FastbootD | 引导链双槽刷（`_a`→500ms→`_b`），收尾 `reboot fastboot`；**无清数据/FRP** | ✅ 测试固化 |
| ARB 熔断判定 | `arbIndex > 0` 即熔断（xbl_config OEM metadata +8 处 uint32） | ✅ 语义一致 |

## 2. 已修复的移植偏差（本次审计发现）

| # | 原偏差 | 上游正确行为 | 修复 |
| --- | --- | --- | --- |
| 1 | 通讯录用 awk/sed 管道在手机端拼 VCF | `content query` 输出由 **PC 端解析**拼 VCF，push→pull 往返 | 重写 `BackupService.BackupContactsAsync` |
| 2 | 模块安装盲目 `ksud \|\| magisk` 双命令 | 先三探针检测管理器，再用对应 CLI；Magisk 失败回退 `su -s` | 重写 `ModuleService.InstallModuleZipAsync` |
| 3 | su 命令用双引号嵌套 `su -c "cmd"` | `su -c 'cmd'` 单引号（部分 shell 对嵌套双引号解析不同） | 修 `AdbService.SuShellAsync` |
| 4 | 欧加分区按硬编码顺序表刷 | **按镜像文件大小升序** | 重写 `ScanImages` |
| 5 | 强力线刷前置 erase userdata/metadata/frp | **不 erase**；super 逻辑分区手术（delete/create-logical-partition 40） | 重写 `RunAsync` + `RebuildSuperAsync` |
| 6 | FRP 在各模式开头擦 | **收尾统一**在 FastbootD 内擦 | 重写收尾序列 |
| 7 | 修复 FastbootD 单槽刷 + vbmeta 带 disable-verity flags | **双槽刷**（_a→500ms→_b）9 分区集；vbmeta **无任何 flags** | 重写 RepairFastbootD 分支 |
| 8 | misc 当作普通分区 | misc 只在**清数据步骤**刷 `tools/tmp/misc.img` 现成文件 | 收尾序列实现 |
| 9 | 管理器探测在 push 之后 | 探针在 push **之前** | 调整顺序 |

## 3. 结构性差异（Linux 无等价物，已用替代实现并注明）

| 上游组件 | 上游行为 | Linux 替代 |
| --- | --- | --- |
| `fh_loader.exe` / `QSaharaServer.exe` | EDL 协议栈 + OPLUS 签名引导序列 | SharpEDL 纯托管 Sahara/Firehose（`/dev/ttyUSB*`） |
| `QSaharaServer.exe -p \\.\COMx -d` | 端口协议探测 | SharpEDS Sahara 握手 |
| WMI `Win32_PnPEntity` 枚举 9008 口 | 端口发现 | `/dev/ttyUSB*`/`ttyACM*` + `qcserial` 绑定 |
| `cmd.exe /c flash_all.bat` | 小米线刷 | 官方包自带 `flash_all.sh`，bash 原生执行 |
| `explorer.exe /select` | 打开目录 | Linux 文件管理器（未实现打开目录，功能可选） |
| `mmc.exe devmgmt.msc` | 设备管理器 | udev/lsusb（环境检查页覆盖诊断需求） |

## 4. 已知未对齐项（诚实清单）

| 项 | 上游细节 | 现状 |
| --- | --- | --- |
| ARB 读取方式 | 上游经 **adb + root 回读 xbl_config** 解析 ELF（不用 getvar） | 我们用 `getvar anti/arbid` 近似；**判定阈值一致**（>0 熔断） |
| 售后包 FB 的 super 合并 | 无 `violet` 标记时 SuperMaker+lpmake 合并散包→`erase super`→`flash super` | SuperMaker 已移植；FB 模式直刷 super 序列待补 `violet` 标记文件逻辑 |
| 自动救砖 | 30 机型清单 → ROM 专区 → aria2 → 7z → 自动 FB 流程 | 需 RomDownload 完整移植后串联（部分依赖 NDM.exe 下载器为可选） |
| fastboot 进度解析 | `Sending 'x' (SIZE UNIT)` 块基准 + `cur` 行累计 | 我们按分区计数（百分比语义等价，字节级进度待补） |
| COW 分区清理 | 刷前 `delete-logical-partition` 清 cow | 待补 |
| 隐藏ROOT完整流程 | 四方案（MagiskAlpha/SukiSU 两代）+ 模块包前缀解析 + HMA 自动启动 | 仅有 denylist 基础命令；完整流程涉及大量专用模块包，待移植 |
| 降级助手 socket 协议 | GhostPID 帧 + ECDH + AES-CTR（8500→9527） | DowngradeTool 引擎已移植（含该协议）；**命令级测试未覆盖**（需协议 mock） |
| 设备等待语义 | 120s 倒计时 + 序列号绑定不改绑 + 30s/3 连续稳定探测 | 简化为 4s 固定等待；**行为差异会在慢速 USB 场景暴露** |

## 5. 测试清单（27 项）

- `BasicCommandParityTests`（12）：设备检测×2、flash/erase/set_active/reboot×8、getvar×2、getprop、root
- `ModuleAndBackupParityTests`（4）：模块安装全链路、成功/失败关键字集、通讯录 content query
- `OujiaFlashParityTests`（4）：大小升序、payload/gpt 跳过、逻辑分区清单、模式表
- `OujiaFlashSequenceTests`（3）：普通线刷完整收尾序列、强力线刷无前置擦除+super 手术、修复 FBD 双槽

## 6. 维护约定

- 上游更新后重跑审计 → 更新本报告 §1/§4 → 将新行为固化为测试 → 修引擎对齐。
- 任何引擎改动必须先加/改一致性测试再改实现（测试即上游行为的可执行规格）。
