---
name: ida-evidence-analysis
description: 使用 IDA MCP 分析二进制、驱动及受保护控制流，导出反编译结果并建立可复核的实现与检测规则证据。用户要求后续 IDA 分析默认使用此 Skill，并根据验证结果持续完善。
---

# IDA evidence analysis

本 Skill 由 AIDebugger 项目维护；使用 `python tools/install_aidebugger.py` 安装 MCP 与 Skill。源码版本位于 `skills/ida-evidence-analysis/`，不要只修改安装副本。

## AIDebugger 后端选择与实例管理

- 有源码、PDB、托管变量、源码断点或 VS 崩溃现场时使用 AIDebuggerVS。无源码的反汇编、Hex-Rays、调用图、IDB 编辑和二进制 patch 使用独立 IDA MCP。两者可同时运行；同一 Windows PID 不可同时由两个用户态调试器附加。IDA 静态分析对应模块时可保留 VS 的动态调试现场。
- 新任务调用 `ida_open(input_path, mode="headless")`；返回 `sessionId` 后短轮询 `ida_session_status`。`headless` 使用已授权安装的 `idat` 与 `TVHEADLESS=1`，`gui` 使用 `ida.exe`。不会接管已有 GUI 或默认 13337 endpoint；不需先手动启动插件。
- 每次操作显式传 `session_id`；通过 `ida_list_tools` 分页获取实际工具和完整参数 schema，再用 `ida_call_tool(session_id, tool, arguments)` 调用。保留上游 `content/structuredContent/isError`；批量结果也要检查每项 `error`，不能仅看外层 `isError=false`。
- 就绪与自动分析完成分开：检查 `identity.autoAnalysisReady`；需要时调用 `server_warmup` 的实际 schema 等待分析，不能把已可查询视为全库分析完成。
- 调试先调用 `debugger_configure` 设置调试器和 EXE；DLL 需要加载它的宿主 EXE或明确附加的 PID。再使用 `dbg_start` 或 `debugger_attach`；读取状态、寄存器、事件确认，不因请求被接受就断言附加成功。`debugger_detach` 不终止目标，`dbg_exit` 终止目标。动态实验仍需用户授权范围。
- patch 默认修改隔离 IDB。`export_patched_binary` 只写新文件，核验输入哈希、每个文件偏移与原始字节，拒绝未映射 patch。导出会改变签名有效性；原 DLL 不变，导出成功不等于运行功能验证。
- VS 现场与 IDA 数据库关联时先核对模块哈希，使用 `RVA=IDA地址-imageBase` 和 `运行时地址=模块加载基址+RVA`；不能只按 DLL 名称或裸地址对应。
- `ida_close(save=True)` 保存确认后关闭，短轮询到进程退出。保存失败或调试仍活跃时不关闭。GUI 插件退出残留可在已确认保存、无活动调试的关闭请求后，通过原始 owned 进程句柄结束；返回 receipt 明示该动作。禁止对外部实例或 unknown outcome 执行该处理。
- 工具超时返回 `IDA_OUTCOME_UNKNOWN` 时，操作可能仍在执行；不自动重试 mutation，不杀进程，不替换实例。先保存错误并查询状态。
- 上游输出截断时，读取 `_meta.ida_mcp.output_id`，调用已发现的 `read_cached_output` 分页提取 JSON 文本。私有下载 URL 要认证，不能直接执行返回的匿名 curl 提示；不要为重新生成缓存而重复 mutation。

通过 IDA MCP 完成用户授权的逆向分析。结论以样本字节、汇编、调用关系和异常处理路径为依据；导出的伪代码与恢复的语义模型分别交付。

## 开始与范围

- 确认目标路径、SHA256、架构、PE 信息、签名状态，以及 MCP 当前 IDB 的输入路径和样本哈希。当前 IDB 不匹配时先解决目标绑定，不分析错误样本。
- 外部正在使用的 IDA 场景保持原 IDB、端口和 gateway 配置；新任务用独立实例及私有 MCP endpoint，并记录 owned process 与样本身份，不抢占默认绑定。已有匹配的已保存 IDB 时直接复用；不要把重跑全库自动分析作为 MCP 可用的前置条件，仍需记录当前分析完成程度。
- 发现当前可用的 IDA MCP 工具及参数；不硬编码某一版本的工具接口。优先结构化结果，限制分页输出，用 `py_eval` 做批量只读导出。
- 不因“分析”而加载驱动、执行样本、注入进程或修改运行中的内核。需要动态实验时单独确定环境和用户授权。
- 先建立函数、导入、字符串、段、交叉引用及 PE exception directory 清单，再按入口、调度、采集、规则、输出串联调用链。
- 用户问运行故障/退出根因时，先盘点已有诊断文件、控制台输出、退出码、转储及 ETW，再关联静态地址；不要用能力清单代替故障证据。核对运行时加载路径与分析样本的身份，不能假定安装目录和 System32 中的同名文件相同。分开“启动失败”“终止执行者”“触发规则”，已有证据不足时明确缺失层级。

## 反编译与证据

- 分批导出全部已识别函数；记录地址、名称、范围、chunks、状态、警告与失败原因。调用反编译器可能增加函数，重新盘点直到快照稳定，并记录新增项。这里的“全部”仅指 IDA 当前识别的函数，不代表全部原始源码或全部保护指令。
- 可使用 [scripts/ida_export_batch.py](scripts/ida_export_batch.py)。在 IDA Python 中用 `runpy.run_path` 加载，显式传入输出目录、起始索引和批量大小。该脚本不修改类型、函数或字节；反编译器仍可能自动完善 IDB。每批独立落盘。
- 同时保存原始伪代码、关键汇编和分析者恢复的语义代码。禁止把 Hex-Rays 输出称为原始源码、可编译源码或正确性已证明的实现。
- 没有警告不等于正确。每个规则至少检查条件分支、返回值使用、位宽/符号、位掩码、循环边界、错误/异常路径及字段来源。
- 原型、`noreturn`、栈分析错误可改变控制流。临时修正前保存类型/属性，修正后仍对照汇编，最终恢复并验证；需持久化时保存独立分析副本。不得隐藏失真的伪代码。
- 每项发现记录：地址/RVA、入口与调用链、输入、触发谓词、动作/返回/输出字段、配置门控、异常行为、证据文件、可信度和未知项。区分采集能力、候选异常、规则命中、上报、处罚；不能跨层推断。

## 受保护代码

遇到高熵段、跳板、返回地址变换、控制流平坦化或反编译失败时，读取 [references/protected-and-kernel.md](references/protected-and-kernel.md)。段名不能证明壳类型。

先用静态分析及受限离线 CPU 仿真恢复具体调用链。隔离导入和特权操作；记录初始状态、停止原因、轨迹、覆盖边界。多组具体状态的相同目标仍不是符号证明，不称为完整脱壳或去虚拟化。

## 已授权动态调试的事件读取

- `debug_event_t` 是由 `eid()` 标记的 union。先保存基本 metadata（eid、pid、tid、ea、handled），再用当前安装的 `ida_idd` 事件常量选择合法 payload getter；禁止对任意事件统一调用 `info()`。安装的 SWIG docstring 可能错位，getter 合法性应核对对应 SDK 的 guard，而非仅据 docstring 推断。
- 官方 [idd.hpp getter guards](https://cpp.docs.hex-rays.com/idd_8hpp_source.html)（QASSERT 3295–3299）：`modinfo()` 仅用于 PROCESS_STARTED / PROCESS_ATTACHED / LIB_LOADED；`exit_code()` 仅用于 PROCESS_EXITED / THREAD_EXITED；`info()` 仅用于 THREAD_STARTED / LIB_UNLOADED / INFORMATION；`bpt()` 仅用于 BREAKPOINT；`exc()` 仅用于 EXCEPTION。其他事件只读取基本 metadata，除非当前 SDK 另有已核实的合法 getter。
- getter 的 native assertion 或 MCP/tool 失败是观察工具故障，不等于被调试 API（包括 CUDA）失败、目标退出或等待超时。事件读取出错时保存实例、target identity、已收集事件和原始错误；用有权控制该目标的调试器状态、明确退出事件/退出码或匹配进程身份的终止证据确认 terminal 状态后，才进入重启流程。不要因事件序列化错误自动杀掉或替换仍存活的实例/目标。

## 验证与交付

- 用原始 PE 字节和汇编复核核心谓词；检查统计相加一致、快照稳定、文件存在、目标哈希未改变、临时修改已恢复。报告明确验证层级和限制。
- 保存报告、函数索引、全部可导出伪代码、失败清单、脚本、轨迹和必要的原始证据。附覆盖率分母及未知范围；源代码级还原未完成则直述，不用导出成功率代替逻辑覆盖率。
- 使用官方文档解释 Windows 权限、结构和 CPU 指令语义；文档支持术语，不代替样本证据。

## 持续完善

用户已要求后续 IDA 分析默认使用本 Skill，并根据实际任务持续完善。遇到可复现错误或新验证方法时，先验证再做小范围更新；只收录可泛化流程，不写入样本私有规则、未证实结论或路径常量。新增/修改脚本需运行验证，最后用 skill-creator 的 `quick_validate.py` 验证格式，并报告实际改动。此约定不自动授权执行样本或改变外部系统。
