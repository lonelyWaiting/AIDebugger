# Protected control flow and kernel evidence

## IDA MCP Python

`py_eval` 的 locals 可能持久化、globals 可能每次重建。跨调用函数不要隐式依赖上次导入的局部模块；通过默认参数、显式传参或函数内导入绑定。读取工具文档确认接口。类型读取在不同版本常见为 `ida_nalt.get_tinfo`，不要猜 API。批量结果直接写入独立诊断目录，返回紧凑统计。

## 保护与离线仿真

先分析跳板中的 call/jmp、栈指针、返回地址和间接目标；把 `.pdata`/`.rdata` 的 RVA 引用与真实调用区分。PE exception directory 不一定位于名为 `.pdata` 的段。解析 RUNTIME_FUNCTION、UNWIND_INFO 与语言特定 scope table，恢复反编译器省略的 except/恢复路径。

Capstone/Unicorn/pefile 可以辅助，但仿真只使用文件映像和人工内存。用时间、指令数、地址范围三种上限；导入、未映射读写、栈外写、特权指令、系统调用、CPUID/时间读取等需停止或按显式有记录的模型处理。检查内存写 hook 的停止语义，不能仅凭函数名声称无副作用。不同种子用于发现状态依赖，不能证明全部状态。

保留原始字节与 IDB；修复函数边界、栈、类型、反汇编标记时记录前后状态，验证语义后才进入下一步。虚拟化逻辑仍无法还原时交付覆盖账本和可继续的具体节点，不用大量假设填空。

## 驱动中的常见证据陷阱

- 高事件量的环形 ETW 可能覆盖目标时段。先检查实际保留的时间范围、目标事件及丢失统计；解析成功不证明时段完整。缺事件时不能推断未发生，可缩减事件集合或缩短高流量采集窗口后再验证。旧追踪的调用栈与新复现的退出状态/来源线程要分别标明，不拼成一次完整新采集。
- OB callbacks：核对 ObjectType、KernelHandle、create/duplicate、目标归属、当前对象、保护集合、例外匹配及 OriginalDesiredAccess/DesiredAccess 两个字段。掩码取补集后对应官方权限名；不要把裁剪若干权限写成拒绝全部句柄。
- VA 枚举：区分 MEM_COMMIT、MEM_IMAGE/MEM_MAPPED/MEM_PRIVATE 和精确 Protect 掩码；`0x70` 不包含 PAGE_EXECUTE_WRITECOPY `0x80`。采集非映像可执行内存不能直接证明恶意。
- WorkingSetWatch 只覆盖产生记录的访问，不代表观察全部内存读写。PFN/EPROCESS/内部表偏移需要 OS/build 分支证据。
- CPUID/MSR/XCR/计时：检查 selector、结果位、异常路径、循环范围、最小值、无符号减法、恢复路径。Hypervisor bit 不证明作弊；检测函数存在不证明当前配置启用。
- Driver/module/BigPool/AVL：列举、指纹匹配、关联判定分别记录。候选内部 AVL 表不能直接命名 PiDDBCacheTable。函数指针发现不等于 hook 检测或修改。
- 导入无直接 xref 不证明未使用；字符串中的进程名不证明完整黑白名单；编译器 GS fail-fast 不证明反作弊主动蓝屏。
- 地址相同的全局/局部伪代码变量、错误 noreturn 和函数原型会造成恒假比较或丢分支；原始汇编优先。
