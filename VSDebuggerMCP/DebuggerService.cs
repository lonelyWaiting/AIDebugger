using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using EnvDTE;
using EnvDTE80;
using EnvDTE100;
using Microsoft.VisualStudio.Shell;
using VSDebuggerMCP.Protocol;

namespace VSDebuggerMCP
{
    /// <summary>
    /// Service that wraps Visual Studio debugger APIs to provide debugging information.
    /// </summary>
    public class DebuggerService
    {
        private readonly DTE2 _dte;
        private readonly Action<string> _log;
        private readonly DisassemblyService _disasmService;

        public DebuggerService(DTE2 dte, Action<string> log)
        {
            _dte = dte ?? throw new ArgumentNullException(nameof(dte));
            _log = log ?? ((s) => { });
            _disasmService = new DisassemblyService();
        }

        private Debugger5 Debugger
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return _dte.Debugger as Debugger5;
            }
        }

        #region Debug Status

        public DebugStatusResult GetDebugStatus()
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new DebugStatusResult
                {
                    IsDebugging = debugger.CurrentMode != dbgDebugMode.dbgDesignMode,
                    IsPaused = debugger.CurrentMode == dbgDebugMode.dbgBreakMode,
                    DebugMode = GetDebugModeString(debugger.CurrentMode)
                };

                if (result.IsDebugging && debugger.CurrentProcess != null)
                {
                    result.CurrentProcess = debugger.CurrentProcess.Name;
                }

                if (result.IsPaused && debugger.CurrentStackFrame != null)
                {
                    var frame = debugger.CurrentStackFrame;
                    result.CurrentLocation = new LocationInfo
                    {
                        FilePath = GetFrameFileName(frame),
                        FunctionName = frame.FunctionName,
                        LineNumber = GetFrameLineNumber(frame),
                        ColumnNumber = 1
                    };
                }

                return result;
            });
        }

        private string GetDebugModeString(dbgDebugMode mode)
        {
            switch (mode)
            {
                case dbgDebugMode.dbgDesignMode: return "Design";
                case dbgDebugMode.dbgBreakMode: return "Break";
                case dbgDebugMode.dbgRunMode: return "Running";
                default: return "Unknown";
            }
        }

        #endregion

        #region Call Stack

        public CallStackResult GetCallStack()
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new CallStackResult();

                if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    return result;
                }

                var currentThread = debugger.CurrentThread;
                if (currentThread != null)
                {
                    result.ThreadId = currentThread.ID;
                    result.ThreadName = currentThread.Name ?? $"Thread {currentThread.ID}";

                    int index = 0;
                    foreach (EnvDTE.StackFrame frame in currentThread.StackFrames)
                    {
                        var lineInfo = GetFrameLineNumberEx(frame);
                        result.Frames.Add(new StackFrameInfo
                        {
                            Index = index++,
                            FunctionName = frame.FunctionName,
                            FilePath = GetFrameFileName(frame),
                            LineNumber = lineInfo.LineNumber,
                            LineNumberReliable = lineInfo.IsReliable,
                            ColumnNumber = 1,
                            Module = frame.Module,
                            Language = frame.Language,
                            ReturnType = frame.ReturnType
                        });
                    }
                }

                return result;
            });
        }

        #endregion

        #region Locals

        public LocalsResult GetLocals()
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new LocalsResult();

                if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    return result;
                }

                var currentFrame = debugger.CurrentStackFrame;
                if (currentFrame == null)
                {
                    return result;
                }

                // Get locals
                foreach (EnvDTE.Expression local in currentFrame.Locals)
                {
                    result.Variables.Add(CreateVariableInfo(local, 0, 2));
                }

                // Get arguments
                try
                {
                    foreach (EnvDTE.Expression arg in currentFrame.Arguments)
                    {
                        var varInfo = CreateVariableInfo(arg, 0, 2);
                        varInfo.Name = $"[arg] {varInfo.Name}";
                        result.Variables.Add(varInfo);
                    }
                }
                catch
                {
                    // Arguments may not be available
                }

                return result;
            });
        }

        private VariableInfo CreateVariableInfo(EnvDTE.Expression expr, int currentDepth, int maxDepth)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var info = new VariableInfo
            {
                Name = expr.Name,
                Type = expr.Type,
                Value = expr.Value,
                HasChildren = expr.DataMembers?.Count > 0,
                IsReadOnly = expr.IsValidValue == false
            };

            if (info.HasChildren && currentDepth < maxDepth)
            {
                info.Children = new List<VariableInfo>();
                try
                {
                    foreach (EnvDTE.Expression child in expr.DataMembers)
                    {
                        info.Children.Add(CreateVariableInfo(child, currentDepth + 1, maxDepth));
                    }
                }
                catch
                {
                    // Children may not be accessible
                }
            }

            return info;
        }

        #endregion

        #region Threads

        public ThreadsResult GetThreads()
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new ThreadsResult();

                if (debugger.CurrentMode == dbgDebugMode.dbgDesignMode)
                {
                    return result;
                }

                var currentThread = debugger.CurrentThread;
                result.CurrentThreadId = currentThread?.ID ?? 0;

                foreach (EnvDTE.Thread thread in debugger.CurrentProgram.Threads)
                {
                    var threadInfo = new ThreadInfo
                    {
                        Id = thread.ID,
                        Name = thread.Name ?? $"Thread {thread.ID}",
                        IsCurrent = currentThread != null && thread.ID == currentThread.ID,
                        IsFrozen = thread.IsFrozen,
                        Priority = thread.Priority,
                        SuspendCount = thread.SuspendCount
                    };

                    // Try to get location
                    try
                    {
                        if (thread.StackFrames.Count > 0)
                        {
                            var topFrame = thread.StackFrames.Item(1) as EnvDTE.StackFrame;
                            if (topFrame != null)
                            {
                                var lineNum = GetFrameLineNumber(topFrame);
                                threadInfo.Location = $"{topFrame.FunctionName} ({GetFrameFileName(topFrame)}:{lineNum})";
                            }
                        }
                    }
                    catch
                    {
                        threadInfo.Location = "Unknown";
                    }

                    result.Threads.Add(threadInfo);
                }

                return result;
            });
        }

        #endregion

        #region Modules

        public ModulesResult GetModules()
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new ModulesResult();

                if (debugger.CurrentMode == dbgDebugMode.dbgDesignMode)
                {
                    result.Error = "Not in debug mode";
                    return result;
                }

                try
                {
                    // Method 1: Try to get modules from stack frames (most reliable)
                    var moduleSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var moduleInfoDict = new Dictionary<string, ModuleInfo>(StringComparer.OrdinalIgnoreCase);

                    // Collect modules from all threads' stack frames
                    try
                    {
                        foreach (EnvDTE.Thread thread in debugger.CurrentProgram.Threads)
                        {
                            try
                            {
                                foreach (EnvDTE.StackFrame frame in thread.StackFrames)
                                {
                                    string modulePath = frame.Module;
                                    if (!string.IsNullOrEmpty(modulePath) && !moduleSet.Contains(modulePath))
                                    {
                                        moduleSet.Add(modulePath);
                                        var moduleInfo = CreateModuleInfoFromPath(modulePath, debugger);
                                        moduleInfoDict[modulePath] = moduleInfo;
                                    }
                                }
                            }
                            catch
                            {
                                // Some threads may not have accessible stack frames
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _log($"Error collecting modules from stack frames: {ex.Message}");
                    }

                    // Method 2: Try to enumerate modules via PEB (Process Environment Block) for native processes
                    try
                    {
                        // Get PEB address and enumerate loaded modules using expression evaluation
                        var additionalModules = GetModulesFromPEB(debugger);
                        foreach (var mod in additionalModules)
                        {
                            if (!moduleSet.Contains(mod.Path))
                            {
                                moduleSet.Add(mod.Path);
                                moduleInfoDict[mod.Path] = mod;
                            }
                            else
                            {
                                // Merge additional info if we already have this module
                                var existing = moduleInfoDict[mod.Path];
                                if (string.IsNullOrEmpty(existing.Address) && !string.IsNullOrEmpty(mod.Address))
                                {
                                    existing.Address = mod.Address;
                                }
                                if (string.IsNullOrEmpty(existing.Size) && !string.IsNullOrEmpty(mod.Size))
                                {
                                    existing.Size = mod.Size;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _log($"Error getting modules from PEB: {ex.Message}");
                    }

                    // Method 3: Try common system DLLs that are usually loaded
                    TryAddCommonSystemModules(debugger, moduleSet, moduleInfoDict);

                    // Sort and add to result
                    int order = 0;
                    var sortedModules = moduleInfoDict.Values
                        .OrderBy(m => m.IsSystemModule)
                        .ThenBy(m => m.Name)
                        .ToList();

                    foreach (var mod in sortedModules)
                    {
                        mod.Order = order++;
                        result.Modules.Add(mod);
                    }

                    result.TotalCount = result.Modules.Count;

                    if (result.Modules.Count == 0)
                    {
                        result.Error = "No modules found. The process may not have loaded any modules yet or module enumeration is not supported for this debug target.";
                    }
                }
                catch (Exception ex)
                {
                    _log($"Error getting modules: {ex.Message}");
                    result.Error = $"Error enumerating modules: {ex.Message}";
                }

                return result;
            });
        }

        private ModuleInfo CreateModuleInfoFromPath(string modulePath, Debugger5 debugger)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var info = new ModuleInfo
            {
                Path = modulePath,
                Name = System.IO.Path.GetFileName(modulePath),
                SymbolsLoaded = false,
                IsSystemModule = IsSystemModule(modulePath)
            };

            // Try to get additional info via expression evaluation
            try
            {
                // Try to get module base address
                string safeModuleName = info.Name.Replace(".", "_").Replace("-", "_");
                
                // Try GetModuleHandle to get base address
                var handleExpr = debugger.GetExpression($"(void*)GetModuleHandleA(\"{EscapeForExpression(info.Name)}\")");
                if (handleExpr != null && handleExpr.IsValidValue && handleExpr.Value != "0x0000000000000000" && handleExpr.Value != "0x00000000")
                {
                    info.Address = handleExpr.Value;

                    // Try to get module size from PE header
                    try
                    {
                        var dosHeader = debugger.GetExpression($"*(short*){handleExpr.Value}");
                        if (dosHeader != null && dosHeader.IsValidValue)
                        {
                            // Check for MZ signature
                            string dosVal = dosHeader.Value.Trim();
                            // MZ = 0x5A4D
                            if (dosVal == "23117" || dosVal == "0x5a4d" || dosVal.ToLower() == "0x5a4d")
                            {
                                // Get e_lfanew (offset to PE header) at offset 0x3C
                                var lfanewExpr = debugger.GetExpression($"*(int*)({handleExpr.Value} + 0x3C)");
                                if (lfanewExpr != null && lfanewExpr.IsValidValue)
                                {
                                    int lfanew;
                                    if (int.TryParse(lfanewExpr.Value, out lfanew))
                                    {
                                        // SizeOfImage is at PE header + 0x50 (for 64-bit) or + 0x50 (same offset)
                                        var sizeExpr = debugger.GetExpression($"*(unsigned int*)({handleExpr.Value} + {lfanew} + 0x50)");
                                        if (sizeExpr != null && sizeExpr.IsValidValue)
                                        {
                                            uint size;
                                            if (uint.TryParse(sizeExpr.Value, out size))
                                            {
                                                info.Size = FormatSize(size);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _log($"[GetModuleDetails] Error getting module info: {ex.Message}");
                    }
                }

                // Check if symbols are loaded by trying to resolve a symbol
                try
                {
                    // A simple heuristic: if we can see function names in the stack, symbols are likely loaded
                    info.SymbolsLoaded = !string.IsNullOrEmpty(info.Address);
                }
                catch (Exception ex)
                {
                    _log($"[GetModuleDetails] Error checking symbols: {ex.Message}");
                }

                // Determine if 64-bit
                info.Is64Bit = Is64BitProcess(debugger);

                // Try to get version info
                info.Version = GetModuleVersion(modulePath);
            }
            catch (Exception ex)
            {
                _log($"Error getting module info for {modulePath}: {ex.Message}");
            }

            return info;
        }

        private List<ModuleInfo> GetModulesFromPEB(Debugger5 debugger)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var modules = new List<ModuleInfo>();

            try
            {
                // Detect if 64-bit process
                var ripExpr = debugger.GetExpression("@rip");
                bool is64Bit = ripExpr != null && ripExpr.IsValidValue;

                // For 64-bit process: PEB is at gs:[0x60]
                // For 32-bit process: PEB is at fs:[0x30]
                
                // Try to get PEB address
                EnvDTE.Expression pebExpr = null;
                
                if (is64Bit)
                {
                    pebExpr = debugger.GetExpression("(void*)__readgsqword(0x60)");
                }
                
                if (pebExpr == null || !pebExpr.IsValidValue || pebExpr.Value == "0x0000000000000000")
                {
                    // Try 32-bit
                    pebExpr = debugger.GetExpression("(void*)__readfsdword(0x30)");
                    is64Bit = false;
                }

                if (pebExpr == null || !pebExpr.IsValidValue)
                {
                    // Fallback: try @$peb pseudo-register
                    pebExpr = debugger.GetExpression("@$peb");
                }

                if (pebExpr != null && pebExpr.IsValidValue && 
                    pebExpr.Value != "0x0000000000000000" && pebExpr.Value != "0x00000000")
                {
                    // Get Ldr (PEB_LDR_DATA) - offset 0x18 for 64-bit, 0x0C for 32-bit
                    int ldrOffset = is64Bit ? 0x18 : 0x0C;
                    var ldrExpr = debugger.GetExpression($"*(void**)({pebExpr.Value} + {ldrOffset})");

                    if (ldrExpr != null && ldrExpr.IsValidValue &&
                        ldrExpr.Value != "0x0000000000000000" && ldrExpr.Value != "0x00000000")
                    {
                        // Walk InMemoryOrderModuleList
                        // InMemoryOrderModuleList is at offset 0x20 for 64-bit, 0x14 for 32-bit within PEB_LDR_DATA
                        int listOffset = is64Bit ? 0x20 : 0x14;
                        
                        // Get list head (Flink pointer)
                        var listHeadExpr = debugger.GetExpression($"*(void**)({ldrExpr.Value} + {listOffset})");
                        
                        if (listHeadExpr != null && listHeadExpr.IsValidValue &&
                            listHeadExpr.Value != "0x0000000000000000" && listHeadExpr.Value != "0x00000000")
                        {
                            ulong listHead = ParseAddress(listHeadExpr.Value);
                            ulong currentEntry = listHead;
                            int maxModules = 500; // Safety limit
                            int count = 0;
                            
                            while (count < maxModules)
                            {
                                try
                                {
                                    // LDR_DATA_TABLE_ENTRY offsets (relative to InMemoryOrderLinks):
                                    // 64-bit: DllBase at -0x10, FullDllName at +0x40, BaseDllName at +0x50
                                    // 32-bit: DllBase at -0x08, FullDllName at +0x28, BaseDllName at +0x30
                                    
                                    int dllBaseOffset = is64Bit ? -0x10 : -0x08;
                                    int fullDllNameOffset = is64Bit ? 0x40 : 0x28;
                                    
                                    // Get DllBase
                                    var dllBaseExpr = debugger.GetExpression($"*(void**)(0x{currentEntry:X} + {dllBaseOffset})");
                                    
                                    if (dllBaseExpr != null && dllBaseExpr.IsValidValue &&
                                        dllBaseExpr.Value != "0x0000000000000000" && dllBaseExpr.Value != "0x00000000")
                                    {
                                        string dllBase = dllBaseExpr.Value;
                                        
                                        // Try to get DLL name using GetModuleFileNameA-like approach
                                        // Since we have the base address, we can read the PE header to get the name
                                        string moduleName = GetModuleNameFromBase(debugger, dllBase, is64Bit);
                                        
                                        if (!string.IsNullOrEmpty(moduleName))
                                        {
                                            string systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
                                            string modulePath = moduleName;
                                            
                                            // Construct full path for system modules
                                            if (!modulePath.Contains("\\") && !modulePath.Contains("/"))
                                            {
                                                string system32Path = System.IO.Path.Combine(systemRoot, "System32", moduleName);
                                                if (System.IO.File.Exists(system32Path))
                                                {
                                                    modulePath = system32Path;
                                                }
                                            }
                                            
                                            modules.Add(new ModuleInfo
                                            {
                                                Name = System.IO.Path.GetFileName(moduleName),
                                                Path = modulePath,
                                                Address = dllBase,
                                                SymbolsLoaded = false,
                                                IsSystemModule = IsSystemModule(modulePath),
                                                Is64Bit = is64Bit
                                            });
                                        }
                                    }
                                    
                                    // Get next entry (Flink)
                                    var flinkExpr = debugger.GetExpression($"*(void**)(0x{currentEntry:X})");
                                    if (flinkExpr == null || !flinkExpr.IsValidValue)
                                        break;
                                    
                                    ulong nextEntry = ParseAddress(flinkExpr.Value);
                                    
                                    // Check if we've looped back to the head
                                    if (nextEntry == listHead || nextEntry == 0)
                                        break;
                                    
                                    currentEntry = nextEntry;
                                    count++;
                                }
                                catch (Exception ex)
                                {
                                    _log($"PEB module walk error at entry {count}: {ex.Message}");
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log($"PEB enumeration error: {ex.Message}");
            }

            return modules;
        }
        
        /// <summary>
        /// Try to get module name from its base address by reading PE export directory.
        /// </summary>
        private string GetModuleNameFromBase(Debugger5 debugger, string baseAddress, bool is64Bit)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            
            try
            {
                // Read DOS header - check MZ signature
                var mzExpr = debugger.GetExpression($"*(unsigned short*)({baseAddress})");
                if (mzExpr == null || !mzExpr.IsValidValue)
                    return null;
                
                string mzVal = mzExpr.Value.Trim();
                // MZ = 0x5A4D = 23117
                if (mzVal != "23117" && mzVal.ToLower() != "0x5a4d")
                    return null;
                
                // Read e_lfanew (offset to PE header) at offset 0x3C
                var lfanewExpr = debugger.GetExpression($"*(unsigned int*)({baseAddress} + 0x3C)");
                if (lfanewExpr == null || !lfanewExpr.IsValidValue)
                    return null;
                
                uint lfanew = ParseUInt(lfanewExpr.Value);
                if (lfanew == 0 || lfanew > 0x1000)
                    return null;
                
                // Export directory RVA is at PE + 0x88 (64-bit) or PE + 0x78 (32-bit)
                int exportDirRvaOffset = is64Bit ? 0x88 : 0x78;
                var exportRvaExpr = debugger.GetExpression($"*(unsigned int*)({baseAddress} + {lfanew} + {exportDirRvaOffset})");
                if (exportRvaExpr == null || !exportRvaExpr.IsValidValue)
                    return null;
                
                uint exportRva = ParseUInt(exportRvaExpr.Value);
                if (exportRva == 0)
                    return null;
                
                // Export directory has Name RVA at offset 0x0C
                var nameRvaExpr = debugger.GetExpression($"*(unsigned int*)({baseAddress} + {exportRva} + 0x0C)");
                if (nameRvaExpr == null || !nameRvaExpr.IsValidValue)
                    return null;
                
                uint nameRva = ParseUInt(nameRvaExpr.Value);
                if (nameRva == 0)
                    return null;
                
                // Read the name string (up to 256 chars)
                var nameBuilder = new System.Text.StringBuilder();
                for (int i = 0; i < 256; i++)
                {
                    var charExpr = debugger.GetExpression($"*(char*)({baseAddress} + {nameRva} + {i})");
                    if (charExpr == null || !charExpr.IsValidValue)
                        break;
                    
                    // Parse char value
                    string charVal = charExpr.Value.Trim();
                    int charCode = 0;
                    
                    if (charVal.StartsWith("'") && charVal.EndsWith("'") && charVal.Length >= 3)
                    {
                        charCode = charVal[1];
                    }
                    else if (charVal.StartsWith("0x"))
                    {
                        charCode = Convert.ToInt32(charVal, 16);
                    }
                    else if (int.TryParse(charVal, out int parsed))
                    {
                        charCode = parsed;
                    }
                    
                    if (charCode == 0)
                        break;
                    
                    nameBuilder.Append((char)charCode);
                }
                
                string name = nameBuilder.ToString();
                return string.IsNullOrEmpty(name) ? null : name;
            }
            catch (Exception ex)
            {
                _log($"GetModuleNameFromBase error: {ex.Message}");
                return null;
            }
        }

        private void TryAddCommonSystemModules(Debugger5 debugger, HashSet<string> moduleSet, Dictionary<string, ModuleInfo> moduleInfoDict)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Common system DLLs that are almost always loaded
            string[] commonDlls = new[]
            {
                "ntdll.dll",
                "kernel32.dll",
                "kernelbase.dll",
                "user32.dll",
                "gdi32.dll",
                "advapi32.dll",
                "msvcrt.dll",
                "ucrtbase.dll",
                "vcruntime140.dll",
                "msvcp140.dll"
            };

            foreach (var dllName in commonDlls)
            {
                try
                {
                    var handleExpr = debugger.GetExpression($"(void*)GetModuleHandleA(\"{dllName}\")");
                    if (handleExpr != null && handleExpr.IsValidValue &&
                        handleExpr.Value != "0x0000000000000000" && handleExpr.Value != "0x00000000" &&
                        handleExpr.Value != "0" && !handleExpr.Value.Contains("error"))
                    {
                        // Module is loaded, try to get its full path
                        string fullPath = GetModuleFullPath(debugger, handleExpr.Value, dllName);
                        
                        if (!moduleSet.Contains(fullPath) && !moduleSet.Contains(dllName))
                        {
                            moduleSet.Add(fullPath);
                            var info = new ModuleInfo
                            {
                                Name = dllName,
                                Path = fullPath,
                                Address = handleExpr.Value,
                                SymbolsLoaded = false,
                                IsSystemModule = true,
                                Is64Bit = Is64BitProcess(debugger)
                            };
                            moduleInfoDict[fullPath] = info;
                        }
                    }
                }
                catch
                {
                    // Module not loaded or error - skip
                }
            }
        }

        private string GetModuleFullPath(Debugger5 debugger, string baseAddress, string moduleName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Try to get full path using GetModuleFileNameA
            // This requires a buffer which is complex in expression evaluation
            // For now, return a constructed path for system modules
            
            string systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            
            if (moduleName.ToLower() == "ntdll.dll")
            {
                return System.IO.Path.Combine(systemRoot, "System32", moduleName);
            }
            
            // Check if it's in System32
            string system32Path = System.IO.Path.Combine(systemRoot, "System32", moduleName);
            if (System.IO.File.Exists(system32Path))
            {
                return system32Path;
            }

            // Check SysWOW64 for 32-bit modules on 64-bit OS
            string sysWow64Path = System.IO.Path.Combine(systemRoot, "SysWOW64", moduleName);
            if (System.IO.File.Exists(sysWow64Path))
            {
                return sysWow64Path;
            }

            return moduleName;
        }

        private bool IsSystemModule(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            string lowerPath = path.ToLower();
            return lowerPath.Contains(@"\windows\") ||
                   lowerPath.Contains(@"\system32\") ||
                   lowerPath.Contains(@"\syswow64\") ||
                   lowerPath.Contains(@"\winsxs\");
        }

        private bool Is64BitProcess(Debugger5 debugger)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                // Check if @rip register exists (64-bit) vs @eip (32-bit)
                var ripExpr = debugger.GetExpression("@rip");
                return ripExpr != null && ripExpr.IsValidValue;
            }
            catch
            {
                return Environment.Is64BitOperatingSystem;
            }
        }

        private string GetModuleVersion(string modulePath)
        {
            try
            {
                if (System.IO.File.Exists(modulePath))
                {
                    var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(modulePath);
                    if (!string.IsNullOrEmpty(versionInfo.FileVersion))
                    {
                        return versionInfo.FileVersion;
                    }
                }
            }
            catch (Exception ex)
            {
                _log($"[GetModuleVersion] Error: {ex.Message}");
            }
            return null;
        }

        private string FormatSize(uint bytes)
        {
            if (bytes >= 1024 * 1024)
                return $"{bytes / (1024 * 1024.0):F2} MB";
            if (bytes >= 1024)
                return $"{bytes / 1024.0:F2} KB";
            return $"{bytes} bytes";
        }

        private string EscapeForExpression(string str)
        {
            return str.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        #endregion

        #region Exception

        public ExceptionResult GetException()
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new ExceptionResult { HasException = false };

                if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    return result;
                }

                try
                {
                    // Try to evaluate $exception
                    var exceptionExpr = debugger.GetExpression("$exception");
                    if (exceptionExpr != null && exceptionExpr.IsValidValue)
                    {
                        result.HasException = true;
                        result.Type = exceptionExpr.Type;
                        result.Message = GetExpressionMemberValue(exceptionExpr, "Message");
                        result.StackTrace = GetExpressionMemberValue(exceptionExpr, "StackTrace");
                        result.Source = GetExpressionMemberValue(exceptionExpr, "Source");
                        result.HResult = GetExpressionMemberValue(exceptionExpr, "HResult");

                        // Try to get inner exception
                        var innerExpr = GetExpressionMember(exceptionExpr, "InnerException");
                        if (innerExpr != null && innerExpr.IsValidValue && innerExpr.Value != "null")
                        {
                            result.InnerException = new ExceptionResult
                            {
                                HasException = true,
                                Type = innerExpr.Type,
                                Message = GetExpressionMemberValue(innerExpr, "Message")
                            };
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log($"Error getting exception: {ex.Message}");
                }

                return result;
            });
        }

        private string GetExpressionMemberValue(EnvDTE.Expression expr, string memberName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                foreach (EnvDTE.Expression member in expr.DataMembers)
                {
                    if (member.Name == memberName)
                    {
                        return member.Value?.Trim('"') ?? "";
                    }
                }
            }
            catch (Exception ex)
            {
                _log($"[GetExpressionMemberValue] Error accessing member '{memberName}': {ex.Message}");
            }
            return "";
        }

        private EnvDTE.Expression GetExpressionMember(EnvDTE.Expression expr, string memberName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                foreach (EnvDTE.Expression member in expr.DataMembers)
                {
                    if (member.Name == memberName)
                    {
                        return member;
                    }
                }
            }
            catch (Exception ex)
            {
                _log($"[GetExpressionMember] Error accessing member '{memberName}': {ex.Message}");
            }
            return null;
        }

        #endregion

        #region Expression Evaluation

        public EvaluateResult EvaluateExpression(string expression, bool expandChildren = false, int maxDepth = 2)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new EvaluateResult { Expression = expression };

                if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    result.IsValid = false;
                    result.ErrorMessage = "Not in break mode";
                    return result;
                }

                try
                {
                    var expr = debugger.GetExpression(expression);
                    result.IsValid = expr.IsValidValue;
                    result.Value = expr.Value;
                    result.Type = expr.Type;
                    result.HasChildren = expr.DataMembers?.Count > 0;

                    if (!result.IsValid)
                    {
                        result.ErrorMessage = expr.Value;
                    }

                    // Try to get hex value for numeric types
                    if (result.IsValid && IsNumericType(expr.Type))
                    {
                        try
                        {
                            var hexExpr = debugger.GetExpression($"(void*){expression}");
                            if (hexExpr.IsValidValue)
                            {
                                result.HexValue = hexExpr.Value;
                            }
                        }
                        catch (Exception ex)
                        {
                            _log($"[EvaluateExpression] Error getting hex value for '{expression}': {ex.Message}");
                        }
                    }

                    // Expand children if requested
                    if (expandChildren && result.HasChildren)
                    {
                        result.Children = new List<VariableInfo>();
                        foreach (EnvDTE.Expression child in expr.DataMembers)
                        {
                            result.Children.Add(CreateVariableInfo(child, 0, maxDepth));
                        }
                    }
                }
                catch (Exception ex)
                {
                    result.IsValid = false;
                    result.ErrorMessage = ex.Message;
                }

                return result;
            });
        }

        private bool IsNumericType(string type)
        {
            var numericTypes = new[] { "int", "uint", "long", "ulong", "short", "ushort", "byte", "sbyte", "IntPtr", "UIntPtr", "nint", "nuint" };
            return numericTypes.Any(t => type.Contains(t));
        }

        #endregion

        #region Memory

        public MemoryResult ReadMemory(string address, int size = 256)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new MemoryResult { Address = address, Size = size };

                if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    return result;
                }

                try
                {
                    // First, evaluate the address to get numeric value
                    var addrExpr = debugger.GetExpression(address);
                    if (!addrExpr.IsValidValue)
                    {
                        return result;
                    }

                    // Read memory bytes
                    var bytes = new List<byte>();
                    var hexBuilder = new StringBuilder();
                    var asciiBuilder = new StringBuilder();

                    // Read in chunks of 16 bytes
                    int bytesPerLine = 16;
                    int lines = (size + bytesPerLine - 1) / bytesPerLine;
                    var formattedBuilder = new StringBuilder();

                    for (int line = 0; line < lines; line++)
                    {
                        int offset = line * bytesPerLine;
                        int count = Math.Min(bytesPerLine, size - offset);

                        // Read each byte
                        var lineHex = new StringBuilder();
                        var lineAscii = new StringBuilder();

                        for (int i = 0; i < count; i++)
                        {
                            try
                            {
                                string byteExpr = $"*(unsigned char*)({address} + {offset + i})";
                                var byteResult = debugger.GetExpression(byteExpr);
                                if (byteResult.IsValidValue)
                                {
                                    // Parse the byte value
                                    string valStr = byteResult.Value.Trim();
                                    byte b = 0;
                                    if (valStr.StartsWith("0x"))
                                    {
                                        b = Convert.ToByte(valStr, 16);
                                    }
                                    else if (byte.TryParse(valStr, out byte parsed))
                                    {
                                        b = parsed;
                                    }

                                    bytes.Add(b);
                                    lineHex.AppendFormat("{0:X2} ", b);
                                    lineAscii.Append(b >= 32 && b < 127 ? (char)b : '.');
                                }
                                else
                                {
                                    lineHex.Append("?? ");
                                    lineAscii.Append('?');
                                }
                            }
                            catch
                            {
                                lineHex.Append("?? ");
                                lineAscii.Append('?');
                            }
                        }

                        // Pad hex to full width
                        while (lineHex.Length < bytesPerLine * 3)
                        {
                            lineHex.Append("   ");
                        }

                        hexBuilder.Append(lineHex);
                        asciiBuilder.Append(lineAscii);

                        // Add formatted line
                        formattedBuilder.AppendFormat("{0}+{1:X4}: {2} | {3}\n",
                            result.Address, offset, lineHex.ToString().TrimEnd(), lineAscii.ToString());
                    }

                    result.Bytes = bytes;
                    result.HexDump = hexBuilder.ToString().Trim();
                    result.AsciiView = asciiBuilder.ToString();
                    result.FormattedView = formattedBuilder.ToString();
                }
                catch (Exception ex)
                {
                    _log($"Error reading memory: {ex.Message}");
                }

                return result;
            });
        }

        #endregion

        #region Disassembly

        /// <summary>
        /// Get disassembly around the current instruction pointer using the Iced library.
        /// Returns properly decoded x86/x64 assembly instructions.
        /// </summary>
        public DisassemblyResult GetDisassembly(int linesBefore = 20, int linesAfter = 20)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new DisassemblyResult();

                if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    result.Error = "Not in break mode";
                    return result;
                }

                try
                {
                    var frame = debugger.CurrentStackFrame;
                    if (frame != null)
                    {
                        result.FunctionName = frame.FunctionName;
                        result.SourceFile = GetFrameFileName(frame);

                        // Determine if 64-bit by checking for RIP register
                        var ripExpr = debugger.GetExpression("@rip");
                        bool is64Bit = ripExpr.IsValidValue;
                        var ipExpr = is64Bit ? ripExpr : debugger.GetExpression("@eip");
                        result.Is64Bit = is64Bit;

                        if (ipExpr.IsValidValue)
                        {
                            ulong currentIP = ParseAddress(ipExpr.Value);
                            result.CurrentAddress = is64Bit
                                ? $"0x{currentIP:X16}"
                                : $"0x{currentIP:X8}";

                            // Calculate bytes to read
                            // x86/x64 instructions average ~4 bytes, max 15 bytes
                            // Read extra to ensure we have enough data for proper disassembly
                            int bytesBeforeIP = linesBefore * 8;
                            int bytesAfterIP = linesAfter * 8;
                            int totalBytes = bytesBeforeIP + bytesAfterIP;

                            ulong startAddress = currentIP > (ulong)bytesBeforeIP
                                ? currentIP - (ulong)bytesBeforeIP
                                : 0;

                            // Read memory block using optimized batch reading
                            byte[] codeBytes = ReadMemoryBlockOptimized(debugger, startAddress, totalBytes);

                            // Fallback: if reading from calculated start failed, try reading from current IP
                            ulong actualStartAddress = startAddress;
                            if ((codeBytes == null || codeBytes.Length == 0) && startAddress != currentIP)
                            {
                                _log($"GetDisassembly: Failed to read from {startAddress:X}, trying from current IP {currentIP:X}");
                                actualStartAddress = currentIP;
                                codeBytes = ReadMemoryBlockOptimized(debugger, currentIP, bytesAfterIP);
                            }

                            if (codeBytes != null && codeBytes.Length > 0)
                            {
                                // Disassemble using Iced
                                var instructions = _disasmService.Disassemble(
                                    codeBytes, actualStartAddress, is64Bit, linesBefore + linesAfter + 50);

                                // Find current IP index
                                int currentIndex = -1;
                                for (int i = 0; i < instructions.Count; i++)
                                {
                                    if (instructions[i].Address == currentIP)
                                    {
                                        currentIndex = i;
                                        break;
                                    }
                                    if (instructions[i].Address > currentIP && i > 0)
                                    {
                                        currentIndex = i - 1;
                                        break;
                                    }
                                }

                                if (currentIndex == -1 && instructions.Count > 0)
                                {
                                    // Current IP not found exactly, use first instruction if we started from currentIP
                                    currentIndex = (actualStartAddress == currentIP) ? 0 : instructions.Count / 2;
                                }

                                // Extract instructions around current IP
                                int startIndex = Math.Max(0, currentIndex - linesBefore);
                                int endIndex = Math.Min(instructions.Count, currentIndex + linesAfter + 1);

                                // If we fell back to reading from currentIP, adjust startIndex
                                if (actualStartAddress == currentIP)
                                {
                                    startIndex = 0;
                                }

                                int sourceLine = GetFrameLineNumber(frame);

                                for (int i = startIndex; i < endIndex; i++)
                                {
                                    var inst = instructions[i];
                                    bool isCurrent = inst.Address == currentIP;

                                    result.Instructions.Add(new DisassemblyInstruction
                                    {
                                        Address = inst.AddressHex,
                                        Opcode = inst.Bytes,
                                        Instruction = inst.FullInstruction,
                                        IsCurrent = isCurrent,
                                        SourceLine = isCurrent ? (int?)sourceLine : null,
                                        Mnemonic = inst.Mnemonic,
                                        Operands = inst.Operands,
                                        IsCall = inst.IsCall,
                                        IsJump = inst.IsJump,
                                        IsReturn = inst.IsReturn,
                                        BranchTarget = inst.BranchTarget?.ToString("X"),
                                        Length = inst.Length
                                    });
                                }

                                result.TotalInstructions = result.Instructions.Count;
                            }
                            else
                            {
                                result.Error = "Could not read memory at current instruction pointer. This may occur when debugging optimized code without symbols.";
                            }
                        }
                        else
                        {
                            result.Error = "Could not determine instruction pointer";
                        }
                    }
                    else
                    {
                        result.Error = "No current stack frame";
                    }
                }
                catch (Exception ex)
                {
                    _log($"Error getting disassembly: {ex.Message}");
                    result.Error = ex.Message;
                }

                return result;
            });
        }

        /// <summary>
        /// Get disassembly starting at a specific address using the Iced library.
        /// Automatically handles thread context issues by trying to switch to user code thread if needed.
        /// </summary>
        public DisassemblyResult GetDisassemblyAt(string address, int count = 50)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new DisassemblyResult();

                if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    result.Error = "Not in break mode";
                    return result;
                }

                // Save current thread in case we need to restore
                int originalThreadId = debugger.CurrentThread?.ID ?? 0;
                bool switchedThread = false;

                try
                {
                    // Try to execute first
                    result = ExecuteDisassemblyAt(debugger, address, count, result);
                    
                    // If first attempt failed (has error or no instructions), try switching to user code thread
                    if (!string.IsNullOrEmpty(result.Error) || result.Instructions.Count == 0)
                    {
                        string firstError = result.Error ?? "No instructions retrieved";
                        _log($"GetDisassemblyAt first attempt failed: {firstError}");

                        var userThread = FindUserCodeThread(debugger);
                        if (userThread != null && userThread.ID != originalThreadId)
                        {
                            _log($"Switching from thread {originalThreadId} to user code thread {userThread.ID}");
                            debugger.CurrentThread = userThread;
                            switchedThread = true;

                            // Reset result and retry
                            result = new DisassemblyResult();
                            result = ExecuteDisassemblyAt(debugger, address, count, result);
                            
                            // If retry also failed, restore the original error
                            if (!string.IsNullOrEmpty(result.Error) || result.Instructions.Count == 0)
                            {
                                _log($"GetDisassemblyAt retry also failed: {result.Error}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log($"GetDisassemblyAt exception: {ex.Message}");
                    result.Error = ex.Message;
                }
                finally
                {
                    // Restore original thread if we switched
                    if (switchedThread && originalThreadId > 0)
                    {
                        try
                        {
                            foreach (EnvDTE.Thread thread in debugger.CurrentProgram.Threads)
                            {
                                if (thread.ID == originalThreadId)
                                {
                                    debugger.CurrentThread = thread;
                                    break;
                                }
                            }
                        }
                        catch (Exception restoreEx)
                        {
                            _log($"Failed to restore original thread: {restoreEx.Message}");
                        }
                    }
                }

                return result;
            });
        }

        /// <summary>
        /// Internal method to execute disassembly at a specific address.
        /// </summary>
        private DisassemblyResult ExecuteDisassemblyAt(Debugger5 debugger, string address, int count, DisassemblyResult result)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Evaluate address expression
            var addrExpr = debugger.GetExpression(address);
            if (!addrExpr.IsValidValue)
            {
                result.Error = $"Invalid address expression: {address}";
                return result;
            }

            ulong baseAddr = ParseAddress(addrExpr.Value);

            // Determine if 64-bit
            var ripExpr = debugger.GetExpression("@rip");
            bool is64Bit = ripExpr != null && ripExpr.IsValidValue;
            result.Is64Bit = is64Bit;

            result.CurrentAddress = is64Bit
                ? $"0x{baseAddr:X16}"
                : $"0x{baseAddr:X8}";

            // Read enough bytes for the requested instruction count
            // Average instruction is ~4 bytes, max is 15, so read count * 8 bytes
            int bytesToRead = count * 8;

            byte[] codeBytes = ReadMemoryBlockOptimized(debugger, baseAddr, bytesToRead);

            if (codeBytes != null && codeBytes.Length > 0)
            {
                // Disassemble using Iced
                var instructions = _disasmService.Disassemble(codeBytes, baseAddr, is64Bit, count);

                // Get current IP to mark current instruction
                var ipExpr = is64Bit ? ripExpr : debugger.GetExpression("@eip");
                ulong currentIP = ipExpr != null && ipExpr.IsValidValue
                    ? ParseAddress(ipExpr.Value)
                    : 0;

                foreach (var inst in instructions)
                {
                    bool isCurrent = inst.Address == currentIP;

                    result.Instructions.Add(new DisassemblyInstruction
                    {
                        Address = inst.AddressHex,
                        Opcode = inst.Bytes,
                        Instruction = inst.FullInstruction,
                        IsCurrent = isCurrent,
                        Mnemonic = inst.Mnemonic,
                        Operands = inst.Operands,
                        IsCall = inst.IsCall,
                        IsJump = inst.IsJump,
                        IsReturn = inst.IsReturn,
                        BranchTarget = inst.BranchTarget?.ToString("X"),
                        Length = inst.Length
                    });
                }

                result.TotalInstructions = result.Instructions.Count;
            }
            else
            {
                result.Error = $"Could not read memory at {address}";
            }

            return result;
        }

        /// <summary>
        /// Find a thread that is in user code context (not system DLL).
        /// Prefers threads in user's exe/dll over system threads.
        /// </summary>
        private EnvDTE.Thread FindUserCodeThread(Debugger5 debugger)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            EnvDTE.Thread bestThread = null;
            bool foundUserCode = false;

            try
            {
                foreach (EnvDTE.Thread thread in debugger.CurrentProgram.Threads)
                {
                    try
                    {
                        if (thread.StackFrames.Count > 0)
                        {
                            var topFrame = thread.StackFrames.Item(1) as EnvDTE.StackFrame;
                            if (topFrame != null)
                            {
                                string modulePath = GetFrameFileName(topFrame);
                                
                                // Check if this is user code (not system DLL)
                                bool isUserCode = !string.IsNullOrEmpty(modulePath) &&
                                    !modulePath.StartsWith("C:\\Windows", StringComparison.OrdinalIgnoreCase) &&
                                    modulePath.IndexOf("\\System32\\", StringComparison.OrdinalIgnoreCase) < 0 &&
                                    modulePath.IndexOf("\\SysWOW64\\", StringComparison.OrdinalIgnoreCase) < 0;

                                if (isUserCode && !foundUserCode)
                                {
                                    bestThread = thread;
                                    foundUserCode = true;
                                }
                                else if (!foundUserCode && bestThread == null)
                                {
                                    // Keep first thread as fallback
                                    bestThread = thread;
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Skip threads we can't inspect
                    }
                }
            }
            catch (Exception ex)
            {
                _log($"FindUserCodeThread error: {ex.Message}");
            }

            return bestThread;
        }

        /// <summary>
        /// Optimized memory reading - reads in larger chunks to reduce IPC calls.
        /// Uses 4-byte reads instead of single-byte reads for better performance.
        /// </summary>
        private byte[] ReadMemoryBlockOptimized(EnvDTE.Debugger debugger, ulong address, int size)
        {
            var result = new List<byte>();

            // Read in 4-byte chunks (DWORD at a time)
            for (int offset = 0; offset < size; offset += 4)
            {
                try
                {
                    var expr = debugger.GetExpression($"*(unsigned int*)(0x{address + (ulong)offset:X})");

                    if (!expr.IsValidValue)
                        break;

                    uint value = ParseUInt(expr.Value);
                    var bytes = BitConverter.GetBytes(value);

                    // Only add the bytes we need (handle end of range)
                    int bytesNeeded = Math.Min(4, size - offset);
                    for (int b = 0; b < bytesNeeded; b++)
                        result.Add(bytes[b]);
                }
                catch (Exception ex)
                {
                    _log($"ReadMemoryBlockOptimized: Error at offset {offset}: {ex.Message}");
                    break;
                }
            }

            return result.ToArray();
        }

        /// <summary>
        /// Parse unsigned int from debugger expression value.
        /// </summary>
        private uint ParseUInt(string value)
        {
            if (string.IsNullOrEmpty(value))
                return 0;

            value = value.Trim();

            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return Convert.ToUInt32(value.Substring(2), 16);

            if (uint.TryParse(value, out uint result))
                return result;

            return 0;
        }

        /// <summary>
        /// Parse an address string to a ulong value.
        /// Handles both hex (with 0x prefix) and decimal formats.
        /// </summary>
        private ulong ParseAddress(string addrStr)
        {
            if (string.IsNullOrEmpty(addrStr))
                return 0;

            addrStr = addrStr.Trim();

            // Handle hex with 0x prefix
            if (addrStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                string hexPart = addrStr.Substring(2);
                // Handle addresses with extra info like "0x7ff712b45a94 {Module!Function}"
                int spaceIdx = hexPart.IndexOf(' ');
                if (spaceIdx > 0)
                    hexPart = hexPart.Substring(0, spaceIdx);
                
                if (ulong.TryParse(hexPart, System.Globalization.NumberStyles.HexNumber, null, out ulong hexAddr))
                {
                    return hexAddr;
                }
            }

            // Try to parse as decimal first (VS debugger often returns decimal values)
            if (ulong.TryParse(addrStr, out ulong decAddr))
            {
                return decAddr;
            }

            // Fallback: try to parse as pure hex without prefix (for edge cases)
            if (ulong.TryParse(addrStr, System.Globalization.NumberStyles.HexNumber, null, out ulong fallbackAddr))
            {
                return fallbackAddr;
            }

            return 0;
        }

        #endregion

        #region Build Errors

        public BuildErrorsResult GetBuildErrors(bool errorsOnly = false, bool warningsOnly = false)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var result = new BuildErrorsResult();

                try
                {
                    var errorList = _dte.ToolWindows.ErrorList;
                    var errorItems = errorList.ErrorItems;

                    for (int i = 1; i <= errorItems.Count; i++)
                    {
                        var item = errorItems.Item(i);
                        bool isError = item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelHigh;
                        bool isWarning = item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelMedium ||
                                        item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelLow;

                        if (errorsOnly && !isError) continue;
                        if (warningsOnly && !isWarning) continue;

                        if (isError) result.ErrorCount++;
                        if (isWarning) result.WarningCount++;

                        // Extract error code from description
                        string errorCode = ExtractErrorCode(item.Description);

                        result.Items.Add(new BuildErrorItem
                        {
                            Severity = isError ? "Error" : "Warning",
                            Code = errorCode,
                            Description = item.Description,
                            FilePath = item.FileName,
                            Line = item.Line,
                            Column = item.Column,
                            Project = item.Project
                        });
                    }
                }
                catch (Exception ex)
                {
                    _log($"Error getting build errors: {ex.Message}");
                }

                return result;
            });
        }

        /// <summary>
        /// Extract error code from error description.
        /// Handles formats like:
        /// - "CS0001: Description..."
        /// - "error CS0001: Description..."
        /// - "warning CA1234: Description..."
        /// - "C1234: Description..."
        /// </summary>
        /// <summary>
        /// Known error code prefixes for Visual Studio and common analyzers.
        /// </summary>
        private static readonly string[] KnownErrorCodePrefixes = new[]
        {
            "CS",    // C# compiler errors
            "CA",    // Code Analysis warnings
            "IDE",   // IDE analyzers
            "BC",    // Visual Basic compiler
            "NU",    // NuGet errors
            "SA",    // StyleCop analyzers
            "RCS",   // Roslynator analyzers
            "S",     // SonarQube analyzers
            "DOC",   // Documentation analyzers
            "C",     // C/C++ compiler errors
            "LNK",   // Linker errors
            "MSB",   // MSBuild errors
            "NETSDK" // .NET SDK errors
        };

        private string ExtractErrorCode(string description)
        {
            if (string.IsNullOrEmpty(description))
                return "";

            // Method 1: Try to match at the beginning with optional error/warning prefix
            var match = Regex.Match(
                description,
                @"^(?:error\s+|warning\s+)?([A-Z]{1,6}\d{4,5}):",
                RegexOptions.IgnoreCase);

            if (match.Success)
            {
                string code = match.Groups[1].Value.ToUpperInvariant();
                // Verify it starts with a known prefix
                if (IsKnownErrorCode(code))
                {
                    return code;
                }
            }

            // Method 2: Search for known prefixes specifically (more reliable)
            foreach (var prefix in KnownErrorCodePrefixes)
            {
                var prefixMatch = Regex.Match(
                    description,
                    $@"\b({Regex.Escape(prefix)}\d{{4,5}})\b",
                    RegexOptions.IgnoreCase);

                if (prefixMatch.Success)
                {
                    return prefixMatch.Groups[1].Value.ToUpperInvariant();
                }
            }

            // Method 3: Fallback - try any code-like pattern at the beginning
            match = Regex.Match(
                description,
                @"^(?:error\s+|warning\s+)?([A-Z]{1,3}\d{4,5}):",
                RegexOptions.IgnoreCase);

            if (match.Success)
            {
                return match.Groups[1].Value.ToUpperInvariant();
            }

            return "";
        }

        /// <summary>
        /// Check if the given code starts with a known error code prefix.
        /// </summary>
        private bool IsKnownErrorCode(string code)
        {
            if (string.IsNullOrEmpty(code))
                return false;

            foreach (var prefix in KnownErrorCodePrefixes)
            {
                if (code.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        #endregion

        #region Full Context

        public FullContextResult GetFullContext()
        {
            return new FullContextResult
            {
                Status = GetDebugStatus(),
                Callstack = GetCallStack(),
                Locals = GetLocals(),
                Exception = GetException(),
                Threads = GetThreads(),
                Modules = GetModules()
            };
        }

        #endregion

        #region Instance Info

        /// <summary>
        /// Gets information about this Visual Studio instance for multi-instance support.
        /// </summary>
        public InstanceInfoResult GetInstanceInfo(string pipeName)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                
                var result = new InstanceInfoResult
                {
                    ProcessId = System.Diagnostics.Process.GetCurrentProcess().Id,
                    PipeName = pipeName,
                    IsDebugging = debugger.CurrentMode != dbgDebugMode.dbgDesignMode,
                    IsPaused = debugger.CurrentMode == dbgDebugMode.dbgBreakMode,
                    DebugMode = GetDebugModeString(debugger.CurrentMode)
                };

                // Get solution info
                try
                {
                    if (_dte.Solution != null && !string.IsNullOrEmpty(_dte.Solution.FullName))
                    {
                        result.SolutionPath = _dte.Solution.FullName;
                        result.SolutionName = System.IO.Path.GetFileNameWithoutExtension(_dte.Solution.FullName);
                    }
                    else
                    {
                        result.SolutionName = "(No solution open)";
                    }
                }
                catch
                {
                    result.SolutionName = "(Unknown)";
                }

                // Get current process name if debugging
                if (result.IsDebugging && debugger.CurrentProcess != null)
                {
                    try
                    {
                        result.CurrentProcess = debugger.CurrentProcess.Name;
                    }
                    catch (Exception ex)
                    {
                        _log($"[GetInstanceInfo] Error getting current process: {ex.Message}");
                    }
                }

                // Get VS version
                try
                {
                    result.VsVersion = _dte.Version;
                }
                catch (Exception ex)
                {
                    _log($"[GetInstanceInfo] Error getting VS version: {ex.Message}");
                }

                return result;
            });
        }

        #endregion

        #region Debug Control

        /// <summary>
        /// Continue execution (F5).
        /// </summary>
        public DebugControlResult Continue()
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new DebugControlResult { Action = "Continue" };

                if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    result.Success = false;
                    result.Message = "Not in break mode";
                    return result;
                }

                try
                {
                    debugger.Go(false); // false = don't wait for break
                    result.Success = true;
                    result.Message = "Execution continued";
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        /// <summary>
        /// Step into (F11).
        /// </summary>
        public DebugControlResult StepInto()
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new DebugControlResult { Action = "StepInto" };

                if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    result.Success = false;
                    result.Message = "Not in break mode";
                    return result;
                }

                try
                {
                    debugger.StepInto(false);
                    result.Success = true;
                    result.Message = "Stepped into";
                    
                    // Get new location after step
                    System.Threading.Thread.Sleep(100); // Brief wait for debugger to settle
                    if (debugger.CurrentStackFrame != null)
                    {
                        result.NewLocation = new LocationInfo
                        {
                            FunctionName = debugger.CurrentStackFrame.FunctionName,
                            FilePath = GetFrameFileName(debugger.CurrentStackFrame),
                            LineNumber = GetFrameLineNumber(debugger.CurrentStackFrame)
                        };
                    }
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        /// <summary>
        /// Step over (F10).
        /// </summary>
        public DebugControlResult StepOver()
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new DebugControlResult { Action = "StepOver" };

                if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    result.Success = false;
                    result.Message = "Not in break mode";
                    return result;
                }

                try
                {
                    debugger.StepOver(false);
                    result.Success = true;
                    result.Message = "Stepped over";
                    
                    System.Threading.Thread.Sleep(100);
                    if (debugger.CurrentStackFrame != null)
                    {
                        result.NewLocation = new LocationInfo
                        {
                            FunctionName = debugger.CurrentStackFrame.FunctionName,
                            FilePath = GetFrameFileName(debugger.CurrentStackFrame),
                            LineNumber = GetFrameLineNumber(debugger.CurrentStackFrame)
                        };
                    }
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        /// <summary>
        /// Step out (Shift+F11).
        /// </summary>
        public DebugControlResult StepOut()
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new DebugControlResult { Action = "StepOut" };

                if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    result.Success = false;
                    result.Message = "Not in break mode";
                    return result;
                }

                try
                {
                    debugger.StepOut(false);
                    result.Success = true;
                    result.Message = "Stepped out";
                    
                    System.Threading.Thread.Sleep(100);
                    if (debugger.CurrentStackFrame != null)
                    {
                        result.NewLocation = new LocationInfo
                        {
                            FunctionName = debugger.CurrentStackFrame.FunctionName,
                            FilePath = GetFrameFileName(debugger.CurrentStackFrame),
                            LineNumber = GetFrameLineNumber(debugger.CurrentStackFrame)
                        };
                    }
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        /// <summary>
        /// Break execution (Ctrl+Alt+Break).
        /// </summary>
        public DebugControlResult Break()
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new DebugControlResult { Action = "Break" };

                if (debugger.CurrentMode != dbgDebugMode.dbgRunMode)
                {
                    result.Success = false;
                    result.Message = "Not running";
                    return result;
                }

                try
                {
                    debugger.Break(false);
                    result.Success = true;
                    result.Message = "Execution broken";
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        /// <summary>
        /// Stop debugging (Shift+F5).
        /// </summary>
        public DebugControlResult Stop()
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new DebugControlResult { Action = "Stop" };

                if (debugger.CurrentMode == dbgDebugMode.dbgDesignMode)
                {
                    result.Success = false;
                    result.Message = "Not debugging";
                    return result;
                }

                try
                {
                    debugger.Stop(false);
                    result.Success = true;
                    result.Message = "Debugging stopped";
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        /// <summary>
        /// Run to cursor location.
        /// </summary>
        public DebugControlResult RunToCursor(string filePath, int lineNumber)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new DebugControlResult { Action = "RunToCursor" };

                // Validate target location
                var validation = ValidateTargetLocation(filePath, lineNumber);
                if (!validation.IsValid)
                {
                    result.Success = false;
                    result.Message = validation.ErrorMessage;
                    return result;
                }

                try
                {
                    // Open the file and set cursor
                    var window = _dte.ItemOperations.OpenFile(filePath);
                    if (window != null)
                    {
                        var textDoc = window.Document?.Object("TextDocument") as EnvDTE.TextDocument;
                        if (textDoc != null)
                        {
                            var editPoint = textDoc.StartPoint.CreateEditPoint();
                            editPoint.MoveToLineAndOffset(lineNumber, 1);
                            textDoc.Selection.MoveToPoint(editPoint);
                        }
                    }

                    debugger.RunToCursor(false);
                    result.Success = true;
                    result.Message = $"Running to {filePath}:{lineNumber}";
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        /// <summary>
        /// Set next statement (move instruction pointer).
        /// </summary>
        public DebugControlResult SetNextStatement(string filePath, int lineNumber)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new DebugControlResult { Action = "SetNextStatement" };

                if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    result.Success = false;
                    result.Message = "Not in break mode";
                    return result;
                }

                // Validate target location
                var validation = ValidateTargetLocation(filePath, lineNumber);
                if (!validation.IsValid)
                {
                    result.Success = false;
                    result.Message = validation.ErrorMessage;
                    return result;
                }

                try
                {
                    // Open the file and set cursor position
                    var window = _dte.ItemOperations.OpenFile(filePath);
                    if (window != null)
                    {
                        var textDoc = window.Document?.Object("TextDocument") as EnvDTE.TextDocument;
                        if (textDoc != null)
                        {
                            var editPoint = textDoc.StartPoint.CreateEditPoint();
                            editPoint.MoveToLineAndOffset(lineNumber, 1);
                            textDoc.Selection.MoveToPoint(editPoint);
                        }
                    }

                    debugger.SetNextStatement();
                    result.Success = true;
                    result.Message = $"Next statement set to {filePath}:{lineNumber}";
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        /// <summary>
        /// Validate that the target file and line are valid execution targets.
        /// </summary>
        private (bool IsValid, string ErrorMessage) ValidateTargetLocation(string filePath, int lineNumber)
        {
            // Check if file exists
            if (string.IsNullOrEmpty(filePath))
            {
                return (false, "File path is required");
            }

            if (!System.IO.File.Exists(filePath))
            {
                return (false, $"File not found: {filePath}");
            }

            // Read file lines
            string[] lines;
            try
            {
                lines = System.IO.File.ReadAllLines(filePath);
            }
            catch (Exception ex)
            {
                return (false, $"Cannot read file: {ex.Message}");
            }

            // Check line number validity
            if (lineNumber < 1)
            {
                return (false, "Line number must be >= 1");
            }

            if (lineNumber > lines.Length)
            {
                return (false, $"Line {lineNumber} is beyond end of file (file has {lines.Length} lines)");
            }

            // Get the target line content
            string targetLine = lines[lineNumber - 1].Trim();

            // Check for empty line
            if (string.IsNullOrEmpty(targetLine))
            {
                return (false, $"Line {lineNumber} is empty - cannot set execution point on empty line");
            }

            // Check for common non-executable patterns
            // Note: These are heuristics and may have false positives/negatives
            string extension = System.IO.Path.GetExtension(filePath).ToLowerInvariant();

            // C# / C++ style comments
            if (targetLine.StartsWith("//"))
            {
                return (false, $"Line {lineNumber} is a comment - cannot set execution point on comment line");
            }

            // Check for opening/closing braces only
            if (targetLine == "{" || targetLine == "}" || targetLine == "{}" ||
                targetLine == "};")
            {
                // Allow brace-only lines - they can be valid execution points in some cases
                // Just warn but don't reject
            }

            // C# specific: check for using statements, namespace declarations, etc.
            if (extension == ".cs")
            {
                if (targetLine.StartsWith("using ") && targetLine.EndsWith(";"))
                {
                    return (false, $"Line {lineNumber} is a using directive - cannot set execution point on using statement");
                }

                if (targetLine.StartsWith("namespace "))
                {
                    return (false, $"Line {lineNumber} is a namespace declaration - cannot set execution point on namespace");
                }
            }

            return (true, null);
        }

        #endregion

        #region Breakpoints

        /// <summary>
        /// Get all breakpoints.
        /// </summary>
        public BreakpointsResult GetBreakpoints()
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new BreakpointsResult();

                try
                {
                    foreach (EnvDTE.Breakpoint bp in debugger.Breakpoints)
                    {
                        result.Breakpoints.Add(new BreakpointInfo
                        {
                            Name = bp.Name,
                            FilePath = bp.File,
                            LineNumber = bp.FileLine,
                            Column = bp.FileColumn,
                            FunctionName = bp.FunctionName,
                            Enabled = bp.Enabled,
                            Condition = bp.Condition,
                            ConditionType = bp.ConditionType.ToString(),
                            HitCount = bp.CurrentHits,
                            HitCountTarget = bp.HitCountTarget,
                            HitCountType = bp.HitCountType.ToString(),
                            BreakpointType = GetBreakpointTypeString(bp.LocationType),
                            Tag = bp.Tag
                        });
                    }
                    result.TotalCount = result.Breakpoints.Count;
                }
                catch (Exception ex)
                {
                    result.Error = ex.Message;
                }

                return result;
            });
        }

        /// <summary>
        /// Set a breakpoint at a file location.
        /// </summary>
        public BreakpointOperationResult SetBreakpoint(string filePath, int lineNumber, string condition = null)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new BreakpointOperationResult { Operation = "Set" };

                try
                {
                    EnvDTE.Breakpoints bps;
                    if (!string.IsNullOrEmpty(condition))
                    {
                        bps = debugger.Breakpoints.Add("", filePath, lineNumber, 1, condition, 
                            dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenTrue);
                    }
                    else
                    {
                        bps = debugger.Breakpoints.Add("", filePath, lineNumber);
                    }

                    if (bps != null && bps.Count > 0)
                    {
                        var bp = bps.Item(1);
                        result.Success = true;
                        result.Message = $"Breakpoint set at {filePath}:{lineNumber}";
                        result.BreakpointInfo = new BreakpointInfo
                        {
                            Name = bp.Name,
                            FilePath = bp.File,
                            LineNumber = bp.FileLine,
                            Enabled = bp.Enabled
                        };
                    }
                    else
                    {
                        result.Success = false;
                        result.Message = "Failed to set breakpoint";
                    }
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        /// <summary>
        /// Set a breakpoint at a function.
        /// </summary>
        public BreakpointOperationResult SetFunctionBreakpoint(string functionName, string condition = null)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new BreakpointOperationResult { Operation = "SetFunction" };

                try
                {
                    EnvDTE.Breakpoints bps;
                    if (!string.IsNullOrEmpty(condition))
                    {
                        bps = debugger.Breakpoints.Add(functionName, "", 0, 0, condition,
                            dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenTrue);
                    }
                    else
                    {
                        bps = debugger.Breakpoints.Add(functionName);
                    }

                    if (bps != null && bps.Count > 0)
                    {
                        var bp = bps.Item(1);
                        result.Success = true;
                        result.Message = $"Function breakpoint set on {functionName}";
                        result.BreakpointInfo = new BreakpointInfo
                        {
                            Name = bp.Name,
                            FunctionName = bp.FunctionName,
                            Enabled = bp.Enabled
                        };
                    }
                    else
                    {
                        result.Success = false;
                        result.Message = "Failed to set function breakpoint";
                    }
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        /// <summary>
        /// Delete a breakpoint.
        /// </summary>
        public BreakpointOperationResult DeleteBreakpoint(string filePath, int lineNumber)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new BreakpointOperationResult { Operation = "Delete" };

                try
                {
                    bool deleted = false;
                    foreach (EnvDTE.Breakpoint bp in debugger.Breakpoints)
                    {
                        if (bp.File == filePath && bp.FileLine == lineNumber)
                        {
                            bp.Delete();
                            deleted = true;
                            break;
                        }
                    }

                    result.Success = deleted;
                    result.Message = deleted ? $"Breakpoint deleted at {filePath}:{lineNumber}" : "Breakpoint not found";
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        /// <summary>
        /// Delete all breakpoints.
        /// </summary>
        public BreakpointOperationResult DeleteAllBreakpoints()
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new BreakpointOperationResult { Operation = "DeleteAll" };

                try
                {
                    int count = debugger.Breakpoints.Count;
                    foreach (EnvDTE.Breakpoint bp in debugger.Breakpoints)
                    {
                        bp.Delete();
                    }

                    result.Success = true;
                    result.Message = $"Deleted {count} breakpoints";
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        /// <summary>
        /// Enable or disable a breakpoint.
        /// </summary>
        public BreakpointOperationResult EnableBreakpoint(string filePath, int lineNumber, bool enabled)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new BreakpointOperationResult { Operation = enabled ? "Enable" : "Disable" };

                try
                {
                    bool found = false;
                    foreach (EnvDTE.Breakpoint bp in debugger.Breakpoints)
                    {
                        if (bp.File == filePath && bp.FileLine == lineNumber)
                        {
                            bp.Enabled = enabled;
                            found = true;
                            break;
                        }
                    }

                    result.Success = found;
                    result.Message = found ? 
                        $"Breakpoint {(enabled ? "enabled" : "disabled")} at {filePath}:{lineNumber}" : 
                        "Breakpoint not found";
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        private string GetBreakpointTypeString(dbgBreakpointLocationType type)
        {
            switch (type)
            {
                case dbgBreakpointLocationType.dbgBreakpointLocationTypeFile: return "File";
                case dbgBreakpointLocationType.dbgBreakpointLocationTypeFunction: return "Function";
                case dbgBreakpointLocationType.dbgBreakpointLocationTypeAddress: return "Address";
                case dbgBreakpointLocationType.dbgBreakpointLocationTypeData: return "Data";
                default: return "Unknown";
            }
        }

        #endregion

        #region Registers

        /// <summary>
        /// Get CPU registers.
        /// </summary>
        public RegistersResult GetRegisters()
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new RegistersResult();

                if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    result.Error = "Not in break mode";
                    return result;
                }

                try
                {
                    bool is64Bit = Is64BitProcess(debugger);
                    result.Is64Bit = is64Bit;

                    // General purpose registers
                    if (is64Bit)
                    {
                        // 64-bit registers
                        string[] regs64 = { "rax", "rbx", "rcx", "rdx", "rsi", "rdi", "rbp", "rsp", 
                                           "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15",
                                           "rip", "rflags" };
                        foreach (var reg in regs64)
                        {
                            var expr = debugger.GetExpression($"@{reg}");
                            if (expr != null && expr.IsValidValue)
                            {
                                result.Registers.Add(new RegisterInfo
                                {
                                    Name = reg.ToUpper(),
                                    Value = expr.Value,
                                    HexValue = FormatAsHex(expr.Value),
                                    Category = GetRegisterCategory(reg)
                                });
                            }
                        }
                    }
                    else
                    {
                        // 32-bit registers
                        string[] regs32 = { "eax", "ebx", "ecx", "edx", "esi", "edi", "ebp", "esp", "eip", "eflags" };
                        foreach (var reg in regs32)
                        {
                            var expr = debugger.GetExpression($"@{reg}");
                            if (expr != null && expr.IsValidValue)
                            {
                                result.Registers.Add(new RegisterInfo
                                {
                                    Name = reg.ToUpper(),
                                    Value = expr.Value,
                                    HexValue = FormatAsHex(expr.Value),
                                    Category = GetRegisterCategory(reg)
                                });
                            }
                        }
                    }

                    // Segment registers
                    string[] segRegs = { "cs", "ds", "es", "fs", "gs", "ss" };
                    foreach (var reg in segRegs)
                    {
                        var expr = debugger.GetExpression($"@{reg}");
                        if (expr != null && expr.IsValidValue)
                        {
                            result.Registers.Add(new RegisterInfo
                            {
                                Name = reg.ToUpper(),
                                Value = expr.Value,
                                HexValue = FormatAsHex(expr.Value),
                                Category = "Segment"
                            });
                        }
                    }

                    // XMM registers (SSE)
                    for (int i = 0; i < 16; i++)
                    {
                        var expr = debugger.GetExpression($"@xmm{i}");
                        if (expr != null && expr.IsValidValue)
                        {
                            result.Registers.Add(new RegisterInfo
                            {
                                Name = $"XMM{i}",
                                Value = expr.Value,
                                Category = "SSE"
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    result.Error = ex.Message;
                }

                return result;
            });
        }

        private string GetRegisterCategory(string reg)
        {
            reg = reg.ToLower();
            if (reg.Contains("ip") || reg.Contains("rip") || reg.Contains("eip"))
                return "Instruction Pointer";
            if (reg.Contains("sp") || reg.Contains("bp"))
                return "Stack";
            if (reg.Contains("flags"))
                return "Flags";
            if (reg.StartsWith("r") && reg.Length <= 3)
                return "General Purpose";
            return "General Purpose";
        }

        private string FormatAsHex(string value)
        {
            // Try to format the value as hex
            if (value.StartsWith("0x") || value.StartsWith("0X"))
                return value;
            
            if (long.TryParse(value, out long num))
            {
                return $"0x{num:X}";
            }
            return value;
        }

        #endregion

        #region Watch

        /// <summary>
        /// Add a watch expression.
        /// </summary>
        public WatchResult AddWatch(string expression)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new WatchResult { Expression = expression };

                if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    result.Error = "Not in break mode";
                    return result;
                }

                try
                {
                    var expr = debugger.GetExpression(expression);
                    result.Value = expr.Value;
                    result.Type = expr.Type;
                    result.IsValid = expr.IsValidValue;
                    
                    if (!result.IsValid)
                    {
                        result.Error = expr.Value; // Error message is in Value when invalid
                    }

                    // Add to watch window
                    _dte.ExecuteCommand("Debug.AddWatch", expression);
                }
                catch (Exception ex)
                {
                    result.Error = ex.Message;
                    result.IsValid = false;
                }

                return result;
            });
        }

        #endregion

        #region Memory Search

        /// <summary>
        /// Search for a pattern in memory using optimized batch reading.
        /// Performance: Reads memory in chunks instead of byte-by-byte, reducing IPC calls from O(n) to O(n/4).
        /// Automatically handles thread context issues by trying to switch to user code thread if needed.
        /// </summary>
        public MemorySearchResult SearchMemory(string startAddress, string pattern, int maxResults = 100, int searchSize = 0x10000)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new MemorySearchResult { Pattern = pattern };

                if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    result.Error = "Not in break mode";
                    return result;
                }

                // Save current thread in case we need to restore
                int originalThreadId = debugger.CurrentThread?.ID ?? 0;
                bool switchedThread = false;

                try
                {
                    // Try to execute first
                    result = ExecuteSearchMemory(debugger, startAddress, pattern, maxResults, searchSize, result);
                    
                    // If first attempt failed (has error or no bytes searched), try switching to user code thread
                    if (!string.IsNullOrEmpty(result.Error) || result.BytesSearched == 0)
                    {
                        string firstError = result.Error ?? "No memory read";
                        _log($"SearchMemory first attempt failed: {firstError}");

                        var userThread = FindUserCodeThread(debugger);
                        if (userThread != null && userThread.ID != originalThreadId)
                        {
                            _log($"SearchMemory: Switching from thread {originalThreadId} to user code thread {userThread.ID}");
                            debugger.CurrentThread = userThread;
                            switchedThread = true;

                            // Reset result and retry
                            result = new MemorySearchResult { Pattern = pattern };
                            result = ExecuteSearchMemory(debugger, startAddress, pattern, maxResults, searchSize, result);
                            
                            // If retry also failed, log it
                            if (!string.IsNullOrEmpty(result.Error) || result.BytesSearched == 0)
                            {
                                _log($"SearchMemory retry also failed: {result.Error}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log($"SearchMemory exception: {ex.Message}");
                    result.Error = ex.Message;
                }
                finally
                {
                    // Restore original thread if we switched
                    if (switchedThread && originalThreadId > 0)
                    {
                        try
                        {
                            foreach (EnvDTE.Thread thread in debugger.CurrentProgram.Threads)
                            {
                                if (thread.ID == originalThreadId)
                                {
                                    debugger.CurrentThread = thread;
                                    break;
                                }
                            }
                        }
                        catch (Exception restoreEx)
                        {
                            _log($"Failed to restore original thread: {restoreEx.Message}");
                        }
                    }
                }

                return result;
            });
        }

        /// <summary>
        /// Internal method to execute memory search.
        /// </summary>
        private MemorySearchResult ExecuteSearchMemory(Debugger5 debugger, string startAddress, string pattern, int maxResults, int searchSize, MemorySearchResult result)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Parse pattern (hex bytes like "4D 5A" or string)
            byte[] patternBytes;
            if (pattern.Contains(" ") || (pattern.Length % 2 == 0 && pattern.All(c => "0123456789ABCDEFabcdef".Contains(c))))
            {
                // Hex pattern
                var hexBytes = pattern.Replace(" ", "").Trim();
                if (hexBytes.Length % 2 != 0)
                {
                    result.Error = "Invalid hex pattern: odd number of characters";
                    return result;
                }
                patternBytes = Enumerable.Range(0, hexBytes.Length / 2)
                    .Select(i => Convert.ToByte(hexBytes.Substring(i * 2, 2), 16))
                    .ToArray();
            }
            else
            {
                // String pattern (ASCII)
                patternBytes = System.Text.Encoding.ASCII.GetBytes(pattern);
            }

            if (patternBytes.Length == 0)
            {
                result.Error = "Empty search pattern";
                return result;
            }

            result.PatternBytes = patternBytes.Select(b => b.ToString("X2")).ToArray();

            // Get start address
            var addrExpr = debugger.GetExpression(startAddress);
            if (!addrExpr.IsValidValue)
            {
                result.Error = $"Invalid start address: {startAddress}";
                return result;
            }

            ulong addr = ParseAddress(addrExpr.Value);

            // Read memory block using optimized batch reading
            // This reduces IPC calls from O(searchSize) to O(searchSize/4)
            byte[] memoryBlock = ReadMemoryBlockOptimized(debugger, addr, searchSize);

            if (memoryBlock == null || memoryBlock.Length == 0)
            {
                result.Error = "Could not read memory at specified address";
                return result;
            }

            // Search for pattern in the local memory block
            int found = 0;
            int searchLimit = memoryBlock.Length - patternBytes.Length + 1;

            // Determine if 64-bit once, not in the loop
            var ripExpr = debugger.GetExpression("@rip");
            bool is64Bit = ripExpr != null && ripExpr.IsValidValue;

            for (int i = 0; i < searchLimit && found < maxResults; i++)
            {
                bool match = true;

                // Check if pattern matches at this position
                for (int j = 0; j < patternBytes.Length; j++)
                {
                    if (memoryBlock[i + j] != patternBytes[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    string matchAddr = is64Bit
                        ? $"0x{addr + (ulong)i:X16}"
                        : $"0x{addr + (ulong)i:X8}";

                    result.Matches.Add(matchAddr);
                    found++;
                }
            }

            result.TotalFound = found;
            result.BytesSearched = memoryBlock.Length;

            return result;
        }

        #endregion

        #region Output Window

        /// <summary>
        /// Get content from a Visual Studio Output Window pane.
        /// </summary>
        /// <param name="pane">The name of the output pane (e.g., "Debug", "Build", "General"). If empty, defaults to "Debug".</param>
        /// <param name="tailLines">Number of lines to return from the end. 0 means all lines.</param>
        public OutputWindowResult GetOutputWindow(string pane = "Debug", int tailLines = 50)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var result = new OutputWindowResult
                {
                    Pane = pane,
                    RequestedLines = tailLines
                };

                try
                {
                    // Get the Output window
                    var outputWindow = _dte.ToolWindows.OutputWindow;
                    if (outputWindow == null)
                    {
                        result.Error = "Output window not available";
                        return result;
                    }

                    // List available panes
                    foreach (OutputWindowPane p in outputWindow.OutputWindowPanes)
                    {
                        try
                        {
                            result.AvailablePanes.Add(p.Name);
                        }
                        catch { }
                    }

                    // Find the requested pane
                    OutputWindowPane targetPane = null;
                    
                    // Try exact match first
                    try
                    {
                        targetPane = outputWindow.OutputWindowPanes.Item(pane);
                    }
                    catch
                    {
                        // Try case-insensitive match
                        foreach (OutputWindowPane p in outputWindow.OutputWindowPanes)
                        {
                            try
                            {
                                if (p.Name.Equals(pane, StringComparison.OrdinalIgnoreCase))
                                {
                                    targetPane = p;
                                    break;
                                }
                            }
                            catch { }
                        }
                    }

                    if (targetPane == null)
                    {
                        result.Error = $"Output pane '{pane}' not found. Available panes: {string.Join(", ", result.AvailablePanes)}";
                        return result;
                    }

                    result.Pane = targetPane.Name;

                    // Get the text from the pane
                    var textDocument = targetPane.TextDocument;
                    if (textDocument == null)
                    {
                        result.Error = "Could not access output pane text";
                        return result;
                    }

                    var startPoint = textDocument.StartPoint.CreateEditPoint();
                    var endPoint = textDocument.EndPoint.CreateEditPoint();
                    string fullContent = startPoint.GetText(endPoint);

                    if (string.IsNullOrEmpty(fullContent))
                    {
                        result.Content = "";
                        result.LineCount = 0;
                        result.Truncated = false;
                        return result;
                    }

                    // Split into lines
                    var lines = fullContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                    result.LineCount = lines.Length;

                    // Apply tail limit if specified
                    if (tailLines > 0 && lines.Length > tailLines)
                    {
                        var tailArray = new string[tailLines];
                        Array.Copy(lines, lines.Length - tailLines, tailArray, 0, tailLines);
                        result.Content = string.Join("\n", tailArray);
                        result.Truncated = true;
                    }
                    else
                    {
                        result.Content = fullContent;
                        result.Truncated = false;
                    }
                }
                catch (Exception ex)
                {
                    _log($"GetOutputWindow error: {ex.Message}");
                    result.Error = ex.Message;
                }

                return result;
            });
        }

        /// <summary>
        /// Search for a pattern in a Visual Studio Output Window pane.
        /// </summary>
        /// <param name="pattern">The pattern to search for (string or regex)</param>
        /// <param name="pane">The name of the output pane</param>
        /// <param name="isRegex">Whether the pattern is a regular expression</param>
        /// <param name="contextLines">Number of context lines before and after each match</param>
        /// <param name="maxMatches">Maximum number of matches to return</param>
        public SearchOutputResult SearchOutput(string pattern, string pane = "Debug", bool isRegex = false, int contextLines = 2, int maxMatches = 20)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var result = new SearchOutputResult
                {
                    Pane = pane,
                    Pattern = pattern,
                    IsRegex = isRegex
                };

                try
                {
                    if (string.IsNullOrEmpty(pattern))
                    {
                        result.Error = "Pattern cannot be empty";
                        return result;
                    }

                    // Get the Output window
                    var outputWindow = _dte.ToolWindows.OutputWindow;
                    if (outputWindow == null)
                    {
                        result.Error = "Output window not available";
                        return result;
                    }

                    // Find the requested pane
                    OutputWindowPane targetPane = null;
                    try
                    {
                        targetPane = outputWindow.OutputWindowPanes.Item(pane);
                    }
                    catch
                    {
                        foreach (OutputWindowPane p in outputWindow.OutputWindowPanes)
                        {
                            try
                            {
                                if (p.Name.Equals(pane, StringComparison.OrdinalIgnoreCase))
                                {
                                    targetPane = p;
                                    break;
                                }
                            }
                            catch { }
                        }
                    }

                    if (targetPane == null)
                    {
                        result.Error = $"Output pane '{pane}' not found";
                        return result;
                    }

                    result.Pane = targetPane.Name;

                    // Get the text
                    var textDocument = targetPane.TextDocument;
                    if (textDocument == null)
                    {
                        result.Error = "Could not access output pane text";
                        return result;
                    }

                    var startPoint = textDocument.StartPoint.CreateEditPoint();
                    var endPoint = textDocument.EndPoint.CreateEditPoint();
                    string fullContent = startPoint.GetText(endPoint);

                    if (string.IsNullOrEmpty(fullContent))
                    {
                        result.TotalMatches = 0;
                        return result;
                    }

                    // Split into lines
                    var lines = fullContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

                    // Prepare regex or simple matcher
                    Regex regex = null;
                    if (isRegex)
                    {
                        try
                        {
                            regex = new Regex(pattern, RegexOptions.IgnoreCase);
                        }
                        catch (Exception ex)
                        {
                            result.Error = $"Invalid regex pattern: {ex.Message}";
                            return result;
                        }
                    }

                    // Search for matches
                    int totalMatches = 0;
                    for (int i = 0; i < lines.Length; i++)
                    {
                        bool isMatch = false;
                        if (isRegex)
                        {
                            isMatch = regex.IsMatch(lines[i]);
                        }
                        else
                        {
                            isMatch = lines[i].IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
                        }

                        if (isMatch)
                        {
                            totalMatches++;

                            if (result.Matches.Count < maxMatches)
                            {
                                var match = new OutputMatch
                                {
                                    LineNumber = i + 1, // 1-based line number
                                    Line = lines[i]
                                };

                                // Add context lines
                                int contextStart = Math.Max(0, i - contextLines);
                                int contextEnd = Math.Min(lines.Length - 1, i + contextLines);
                                for (int j = contextStart; j <= contextEnd; j++)
                                {
                                    if (j != i) // Don't include the match line in context
                                    {
                                        match.Context.Add($"L{j + 1}: {lines[j]}");
                                    }
                                }

                                result.Matches.Add(match);
                            }
                        }
                    }

                    result.TotalMatches = totalMatches;
                    result.Truncated = totalMatches > maxMatches;
                }
                catch (Exception ex)
                {
                    _log($"SearchOutput error: {ex.Message}");
                    result.Error = ex.Message;
                }

                return result;
            });
        }

        #endregion

        #region Stack Frame Navigation

        /// <summary>
        /// Switch to a specific stack frame.
        /// </summary>
        public DebugControlResult SwitchStackFrame(int frameIndex)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new DebugControlResult { Action = "SwitchStackFrame" };

                if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    result.Success = false;
                    result.Message = "Not in break mode";
                    return result;
                }

                try
                {
                    var thread = debugger.CurrentThread;
                    if (thread != null && frameIndex < thread.StackFrames.Count)
                    {
                        // Stack frames are 1-indexed in DTE
                        var frame = thread.StackFrames.Item(frameIndex + 1) as EnvDTE.StackFrame;
                        if (frame != null)
                        {
                            debugger.CurrentStackFrame = frame;
                            result.Success = true;
                            result.Message = $"Switched to frame {frameIndex}: {frame.FunctionName}";
                            result.NewLocation = new LocationInfo
                            {
                                FunctionName = frame.FunctionName,
                                FilePath = GetFrameFileName(frame),
                                LineNumber = GetFrameLineNumber(frame)
                            };
                        }
                        else
                        {
                            result.Success = false;
                            result.Message = "Invalid frame";
                        }
                    }
                    else
                    {
                        result.Success = false;
                        result.Message = $"Frame index {frameIndex} out of range";
                    }
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        /// <summary>
        /// Switch to a specific thread.
        /// </summary>
        public DebugControlResult SwitchThread(int threadId)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new DebugControlResult { Action = "SwitchThread" };

                if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    result.Success = false;
                    result.Message = "Not in break mode";
                    return result;
                }

                try
                {
                    bool found = false;
                    foreach (EnvDTE.Thread thread in debugger.CurrentProgram.Threads)
                    {
                        if (thread.ID == threadId)
                        {
                            debugger.CurrentThread = thread;
                            found = true;
                            result.Success = true;
                            result.Message = $"Switched to thread {threadId}";
                            
                            if (thread.StackFrames.Count > 0)
                            {
                                var topFrame = thread.StackFrames.Item(1) as EnvDTE.StackFrame;
                                if (topFrame != null)
                                {
                                    result.NewLocation = new LocationInfo
                                    {
                                        FunctionName = topFrame.FunctionName,
                                        FilePath = GetFrameFileName(topFrame),
                                        LineNumber = GetFrameLineNumber(topFrame)
                                    };
                                }
                            }
                            break;
                        }
                    }

                    if (!found)
                    {
                        result.Success = false;
                        result.Message = $"Thread {threadId} not found";
                    }
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Gets the file name from a stack frame safely - IMPROVED VERSION.
        /// Handles both native and managed code.
        /// </summary>
        private string GetFrameFileName(EnvDTE.StackFrame frame)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                string language = frame.Language ?? "";
                bool isManagedCode = language == "C#" || language == "VB" || language == "F#" || language == "Basic";
                
                // Method 1: For managed code, try to find source file through solution search
                if (isManagedCode)
                {
                    try
                    {
                        // Try to get from active document if this is the current frame
                        var debugger = Debugger;
                        if (debugger != null && debugger.CurrentStackFrame == frame)
                        {
                            var activeDoc = _dte.ActiveDocument;
                            if (activeDoc != null && !string.IsNullOrEmpty(activeDoc.FullName))
                            {
                                // Verify the file is a source file
                                if (activeDoc.FullName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                                    activeDoc.FullName.EndsWith(".vb", StringComparison.OrdinalIgnoreCase) ||
                                    activeDoc.FullName.EndsWith(".fs", StringComparison.OrdinalIgnoreCase))
                                {
                                    return activeDoc.FullName;
                                }
                            }
                        }
                        
                        // Try to find source file by class name
                        var parts = ParseFunctionName(frame.FunctionName);
                        if (!string.IsNullOrEmpty(parts.ClassName))
                        {
                            var filePath = FindSourceFileByClassName(parts.ClassName);
                            if (!string.IsNullOrEmpty(filePath))
                            {
                                return filePath;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _log($"Error finding managed source file: {ex.Message}");
                    }
                }

                // Method 2: For native code, try __FILE__ macro
                if (language == "C++" || language == "C" || !isManagedCode)
                {
                    try
                    {
                        var debugger = Debugger;
                        if (debugger != null)
                        {
                            var fileExpr = debugger.GetExpression("__FILE__");
                            if (fileExpr != null && fileExpr.IsValidValue)
                            {
                                string filePath = fileExpr.Value.Trim('"');
                                if (!string.IsNullOrEmpty(filePath) && System.IO.File.Exists(filePath))
                                {
                                    return filePath;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _log($"[GetFrameFileName] Error using __FILE__ macro: {ex.Message}");
                    }
                }

                // Method 3: Try to get from frame's module path and search for corresponding source
                try
                {
                    string modulePath = frame.Module;
                    if (!string.IsNullOrEmpty(modulePath))
                    {
                        // For .NET assemblies, try to find matching source file in solution
                        string moduleDir = System.IO.Path.GetDirectoryName(modulePath);
                        string moduleName = System.IO.Path.GetFileNameWithoutExtension(modulePath);
                        
                        // Check if there's a PDB file nearby
                        string pdbPath = System.IO.Path.ChangeExtension(modulePath, ".pdb");
                        if (System.IO.File.Exists(pdbPath))
                        {
                            // PDB exists, VS should be able to find source
                            _log($"PDB found at {pdbPath}, source lookup may succeed");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log($"[GetFrameFileName] Error checking module path: {ex.Message}");
                }

                // Fallback: Return module name
                return frame.Module ?? "";
            }
            catch (Exception ex)
            {
                _log($"Error getting frame file name: {ex.Message}");
                return "";
            }
        }

        /// <summary>
        /// Gets the line number from a stack frame safely - IMPROVED VERSION.
        /// Returns a tuple with line number and whether it's reliable (from the current frame).
        /// </summary>
        private (int LineNumber, bool IsReliable) GetFrameLineNumberEx(EnvDTE.StackFrame frame)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var debugger = Debugger;
                bool isCurrentFrame = debugger != null && debugger.CurrentStackFrame == frame;
                string language = frame.Language ?? "";
                bool isManagedCode = language == "C#" || language == "VB" || language == "F#" || language == "Basic";

                // Method 1: For managed code current frame, use active document cursor position
                if (isManagedCode && isCurrentFrame)
                {
                    try
                    {
                        var activeDoc = _dte.ActiveDocument;
                        if (activeDoc != null)
                        {
                            var textDoc = activeDoc.Object("TextDocument") as EnvDTE.TextDocument;
                            if (textDoc != null && textDoc.Selection != null)
                            {
                                int line = textDoc.Selection.ActivePoint.Line;
                                if (line > 0)
                                {
                                    return (line, true);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _log($"[GetFrameLineNumberEx] Error using active document: {ex.Message}");
                    }
                }

                // Method 2: For native code current frame, try __LINE__ macro
                if ((language == "C++" || language == "C") && isCurrentFrame)
                {
                    try
                    {
                        if (debugger != null)
                        {
                            var lineExpr = debugger.GetExpression("__LINE__");
                            if (lineExpr != null && lineExpr.IsValidValue)
                            {
                                if (int.TryParse(lineExpr.Value, out int lineNum) && lineNum > 0)
                                {
                                    return (lineNum, true);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _log($"[GetFrameLineNumberEx] Error using __LINE__ macro: {ex.Message}");
                    }
                }

                // Method 3: For non-current frames, try to find open document for this file
                // and check if there's debug info available
                try
                {
                    string filePath = GetFrameFileName(frame);
                    if (!string.IsNullOrEmpty(filePath) && System.IO.File.Exists(filePath))
                    {
                        // Check if this file is currently open in an editor
                        foreach (Document doc in _dte.Documents)
                        {
                            try
                            {
                                if (doc.FullName.Equals(filePath, StringComparison.OrdinalIgnoreCase))
                                {
                                    // File is open but we can't reliably get line for non-current frames
                                    // Return 0 with unreliable flag
                                    break;
                                }
                            }
                            catch (Exception ex)
                            {
                                _log($"[GetFrameLineNumberEx] Error checking document: {ex.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log($"[GetFrameLineNumberEx] Error checking open documents: {ex.Message}");
                }

                // For non-current frames, we cannot reliably determine line number
                // without using lower-level debugging APIs (IDebugStackFrame2, etc.)
                return (0, false);
            }
            catch (Exception ex)
            {
                _log($"[GetFrameLineNumberEx] Unexpected error: {ex.Message}");
                return (0, false);
            }
        }

        /// <summary>
        /// Gets the line number from a stack frame safely - simple version for backward compatibility.
        /// </summary>
        private int GetFrameLineNumber(EnvDTE.StackFrame frame)
        {
            return GetFrameLineNumberEx(frame).LineNumber;
        }

        /// <summary>
        /// Parse function name to extract class and method.
        /// Handles C# style (Namespace.Class.Method) and C++ style (Namespace::Class::Method)
        /// </summary>
        private (string ClassName, string MethodName) ParseFunctionName(string functionName)
        {
            if (string.IsNullOrEmpty(functionName))
                return (null, null);

            // Remove generic type parameters using stack-based approach to handle nested generics
            // e.g., "Dictionary<string, List<int>>" -> "Dictionary"
            functionName = RemoveGenericParameters(functionName);

            // Remove parameter list if present (also handle nested parentheses)
            functionName = RemoveMethodParameters(functionName);

            // Handle C# style: Namespace.Class.Method or Class.Method
            // Handle C++ style: Namespace::Class::Method
            string separator = functionName.Contains("::") ? "::" : ".";
            var parts = functionName.Split(new[] { separator }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length >= 2)
            {
                return (parts[parts.Length - 2], parts[parts.Length - 1]);
            }

            return (null, parts.Length > 0 ? parts[0] : null);
        }

        /// <summary>
        /// Remove generic type parameters from a string, handling nested generics.
        /// e.g., "Dictionary&lt;string, List&lt;int&gt;&gt;" becomes "Dictionary"
        /// </summary>
        private string RemoveGenericParameters(string input)
        {
            if (string.IsNullOrEmpty(input) || !input.Contains("<"))
                return input;

            var result = new StringBuilder();
            int depth = 0;

            foreach (char c in input)
            {
                if (c == '<')
                {
                    depth++;
                }
                else if (c == '>')
                {
                    depth--;
                }
                else if (depth == 0)
                {
                    result.Append(c);
                }
            }

            return result.ToString();
        }

        /// <summary>
        /// Remove method parameters from a string, handling nested parentheses.
        /// e.g., "Method(int x, Func&lt;int, int&gt;)" becomes "Method"
        /// </summary>
        private string RemoveMethodParameters(string input)
        {
            if (string.IsNullOrEmpty(input) || !input.Contains("("))
                return input;

            int parenIndex = input.IndexOf('(');
            if (parenIndex <= 0)
                return input;

            // Just take everything before the first '('
            return input.Substring(0, parenIndex).Trim();
        }

        /// <summary>
        /// Find source file by searching solution for class definition.
        /// </summary>
        private string FindSourceFileByClassName(string className)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (_dte.Solution == null || string.IsNullOrEmpty(className))
                    return null;

                foreach (Project project in _dte.Solution.Projects)
                {
                    try
                    {
                        var filePath = SearchProjectForClass(project.ProjectItems, className);
                        if (!string.IsNullOrEmpty(filePath))
                        {
                            return filePath;
                        }
                    }
                    catch (Exception ex)
                    {
                        _log($"[FindSourceFileByClassName] Error searching project '{project?.Name}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _log($"Error finding source file by class name: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Search project items recursively for a class definition.
        /// </summary>
        private string SearchProjectForClass(ProjectItems items, string className)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (items == null) return null;

            foreach (ProjectItem item in items)
            {
                try
                {
                    // Check if this is a code file
                    if (item.FileCodeModel != null)
                    {
                        if (ContainsClass(item.FileCodeModel.CodeElements, className))
                        {
                            try
                            {
                                return item.FileNames[1]; // 1-indexed
                            }
                            catch
                            {
                                return item.FileNames[0];
                            }
                        }
                    }

                    // Recurse into nested items (folders, etc.)
                    if (item.ProjectItems != null && item.ProjectItems.Count > 0)
                    {
                        var result = SearchProjectForClass(item.ProjectItems, className);
                        if (!string.IsNullOrEmpty(result))
                        {
                            return result;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log($"[SearchProjectForClass] Error processing item: {ex.Message}");
                }
            }
            return null;
        }

        /// <summary>
        /// Check if code elements contain a class with the specified name.
        /// </summary>
        private bool ContainsClass(CodeElements elements, string className)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (elements == null) return false;

            try
            {
                foreach (CodeElement element in elements)
                {
                    try
                    {
                        // Check if this element is the target class/struct
                        if ((element.Kind == vsCMElement.vsCMElementClass ||
                             element.Kind == vsCMElement.vsCMElementStruct) &&
                            element.Name == className)
                        {
                            return true;
                        }

                        // Search within namespaces
                        if (element.Kind == vsCMElement.vsCMElementNamespace)
                        {
                            var ns = element as CodeNamespace;
                            if (ns != null && ContainsClass(ns.Members, className))
                            {
                                return true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _log($"[ContainsClass] Error checking element: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _log($"[ContainsClass] Error iterating elements: {ex.Message}");
            }
            return false;
        }

        private T ExecuteOnMainThread<T>(Func<T> action)
        {
            T result = default(T);
            Exception capturedException = null;
            
            try
            {
                // Use a timeout to prevent infinite blocking
                var task = System.Threading.Tasks.Task.Run(() =>
                {
                    ThreadHelper.JoinableTaskFactory.Run(async () =>
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        try
                        {
                            result = action();
                        }
                        catch (Exception ex)
                        {
                            capturedException = ex;
                        }
                    });
                });

                // Wait with timeout (10 seconds)
                if (!task.Wait(TimeSpan.FromSeconds(10)))
                {
                    _log("ExecuteOnMainThread: Timeout waiting for UI thread");
                    throw new TimeoutException("Timeout waiting for VS UI thread. Is Visual Studio responsive?");
                }

                if (capturedException != null)
                {
                    throw capturedException;
                }
            }
            catch (AggregateException ae)
            {
                _log($"ExecuteOnMainThread: AggregateException - {ae.InnerException?.Message ?? ae.Message}");
                throw ae.InnerException ?? ae;
            }
            catch (Exception ex)
            {
                _log($"ExecuteOnMainThread: Exception - {ex.Message}");
                throw;
            }
            
            return result;
        }

        #endregion
    }
}
