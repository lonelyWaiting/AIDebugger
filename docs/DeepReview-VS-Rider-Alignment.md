# AIDebugger 深度 Review（功能完整性与 VS/Rider 对齐）

## 审查范围
- VS 扩展：`VSDebuggerMCP/DebuggerService.cs`、`VSDebuggerMCP/NamedPipeServer.cs`
- Rider 插件：`RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/DebuggerService.kt`、`RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/NamedPipeServer.kt`
- MCP 桥接：`mcp_server/vs_bridge.py`、`mcp_server/rider_bridge.py`、`mcp_server/ide_bridge.py`、`mcp_server/server.py`
- 自动注册与构建：`VSDebuggerMCP/CursorMcpRegistration.cs`、`RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/CursorMcpRegistration.kt`、`.github/workflows/build.yml`

## 结论摘要
- 当前接口层（method 名称）基本对齐，但实现层差异明显，Rider 在内存、模块、输出窗口、异常细节、寄存器、Watch 等能力上明显弱于 VS。
- 存在 2 个高风险稳定性/正确性问题：**Rider 内存读取结果可能“看起来成功但实际错误”**，以及 **Python bridge 超时后可能残留阻塞线程**。
- 建议先修“正确性与稳定性”再做“能力对齐”，否则 AI 会基于不可信数据分析，风险高于功能缺失。

## 实施进展（截至 2026-02-02）
- 已落地（正确性）：Rider/VS 的 `read_memory`、`search_memory` 不再用 `0` 填充失败字节，改为 `status` + `bytesRead/failedReads/partial` 的真实结果语义。
- 已落地（稳定性）：Rider/VS 的 callstack/threads 增加快照一致性重试（最多 2 次），并返回 `snapshotStable/snapshotAttempts/snapshotStatus` 字段。
- 已落地（稳定性）：bridge 侧 `_sync_call_to_pipe` 增加 Win32 级等待与读取超时控制（`WaitNamedPipeW` + `PeekNamedPipe`），避免仅靠外层 `wait_for`。
- 已落地（稳定性）：bridge 线程池改为有界专用 executor，并采用“内层 I/O 超时 < 外层 guard 超时”的策略，降低 timeout 后阻塞线程堆积风险。
- 已落地（稳定性）：bridge 已演进为 overlapped I/O，并在超时路径执行 `CancelIoEx`（不可用时回退 `CancelIo`），实现更强的“硬取消”能力。
- 已落地（错误信息约束）：内存/堆栈相关新路径统一使用状态字段，不返回“伪成功”数据；失败时返回 `unavailable/partial/unstable`。
- 已落地（能力合同）：VS/Rider 在 callstack/threads/modules/exception/registers/watch/output 响应中新增 `capability`/`behavior`/`availableCategories` 字段，显式声明能力边界。
- 已落地（P0 分析能力）：新增 `capture_debug_bundle`（统一 `snapshotId/capturedAt/snapshotStatus/partial`）与 `evaluate_expressions_batch`（批量同快照求值）。
- 已落地（P0 语义统一）：`get_debug_status` 新增标准化 `stopReason`（`not_paused/breakpoint/exception/step/user_break/pause_unknown`）与 `stopReasonRaw`。
- 已落地（Rider 对齐增强）：异常详情补齐 `stackTrace/source/hResult/innerException`（可得时返回），模块信息补齐 `FullName/Location/Version` 解析，寄存器增加分类并做 segment/XMM best-effort 读取。
- 已落地（可维护性）：VS 增加“类名->源码路径”缓存与 miss 缓存，减少 callstack 高频请求下的 solution 递归扫描。
- 已落地（可维护性）：VS 源码定位缓存改为“类型全名优先 + 短类名回退”，并增加 miss TTL 与 solution 切换自动失效，降低命名冲突和陈旧缓存风险。
- 已落地（注册体验）：Rider 注册增加 Python 3.10+ 预检，server 文件复制改为 bundle signature 增量策略（签名一致且文件齐全时跳过复制）。
- 已落地（安全边界）：VS/Rider Named Pipe 增加可选严格模式（`AIDB_PIPE_SECURITY=strict|same_user|owner_only`），支持同用户/owner-only 访问控制。
- 已落地（稳定性）：VS NamedPipe listener 的等待连接已支持取消联动（stop 时可打断 `BeginWaitForConnection`），避免停机时残留监听任务。
- 已落地（CI 基线）：新增最小合同测试（协议字段与 bridge 超时语义静态断言）并接入 workflow。
- 已验证：`VSDebuggerMCP` Release 构建通过（0 error）。
- 已验证（环境约束）：`RiderDebuggerMCP` 构建要求 Java 17+（本机默认 `JAVA_HOME` 指向 JDK8 会失败）；切换到 Rider 自带 JBR 21 后可完成构建流程（本地命令端可能因超时中断输出，建议以 CI 结果作为最终准入）。

## 主要发现（按严重级别）

### P0 - 数据正确性：Rider 内存相关工具可能返回伪造数据（默认为 0）
- 证据：
  - `RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/DebuggerService.kt:333`
  - `RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/DebuggerService.kt:336`
  - `RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/DebuggerService.kt:713`
  - `RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/DebuggerService.kt:716`
- 问题：读取单字节失败时直接用 `0` 填充，`read_memory`/`search_memory` 会给出“成功格式”的结果，导致误判。
- 影响：AI 可能在错误内存快照上进行根因分析，属于高风险“静默错误”。
- 推荐做法（最推荐）：
  1) 失败字节不要填 0，改为“失败计数 + 终止/部分结果标记”；  
  2) 返回结构增加 `bytesRead`、`failedReads`、`partial`；  
  3) 当失败率超过阈值直接返回 error，不给“看似完整”的 dump。

### P0 - 稳定性：bridge 的 timeout 不是 I/O 级超时，可能产生阻塞线程堆积
- 证据：
  - `mcp_server/vs_bridge.py:159`（声明 timeout 但 `_sync_call_to_pipe` 内未使用）
  - `mcp_server/vs_bridge.py:239`（阻塞 `ReadFile` 循环）
  - `mcp_server/vs_bridge.py:426`（外层 `wait_for` 仅超时 Future）
  - `mcp_server/rider_bridge.py:162`
  - `mcp_server/rider_bridge.py:242`
  - `mcp_server/rider_bridge.py:429`
- 问题：协程超时后，线程池中的阻塞 Win32 调用仍可能继续挂住。
- 影响：高并发或 IDE 偶发卡死时，MCP 服务吞吐下降，最终表现为“越来越慢”或“持续 timeout”。
- 推荐做法（最推荐）：
  1) 改为可取消的 overlapped I/O + `CancelIoEx`；  
  2) 或增加连接/读取阶段的 Win32 级等待超时并强制关闭 handle；  
  3) 对连续 timeout 做熔断与实例重选。
- 进展：已完成 Win32 级连接/读取超时 + 有界线程池 + guard 超时编排，并落地 overlapped I/O + `CancelIoEx`（含回退策略）。

### P1 - 功能不对齐：Output 能力 Rider 明显弱于 VS（部分修复）
- 证据：
  - Rider 只从 debug console 取文本：`RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/DebuggerService.kt:851`
  - Rider 固定 `availablePanes = ["Debug"]`：`RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/DebuggerService.kt:863`
  - VS 支持多 pane 枚举与查找：`VSDebuggerMCP/DebuggerService.cs:2923`、`VSDebuggerMCP/DebuggerService.cs:2938`
- 影响：`Build/General` 等输出在 Rider 端不可用，用户体验与 README 预期不一致。
- 进展：已在 Rider 返回中显式声明 `capability: limited_to_debug_console`，并对非 `Debug` pane 返回明确的“不支持”语义，避免误导。
- 后续：若要彻底对齐 VS，仍需补齐多 pane 读取实现。

### P1 - 功能不对齐：Modules/Exception 信息深度 Rider 明显低于 VS（部分修复）
- 证据：
  - Rider modules 依赖 `System.AppDomain.CurrentDomain.GetAssemblies()`：`RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/DebuggerService.kt:260`
  - Rider exception 仅填 type/message：`RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/DebuggerService.kt:289`
  - VS exception 包含 message/stackTrace/source/hResult/inner：`VSDebuggerMCP/DebuggerService.cs:876`
  - VS modules 含多来源枚举与 richer metadata：`VSDebuggerMCP/DebuggerService.cs:288`、`VSDebuggerMCP/DebuggerService.cs:321`
- 影响：Rider 端在 native/混合调试与异常深挖上信息不足，AI 诊断质量下降。
- 进展：Rider exception 已补齐 `stackTrace/source/hResult/innerException`（可获取时），modules 已补齐 `FullName/Location/Version` 解析并声明能力范围。
- 后续：Rider modules 的 native/混合调试深度（address/size/symbol）仍弱于 VS。

### P1 - 功能不对齐：Registers/Watch 在 Rider 端能力缩减（部分修复）
- 证据：
  - Rider 寄存器仅通用寄存器：`RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/DebuggerService.kt:664`
  - VS 还包含段寄存器与 XMM：`VSDebuggerMCP/DebuggerService.cs:2594`、`VSDebuggerMCP/DebuggerService.cs:2611`
  - Rider `add_watch` 实际仅求值返回：`RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/DebuggerService.kt:693`
  - VS 会执行 `Debug.AddWatch`：`VSDebuggerMCP/DebuggerService.cs:2696`
- 影响：同一 MCP 工具在两 IDE 行为不一致，提示词可复用性差。
- 进展：已加入 `watch.behavior`（Rider=`evaluate_only`，VS=`persisted_watch`）；Rider registers 已增加 `category` 与 `availableCategories`，并对 segment/XMM 做 best-effort 读取。
- 后续：Rider 是否“持久化到 Watch 窗口”仍不等同 VS，需要后续能力补齐。

### P2 - 功能不对齐：Callstack/Threads 字段完整度不一致（部分修复）
- 证据：
  - Rider callstack 未填 module/language/returnType：`RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/DebuggerService.kt:210`
  - VS 会填充这些字段：`VSDebuggerMCP/DebuggerService.cs:121`
  - Rider thread 的 `isFrozen` 固定 false，priority/suspendCount 未填：`RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/DebuggerService.kt:247`
  - VS 提供真实线程状态信息：`VSDebuggerMCP/DebuggerService.cs:238`
- 影响：跨 IDE 自动化分析规则难统一。
- 进展：已引入 `capability` 字段；Rider callstack 增加 module/language 推断填充，threads 显式返回受限语义（`partial_thread_metadata`）。
- 后续：Rider 线程冻结/优先级/挂起计数仍依赖底层 API 能力，当前为受限状态而非完全对齐。

### P2 - 可维护性与性能：VS 的文件定位逻辑重且运行在主线程（已缓解）
- 证据：
  - callstack 循环逐帧调用 `GetFrameFileName`：`VSDebuggerMCP/DebuggerService.cs:110`
  - `GetFrameFileName` 会做项目递归类搜索：`VSDebuggerMCP/DebuggerService.cs:3334`
  - 所有操作通过 `ExecuteOnMainThread` 且有 10s 超时：`VSDebuggerMCP/DebuggerService.cs:3724`、`VSDebuggerMCP/DebuggerService.cs:3749`
- 影响：大工程下可能导致 UI 卡顿、请求超时、返回不稳定。
- 进展：已增加“类名->源文件”命中缓存和 miss 缓存，降低重复扫描成本。
- 进展：已补充类型全名匹配、miss TTL、solution 切换自动失效，降低错误命中与长期 miss 污染。
- 后续：如需进一步优化，可继续将大扫描迁移到后台索引或更底层调试符号 API。

### P3 - 注册体验不对齐：Rider 每次启动复制 server 文件且无 Python 前置校验（已修复）
- 证据：
  - Rider 启动即注册：`RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/RiderDebuggerPlugin.kt:8`
  - `ensureServerFiles` 每次复制文件：`RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/CursorMcpRegistration.kt:66`
  - VS 有 Python 可用性检查：`VSDebuggerMCP/VSDebuggerMCPPackage.cs:103`
- 影响：启动耗时增加，且在无 Python 环境下可能写入不可用配置。
- 进展：已增加 Python 3.10+ 预检，并改为 bundle signature 增量复制（签名一致时跳过重复拷贝）。

### P3 - 安全边界：Named Pipe 对 Everyone 开放写权限（已修复为可配置）
- 证据：
  - VS 使用 WorldSid 读写：`VSDebuggerMCP/NamedPipeServer.cs:117`
  - Rider 使用 `WD` 的 GA 权限：`RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/NamedPipeServer.kt:164`
- 影响：同机任意进程理论上可发调试控制指令（continue/stop 等）。
- 进展：已增加可选严格安全模式（`AIDB_PIPE_SECURITY=strict|same_user|owner_only`），满足同用户/owner-only 白名单诉求；默认仍保留兼容模式。

### P3 - 质量保障：CI 仅构建，缺少自动化回归（已修复）
- 证据：
  - 工作流仅 restore/build/upload：`.github/workflows/build.yml:25`、`.github/workflows/build.yml:66`、`.github/workflows/build.yml:89`
- 影响：跨 IDE 行为回归无法被流水线提前发现。
- 进展：已补充最小合同测试并接入 workflow（协议关键字段与 bridge 超时机制静态断言）。

## VS 与 Rider 功能对齐矩阵（当前）
| 能力 | VS | Rider | 对齐状态 |
|---|---|---|---|
| 调试状态/控制（continue/step/stop） | 完整 | 基本完整 | 部分对齐 |
| callstack/threads 元数据 | 丰富 | 精简但有 capability 声明与部分推断 | 部分对齐 |
| modules | 多来源、含 metadata | 以 AppDomain 为主，补齐了 version/path 解析与 capability | 部分对齐 |
| exception | 含 stackTrace/source/hResult/inner | 已补齐可得字段并标注 capability | 部分对齐 |
| read_memory/search_memory | 可用且有错误兜底 | 已移除伪造数据，改为 status+partial 语义 | 已对齐（语义层） |
| disassembly | Iced + 内存读取优化 | Iced + 依赖表达式读内存 | 部分对齐 |
| output/search_output | 多 pane | debug console 单通道（已显式 capability） | 部分对齐 |
| registers | GP + Segment + XMM | GP + Segment/XMM best-effort + category | 部分对齐 |
| add_watch | 持久化到 Watch 窗口 | 仅 evaluate（已返回 behavior） | 部分对齐 |
| MCP 自动注册 | 有 Python 预检 | 已补 Python 预检 + 增量复制 | 已对齐（基线） |

## 最推荐的做法（执行优先级）
1. **先修正确性和稳定性（P0）**  
   先解决 Rider 内存伪成功与 bridge 阻塞超时，确保“返回结果可信”。
2. **建立“能力合同”而不是“方法同名即对齐”**  
   为每个工具定义必备字段、可选字段、失败语义、`unsupported` 约定，并在响应中暴露 capability。
3. **做跨 IDE 一致性测试（最小可行）**  
   针对 10 个高频工具（status/callstack/locals/exception/modules/read_memory/disassembly/output/registers/watch）建立合同测试，CI 至少跑序列化+dispatch+字段断言。
4. **分阶段补齐 Rider 与 VS 差距**  
   先补异常详情、输出 pane、watch 语义、寄存器分类，再补 modules 深度和线程元数据。
5. **把“安全模式”做成可配置项**  
   保留当前宽松模式以兼容管理员/非管理员通信，同时提供同用户 SID 限制模式。
