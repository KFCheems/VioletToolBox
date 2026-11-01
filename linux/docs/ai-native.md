# AI-Native 接口规范（vtb CLI / MCP / NL 路由）

紫罗兰工具箱 Linux 版的原生 AI 面。设计原则：**JSON in / JSON out、单命令单职责、危险操作显式标记、stdout 纯净（人类诊断走 stderr）**——任何 AI 代理（Claude Desktop、ZCode、shell 循环里的 agent）都能直接驱动整个工具箱。

三层接口共用一个 `CommandRegistry`（唯一事实源）：

```
CommandRegistry ──┬── vtb CLI（shell 调用，AI 代理最简路径）
                  ├── MCP 服务器（JSON-RPC over stdio，AI 助手原生工具）
                  └── NL 路由（自然语言 → 命令，确定性关键词匹配）
                       ↑ 底层全部复用与 GUI 相同的引擎（一致性由 36 项命令级测试保障）
```

## 1. `vtb` CLI —— 代理的 shell 接口

```bash
export DOTNET_ROOT=~/.dotnet   # 用户级运行时需要
vtb --list                     # 全部命令 + 参数 schema（JSON）
vtb --schema flash.partition   # 单命令 schema
vtb --tools                    # MCP tools/list 格式（供 AI 预注入上下文）
vtb --raw device.list          # 紧凑 JSON（管道场景）
vtb device.info                # 执行 → JSON envelope
vtb flash.partition --partition boot --image /path/boot.img
vtb --mcp                      # 作为 MCP 服务器运行（stdio）
vtb "检测设备"                  # 自然语言路由
```

**JSON envelope（snake_case，任何语言可解析）**：

```json
{
  "ok": true,
  "command": "device.list",
  "data": { "adb": [...], "fastboot": [...] },
  "error": null,
  "log": ["…逐行执行日志…"]
}
```

**退出码契约**：`0` 成功 · `1` 执行失败（读 `error`）· `2` 未知命令 · `3` 意图路由不完整（读 `data.candidates`）

### 命令目录（17 个，按类目）

| 类目 | 命令 | 说明 |
| --- | --- | --- |
| device | `device.list` / `device.info` / `device.reboot` | 设备检测、属性摘要、重启（edl 标记危险） |
| flash | `flash.partition` / `flash.repair-fastbootd` | 刷分区（fastboot 优先 root dd 回退）、修复 FBD 双槽 |
| partition | `partition.list` / `partition.read` | 分区表（adb/fastboot 双通道）、分区回读 |
| payload | `payload.analyze` / `payload.extract` / `extract.ofp` / `extract.ops` | 清单解析（本地/URL）、解出分区、欧加固件 |
| edl | `edl.ports` / `edl.info` / `edl.partitions` | 9008 串口、Sahara 身份、全 LUN 分区表 |
| module | `module.detect` / `module.install` | 管理器探针、模块安装 |
| env | `tools.check` | 工具链可用性（缺失清单） |

## 2. MCP 服务器

```jsonc
// 注册到任意 MCP 客户端（stdio transport）:
{ "mcpServers": { "violettoolbox": { "command": "/path/to/vtb", "args": ["--mcp"] } } }
```

协议：JSON-RPC 2.0 / MCP `2024-11-05`。`tools/list` 返回 17 个 `vtb_*` 工具（camelCase 参数、含 enum/default/description 的 JSON Schema）；`tools/call` 返回标准 content blocks（text = envelope JSON，`isError` 同步 ok）。危险工具在描述中带 `[危险操作]` 标记，代理应向用户确认后调用。

## 3. NL 路由（确定性，无运行时 LLM 依赖）

关键词加权匹配 + 浅参数抽取（路径/URL/枚举值/分区名）。**安全语义**：
- 请求完全确定（必填参数齐）→ 直接执行
- 不确定 → 返回候选命令 + 缺参数清单（exit 3），**绝不猜着执行**

```bash
$ vtb "我想刷入boot"
→ candidates: flash.partition … 待补参数: 缺少必需参数 --image
$ vtb "解析payload /tmp/ota.zip"
→ 直接执行 payload.analyze --source /tmp/ota.zip
```

## 4. AI 代理集成示例

Claude / ZCode 会话（MCP）：

```
用户: 帮我把这台一加的 boot 备份出来
AI:   tools/call vtb_device_list     → serial=PJD110
      tools/call vtb_partition_read  { partition: "boot", out: "~/backup/boot.img", serial: "PJD110" }
      → data.bytes = 67108864  ✓
```

Shell 代理（CLI 组合流）：

```bash
vtb --raw payload.extract --source "$(vtb --raw tools.check | jq -r '.data.url')" --out /tmp/images
vtb --raw flash.partition --partition boot --image /tmp/images/boot.img
```

## 5. 扩展约定（给贡献者）

新能力接入只需三步，全部接口自动获得：
1. 在 `BuiltinCommands.RegisterAll()` 注册 `AgentCommand`（Verb/Summary/Params/Keywords/Execute）；
2. `Params` 即 JSON Schema（enum 值、default 会自动进入 MCP tools/list 与 CLI 解析）；
3. 危险操作设 `Destructive = true`。

契约由 `AgentSurfaceTests` 固化：工具名 MCP 安全字符集、schema 可序列化往返、参数解析（`--k v`/`--k=v`/默认值）、必填校验、中文意图路由、MCP 握手/未知方法错误码。套件 36/36 绿。上游引擎行为不变——Agent 层只是同一引擎的第三个面，一致性继承自命令级测试。
