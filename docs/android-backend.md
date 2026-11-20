# Android native debugging

Android MCP attaches LLDB to a running debuggable app on a connected Android
device or emulator. The host runtime supports Windows x64. Use native C/C++
debug builds with matching unstripped libraries to resolve source lines and
inspect variables. Managed Java/Kotlin debugging and app launch are outside
this backend.

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
Studio, a system Android SDK/NDK, and Android paths on `PATH` are unnecessary.

For offline setup, give `--android-archives <directory>` containing the official
`android-ndk-r27c-windows.zip` and `platform-tools_r37.0.3-win.zip`. Omit
`--client codex` to register Claude and Codex. Restart the MCP client or start a
new session after registration. The device must have USB or wireless debugging
enabled and authorize this host. Windows USB connections can require the
manufacturer's device driver.
To install IDA, kernel, and Android together, pass `--backend all` with
`--accept-android-licenses` or `--android-archives`.

## Debug a running app

1. Start the app and call `android_list_devices`. Note the serial of the device
   in the `device` state.
2. Call `android_attach` with the serial and Android application ID. Supply
   `symbol_dirs` as absolute directories containing the exact unstripped
   libraries for this build and ABI. Use `source_map` to map build-machine path
   prefixes to existing local source directories. To attach to a secondary
   process, supply its `package:suffix` name. Specify `abi` if package metadata
   fails to identify the target architecture.
3. Poll `android_job_status` with the session and job IDs. Once attached, set a
   breakpoint by symbol or file and line using `android_set_breakpoint`, then
   continue with `android_control`. A breakpoint reporting zero resolved
   locations can resolve when the library loads.
4. Inspect `status` and compare `stopId` to confirm a new stop. Use
   `android_inspect` for threads, call stacks, locals, modules, or breakpoints.
   Use a returned thread ID when a thread other than the selected thread is needed.
5. Call `android_detach` and poll its result to release the target and delete
   this session's forwarding and temporary device files. A job can report
   cleanup errors or an unknown target state after a transport failure.

The host starts a dedicated LLDB process for each session. ADB transfers the
matching `lldb-server`, starts it through Android's `run-as`, and forwards a
host loopback port to its unique abstract Unix socket. ADB device transports,
Android sessions, and job IDs are explicit. An existing Android Studio or LLDB
attachment can prevent a second debugger from attaching to the same process.

## Validate

Run `python -m unittest discover -s tests -p "test_android_backend.py"` for the
installer, target validation, and session contracts. The opt-in
`tests/android_integration/run_android_smoke.py --help` describes the live
smoke for a running test app with a repeatedly invoked native function. It
drives the installed MCP with SDK environment variables and SDK paths removed.
