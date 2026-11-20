# Android debugging

Android MCP launches or attaches to debuggable apps on Windows x64. Choose
native LLDB, Java JDWP, or both on the same process for Java/JNI handoffs.
Native source inspection uses matching unstripped libraries. Java source lines
and locals use the APK's bytecode debug metadata.

## Install

Review Google's [Platform-Tools terms](https://developer.android.com/tools/releases/platform-tools)
and [NDK terms](https://developer.android.com/ndk/downloads/) before running:

```powershell
.\Install-AIDebugger.cmd --backend android --client codex --accept-android-licenses
```

The installer downloads pinned Google ZIPs (about 790 MB total), verifies their
sizes and SHA-256 hashes, extracts the debugging files, and smoke-tests ADB
and LLDB before registering the Android MCP server. The private runtime lives
under `~/.ai-debugger/android/runtime/` and is reused on later runs. Android
Studio, a system Android SDK/NDK or JDK, and Android paths on `PATH` are unnecessary.

For offline setup, give `--android-archives <directory>` containing the official
`android-ndk-r27c-windows.zip` and `platform-tools_r37.0.3-win.zip`. Omit
`--client codex` to register Claude and Codex. Restart the MCP client or start a
new session after registration. The device must have USB or wireless debugging
enabled and authorize this host. Windows USB connections can require the
manufacturer's device driver.
To install IDA, kernel, Windows dump, and Android together, pass `--backend all` with
`--accept-android-licenses` or `--android-archives`.

Use `debug_capabilities(domain="inspection")` to discover native and Java views
and their direct tools. `debug_inspect(target=<session ID>, query="stack", view="java")`
returns the regular Android job receipt. Poll it with `android_job_status`.
Use the direct inspection tools to select a thread, frame, or page.
Omit `view` to use the session's debugger, or the native view in a dual session.

## Launch or attach

1. Call `android_list_devices` and choose the serial in the `device` state.
2. Use `android_attach` for a running app. Use `android_launch` with its
   application ID and activity class, such as `.MainActivity`, to set breakpoints
   before startup. The target process must be stopped for launch. Choose the
   `native`, `java` or `dual` mode. To select a secondary process, use its
   `package:suffix` name.
3. In native or dual mode, set `symbol_dirs` to directories containing exact
   unstripped libraries for the installed build and ABI. Use `source_map` to map
   build-machine source prefixes to existing local directories. Supply an
   explicit `abi` when package metadata cannot identify the runtime architecture.
4. Poll `android_job_status` with both IDs. Attach and launch report a phase
   such as `wait_for_jdwp`, `prepare_symbols`, `deploy_lldb_server` or
   `connect_lldb`. They accept a `deadline_ms` and support cancellation through
   `android_cancel_job`. A cancelled wait leaves any previously submitted
   continue or step in effect. A transport interruption after dispatch can
   return an unknown target state. Cleanup errors are reported in the job receipt.
5. Use `android_detach` to release both debuggers, the native forward and the
   direct JDWP connection. A live
   debugger in another IDE can prevent attaching to the same process.

## Native inspection and execution

- `android_set_breakpoint` supports symbols, source lines and load addresses,
  with optional conditions. `android_configure_breakpoint` changes conditions
  and enabled state. `android_watchpoint` sets or removes hardware watchpoints
  where the target supports them.
- `android_inspect` exposes threads, stack, locals, loaded modules and breakpoints.
  `android_native_query` adds registers, disassembly, address resolution, bounded
  memory reads/search, and LLDB expression evaluation. Evaluation can run code
  in the target process.
- `android_write_memory` writes raw bytes supplied as contiguous hex pairs to
  a paused native target. Pass `expected_stop_id` to tie the write to a specific
  stop. `requestedRangeVerified` confirms a complete write and readback match.
  `bytesWritten` and `verified` describe the prefix reported by LLDB. A lost
  reply, transport error or short write can leave bytes changed.
- `android_snapshot` captures one stop's status, stack and top-frame locals.
  `android_run_until_stop` can issue one continue or step and capture the next
  stop in a single job. Compare `stopId` and inspect `outcome` and
  `snapshotStable`. LLDB can stop on an unrelated signal before a breakpoint.
- The server reuses pulled system symbol files across sessions on the same
  device build and ABI. Each native session owns its own `lldb-server` and ADB
  forwarding. Android can still report an input-dispatch ANR while a foreground
  app is paused at a debugger stop.

## Java and JNI

For startup, provide `java_breakpoints` with `class_name` and `method` or
`line`, and `native_breakpoints` with `symbol`, source location or `address`.
Breakpoints can bind as classes and shared libraries load. Use a Java method
descriptor to distinguish overloads when needed.

- In native mode, continue once to release the startup gate and run to an
  initial breakpoint such as `JNI_OnLoad`.
- In Java mode, use `android_java_control`. It can wait server-side and return
  the new Java stop, stack and locals. Set method or source-line breakpoints
  with `android_java_breakpoint`, configure exception stops with
  `android_java_exception_breakpoint`, and use `android_java_object` for
  declared instance fields, arrays and strings. Java IDs use decimal strings.
- In dual mode, use `android_java_control(action="continue",
  release_native_startup=true, wait_ms=10000)` to release the initial LLDB stop
  and the startup gate in one job. Subsequent Java continues can report
  `native_stop`. Use native control at that point. When native execution
  returns to a Java breakpoint, `android_run_until_stop` can report
  `java_stop` with the Java stack and locals. While LLDB has stopped the
  process, Java status returns `blockedByNative` instead of waiting on a
  suspended JDWP agent. Java and native stacks remain separate views.

The Java view provides JDWP inspection and stepping. Full Java expression
evaluation, Kotlin inline/coroutine source mapping, and merged Java/native
stack rendering are outside the current feature set.

## Validate

Run `python -m unittest discover -s tests -p "test_android_backend.py"` for the
installer, target validation, and session contracts. The opt-in
`tests/android_integration/run_android_smoke.py --help` describes the live
smoke for a running test app with a repeatedly invoked native function. It
drives the installed MCP with SDK environment variables and SDK paths removed.

The Java/JNI smoke is emulator-only. Its fixture builder uses an SDK, NDK and
JDK to build an APK for testing. The installed debugger uses its own runtime.

```powershell
python tests/android_integration/build_dual_probe.py --sdk $env:ANDROID_HOME --jdk <JDK> --output <fixture-dir>
adb -s emulator-5588 install <fixture-dir>/dual.apk
python tests/android_integration/run_android_dual_smoke.py --server <installed-android_server.py> --adb <private-adb.exe> --fixture <fixture-dir>
```
