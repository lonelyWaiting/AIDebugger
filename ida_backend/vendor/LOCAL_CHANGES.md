Vendored source: mrexodia/ida-pro-mcp, local 2.0.0 source archive used by the
installed Claude plugin. Exact git commit unavailable in that archive.
UPSTREAM.json records original file hashes; LICENSE.ida-pro-mcp is retained.

Local change: ida_pro_mcp/ida_mcp/mainthread.py adds an optional on_idle callback
to MainThreadPump.run. The owning IDA thread uses it to service debugger events
and lifecycle shutdown while idat is running without its interactive event loop.
The default behavior is unchanged for other callers.

AIDebugger's session management, authenticated HTTP handler, debugger setup,
guarded event serialization and file patch export live outside the vendor tree.
IDA, Hex-Rays binaries, licenses and the idapro package are not redistributed.
