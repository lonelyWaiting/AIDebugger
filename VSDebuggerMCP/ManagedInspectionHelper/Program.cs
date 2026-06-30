using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Web.Script.Serialization;
using VSDebuggerMCP.Protocol;

namespace ManagedInspectionHelper
{
    internal static class Program
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private static readonly OpCode[] SingleByteOpCodes = BuildOpCodeMap(false);
        private static readonly OpCode[] MultiByteOpCodes = BuildOpCodeMap(true);
        private static List<string> _probeDirectories = new List<string>();

        private static int Main()
        {
            try
            {
                string requestJson = Console.In.ReadToEnd();
                var request = Serializer.Deserialize<ManagedInspectionRequest>(requestJson);
                if (request == null || string.IsNullOrWhiteSpace(request.Operation))
                {
                    Console.Error.WriteLine("Invalid managed inspection request");
                    return 2;
                }

                _probeDirectories = NormalizeProbeDirectories(request.ProbeDirectories, request.AssemblyPath);
                AppDomain.CurrentDomain.AssemblyResolve += ResolveManagedDependency;
                object result;
                switch (request.Operation)
                {
                    case "inspect_managed_assembly":
                        result = InspectManagedAssembly(request);
                        break;
                    case "inspect_managed_type":
                        result = InspectManagedType(request);
                        break;
                    case "inspect_managed_method_il":
                        result = InspectManagedMethodIl(request);
                        break;
                    default:
                        Console.Error.WriteLine("Unknown managed inspection operation: " + request.Operation);
                        return 3;
                }

                Console.Out.Write(Serializer.Serialize(result));
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
            finally
            {
                AppDomain.CurrentDomain.AssemblyResolve -= ResolveManagedDependency;
            }
        }

        private static ManagedAssemblyResult InspectManagedAssembly(ManagedInspectionRequest request)
        {
            string normalizedPath = NormalizeAssemblyPath(request.AssemblyPath);
            string normalizedFilter = string.IsNullOrWhiteSpace(request.TypeFilter) ? null : request.TypeFilter.Trim();
            int maxTypes = Math.Max(1, Math.Min(request.MaxTypes, 4096));
            int maxMethodsPerType = Math.Max(0, Math.Min(request.MaxMethodsPerType, 512));
            var result = new ManagedAssemblyResult
            {
                AssemblyPath = normalizedPath,
                TypeFilter = normalizedFilter,
                Capability = "external_process_managed_metadata_and_il_read_only"
            };

            if (string.IsNullOrEmpty(normalizedPath) || !File.Exists(normalizedPath))
            {
                result.Error = "Assembly file not found";
                result.Partial = true;
                return result;
            }

            try
            {
                var assembly = Assembly.LoadFrom(normalizedPath);
                result.AssemblyFullName = assembly.FullName;
                result.ModuleVersionId = SafeModuleVersionId(assembly.ManifestModule);

                string loaderError;
                var types = GetLoadableTypes(assembly, out loaderError).ToList();
                result.TotalTypeCount = types.Count;
                var matchedTypes = types
                    .Where(t => MatchesManagedTypeFilter(t, normalizedFilter))
                    .OrderBy(t => t.FullName ?? t.Name)
                    .ToList();
                result.MatchedTypeCount = matchedTypes.Count;

                foreach (var type in matchedTypes.Take(maxTypes))
                {
                    result.Types.Add(BuildManagedTypeInfo(type, maxMethodsPerType));
                }

                result.Partial = matchedTypes.Count > result.Types.Count || !string.IsNullOrEmpty(loaderError);
                result.Error = loaderError;
            }
            catch (Exception ex)
            {
                result.Partial = true;
                result.Error = ex.Message;
            }

            return result;
        }

        private static ManagedTypeResult InspectManagedType(ManagedInspectionRequest request)
        {
            string normalizedPath = NormalizeAssemblyPath(request.AssemblyPath);
            string normalizedTypeName = string.IsNullOrWhiteSpace(request.TypeName) ? null : request.TypeName.Trim();
            int maxMethods = Math.Max(0, Math.Min(request.MaxMethods, 4096));
            var result = new ManagedTypeResult
            {
                AssemblyPath = normalizedPath,
                TypeName = normalizedTypeName,
                Capability = "external_process_managed_metadata_and_il_read_only"
            };

            if (string.IsNullOrEmpty(normalizedPath) || !File.Exists(normalizedPath))
            {
                result.Error = "Assembly file not found";
                return result;
            }

            if (string.IsNullOrEmpty(normalizedTypeName))
            {
                result.Error = "typeName is required";
                return result;
            }

            try
            {
                var assembly = Assembly.LoadFrom(normalizedPath);
                string loaderError;
                var type = FindManagedType(assembly, normalizedTypeName, out loaderError);
                if (type == null)
                {
                    result.Error = string.IsNullOrEmpty(loaderError)
                        ? "Type not found: " + normalizedTypeName
                        : "Type not found: " + normalizedTypeName + "; " + loaderError;
                    return result;
                }

                result.Type = BuildManagedTypeInfo(type, maxMethods);
                result.Error = loaderError;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }

            return result;
        }

        private static ManagedMethodIlResult InspectManagedMethodIl(ManagedInspectionRequest request)
        {
            string normalizedPath = NormalizeAssemblyPath(request.AssemblyPath);
            string normalizedTypeName = string.IsNullOrWhiteSpace(request.TypeName) ? null : request.TypeName.Trim();
            string normalizedMethodName = string.IsNullOrWhiteSpace(request.MethodName) ? null : request.MethodName.Trim();
            int overloadIndex = Math.Max(0, request.OverloadIndex);
            int maxInstructions = Math.Max(1, Math.Min(request.MaxInstructions, 4096));
            var result = new ManagedMethodIlResult
            {
                AssemblyPath = normalizedPath,
                TypeName = normalizedTypeName,
                MethodName = normalizedMethodName,
                OverloadIndex = overloadIndex,
                Capability = "external_process_managed_metadata_and_il_read_only"
            };

            if (string.IsNullOrEmpty(normalizedPath) || !File.Exists(normalizedPath))
            {
                result.Partial = true;
                result.Error = "Assembly file not found";
                return result;
            }

            if (string.IsNullOrEmpty(normalizedTypeName) || string.IsNullOrEmpty(normalizedMethodName))
            {
                result.Partial = true;
                result.Error = "typeName and methodName are required";
                return result;
            }

            try
            {
                var assembly = Assembly.LoadFrom(normalizedPath);
                string loaderError;
                var type = FindManagedType(assembly, normalizedTypeName, out loaderError);
                if (type == null)
                {
                    result.Partial = true;
                    result.Error = string.IsNullOrEmpty(loaderError)
                        ? "Type not found: " + normalizedTypeName
                        : "Type not found: " + normalizedTypeName + "; " + loaderError;
                    return result;
                }

                var candidates = GetManagedMethodCandidates(type, normalizedMethodName).ToList();
                result.MatchedOverloadCount = candidates.Count;
                if (candidates.Count == 0)
                {
                    result.Partial = true;
                    result.Error = "Method not found: " + normalizedMethodName;
                    return result;
                }

                if (overloadIndex >= candidates.Count)
                {
                    result.Partial = true;
                    result.Error = $"overloadIndex {overloadIndex} is out of range; matched {candidates.Count} overload(s)";
                    return result;
                }

                var method = candidates[overloadIndex];
                result.Method = BuildManagedMethodInfo(method);
                MethodBody body;
                try
                {
                    body = method.GetMethodBody();
                }
                catch (Exception ex)
                {
                    result.Partial = true;
                    result.Error = "Method body unavailable: " + ex.Message;
                    return result;
                }

                if (body == null)
                {
                    result.Partial = true;
                    result.Error = "Method body unavailable; method may be abstract, extern, or runtime-provided";
                    return result;
                }

                foreach (LocalVariableInfo local in body.LocalVariables)
                {
                    result.Locals.Add(new ManagedLocalInfo
                    {
                        Index = local.LocalIndex,
                        Type = FormatTypeName(local.LocalType),
                        IsPinned = local.IsPinned
                    });
                }

                string decodeError;
                bool partial;
                int totalInstructionCount;
                result.Instructions = DecodeIlInstructions(
                    method,
                    body.GetILAsByteArray() ?? new byte[0],
                    maxInstructions,
                    out totalInstructionCount,
                    out partial,
                    out decodeError);
                result.TotalInstructionCount = totalInstructionCount;
                result.Partial = partial || !string.IsNullOrEmpty(loaderError) || !string.IsNullOrEmpty(decodeError);
                result.Error = CombineErrors(loaderError, decodeError);
            }
            catch (Exception ex)
            {
                result.Partial = true;
                result.Error = ex.Message;
            }

            return result;
        }

        private static ResolveEventHandler ResolveManagedDependency = (sender, args) =>
        {
            try
            {
                var requested = new AssemblyName(args.Name);
                var alreadyLoaded = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.FullName, args.Name, StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(a.GetName().Name, requested.Name, StringComparison.OrdinalIgnoreCase));
                if (alreadyLoaded != null)
                {
                    return alreadyLoaded;
                }

                foreach (string directory in _probeDirectories)
                {
                    foreach (var extension in new[] { ".dll", ".exe" })
                    {
                        string candidate = Path.Combine(directory, requested.Name + extension);
                        if (File.Exists(candidate))
                        {
                            return Assembly.LoadFrom(candidate);
                        }
                    }
                }
            }
            catch
            {
            }

            return null;
        };

        private static List<string> NormalizeProbeDirectories(IEnumerable<string> probeDirectories, string assemblyPath)
        {
            var directories = new List<string>();
            AddProbeDirectory(directories, Path.GetDirectoryName(NormalizeAssemblyPath(assemblyPath)));
            if (probeDirectories != null)
            {
                foreach (string directory in probeDirectories)
                {
                    AddProbeDirectory(directories, directory);
                }
            }

            return directories;
        }

        private static void AddProbeDirectory(List<string> directories, string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            try
            {
                string fullPath = Path.GetFullPath(directory);
                if (Directory.Exists(fullPath) &&
                    !directories.Any(d => string.Equals(d, fullPath, StringComparison.OrdinalIgnoreCase)))
                {
                    directories.Add(fullPath);
                }
            }
            catch
            {
            }
        }

        private static string NormalizeAssemblyPath(string assemblyPath)
        {
            if (string.IsNullOrWhiteSpace(assemblyPath))
            {
                return null;
            }

            try
            {
                return Path.GetFullPath(assemblyPath.Trim().Trim('"'));
            }
            catch
            {
                return assemblyPath.Trim().Trim('"');
            }
        }

        private static string SafeModuleVersionId(Module module)
        {
            try
            {
                return module?.ModuleVersionId.ToString("D");
            }
            catch
            {
                return null;
            }
        }

        private static IEnumerable<Type> GetLoadableTypes(Assembly assembly, out string loaderError)
        {
            loaderError = null;
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                loaderError = string.Join("; ", ex.LoaderExceptions
                    .Where(e => e != null)
                    .Take(8)
                    .Select(e => e.Message));
                return ex.Types.Where(t => t != null).ToArray();
            }
            catch (Exception ex)
            {
                loaderError = ex.Message;
                return Enumerable.Empty<Type>();
            }
        }

        private static bool MatchesManagedTypeFilter(Type type, string filter)
        {
            if (type == null)
            {
                return false;
            }

            if (string.IsNullOrEmpty(filter))
            {
                return true;
            }

            return ContainsIgnoreCase(type.FullName, filter) ||
                   ContainsIgnoreCase(type.Name, filter) ||
                   ContainsIgnoreCase(type.Namespace, filter);
        }

        private static bool ContainsIgnoreCase(string value, string expected)
        {
            return !string.IsNullOrEmpty(value) &&
                   !string.IsNullOrEmpty(expected) &&
                   value.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static ManagedTypeInfo BuildManagedTypeInfo(Type type, int maxMethods)
        {
            var info = new ManagedTypeInfo
            {
                Name = type.Name,
                FullName = type.FullName,
                NamespaceName = type.Namespace,
                AssemblyQualifiedName = type.AssemblyQualifiedName,
                BaseType = FormatTypeName(type.BaseType),
                IsPublic = type.IsPublic || type.IsNestedPublic,
                IsInterface = type.IsInterface,
                IsEnum = type.IsEnum,
                IsValueType = type.IsValueType
            };

            var methods = GetDeclaredManagedMethods(type)
                .OrderBy(m => m.Name)
                .ThenBy(FormatMethodSignature)
                .ToList();
            foreach (var method in methods.Take(maxMethods))
            {
                info.Methods.Add(BuildManagedMethodInfo(method));
            }
            info.Partial = methods.Count > info.Methods.Count;
            return info;
        }

        private static IEnumerable<MethodBase> GetDeclaredManagedMethods(Type type)
        {
            const BindingFlags flags =
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.Instance |
                BindingFlags.Static |
                BindingFlags.DeclaredOnly;
            foreach (var constructor in type.GetConstructors(flags))
            {
                yield return constructor;
            }
            foreach (var method in type.GetMethods(flags))
            {
                yield return method;
            }
        }

        private static ManagedMethodInfo BuildManagedMethodInfo(MethodBase method)
        {
            var info = new ManagedMethodInfo
            {
                Name = method.Name,
                Signature = FormatMethodSignature(method),
                DeclaringType = method.DeclaringType?.FullName,
                ReturnType = method is MethodInfo methodInfo ? FormatTypeName(methodInfo.ReturnType) : null,
                IsPublic = method.IsPublic,
                IsStatic = method.IsStatic,
                IsConstructor = method.IsConstructor
            };

            try
            {
                info.MetadataToken = method.MetadataToken;
                info.MetadataTokenHex = FormatMetadataToken(method.MetadataToken);
            }
            catch
            {
            }

            try
            {
                foreach (var parameter in method.GetParameters())
                {
                    info.Parameters.Add(new ManagedParameterInfo
                    {
                        Name = parameter.Name,
                        Type = FormatTypeName(parameter.ParameterType),
                        Position = parameter.Position
                    });
                }
            }
            catch
            {
            }

            return info;
        }

        private static Type FindManagedType(Assembly assembly, string typeName, out string loaderError)
        {
            loaderError = null;
            try
            {
                var exact = assembly.GetType(typeName, throwOnError: false, ignoreCase: true);
                if (exact != null)
                {
                    return exact;
                }
            }
            catch (Exception ex)
            {
                loaderError = ex.Message;
            }

            string typeLoadError;
            var types = GetLoadableTypes(assembly, out typeLoadError).ToList();
            loaderError = CombineErrors(loaderError, typeLoadError);
            return types.FirstOrDefault(t => string.Equals(t.FullName, typeName, StringComparison.OrdinalIgnoreCase)) ??
                   types.FirstOrDefault(t => string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase)) ??
                   types.FirstOrDefault(t => EndsWithNestedTypeName(t, typeName));
        }

        private static bool EndsWithNestedTypeName(Type type, string typeName)
        {
            if (type == null || string.IsNullOrEmpty(type.FullName) || string.IsNullOrEmpty(typeName))
            {
                return false;
            }

            return type.FullName.EndsWith("." + typeName, StringComparison.OrdinalIgnoreCase) ||
                   type.FullName.EndsWith("+" + typeName, StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<MethodBase> GetManagedMethodCandidates(Type type, string methodName)
        {
            return GetDeclaredManagedMethods(type)
                .Where(m => MatchesManagedMethodName(m, methodName))
                .OrderBy(m => SafeMetadataToken(m))
                .ThenBy(FormatMethodSignature);
        }

        private static int SafeMetadataToken(MethodBase method)
        {
            try { return method.MetadataToken; }
            catch { return 0; }
        }

        private static bool MatchesManagedMethodName(MethodBase method, string methodName)
        {
            if (method == null || string.IsNullOrEmpty(methodName))
            {
                return false;
            }

            if (method.IsConstructor &&
                (string.Equals(methodName, ".ctor", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(methodName, ".cctor", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(methodName, "#ctor", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(methodName, method.DeclaringType?.Name, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (string.Equals(method.Name, methodName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string declaringType = method.DeclaringType?.FullName;
            if (!string.IsNullOrEmpty(declaringType) &&
                string.Equals(declaringType + "." + method.Name, methodName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string nestedDeclaringType = declaringType?.Replace("+", ".");
            return !string.IsNullOrEmpty(nestedDeclaringType) &&
                   string.Equals(nestedDeclaringType + "." + method.Name, methodName, StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatMethodSignature(MethodBase method)
        {
            string returnType = method is MethodInfo methodInfo ? FormatTypeName(methodInfo.ReturnType) + " " : string.Empty;
            string declaringPrefix = method.DeclaringType == null ? string.Empty : method.DeclaringType.FullName + ".";
            string parameters = string.Join(", ", method.GetParameters().Select(p => FormatTypeName(p.ParameterType) + " " + p.Name));
            return $"{returnType}{declaringPrefix}{method.Name}({parameters})";
        }

        private static string FormatTypeName(Type type)
        {
            if (type == null)
            {
                return null;
            }

            if (type.IsGenericType)
            {
                string genericName = type.FullName ?? type.Name;
                int tickIndex = genericName.IndexOf('`');
                if (tickIndex >= 0)
                {
                    genericName = genericName.Substring(0, tickIndex);
                }
                return $"{genericName}<{string.Join(", ", type.GetGenericArguments().Select(FormatTypeName))}>";
            }

            if (type.IsArray)
            {
                return FormatTypeName(type.GetElementType()) + "[]";
            }
            if (type.IsByRef)
            {
                return FormatTypeName(type.GetElementType()) + "&";
            }
            if (type.IsPointer)
            {
                return FormatTypeName(type.GetElementType()) + "*";
            }

            return type.FullName ?? type.Name;
        }

        private static List<ManagedIlInstructionInfo> DecodeIlInstructions(
            MethodBase method,
            byte[] il,
            int maxInstructions,
            out int totalInstructionCount,
            out bool partial,
            out string error)
        {
            var instructions = new List<ManagedIlInstructionInfo>();
            totalInstructionCount = 0;
            partial = false;
            error = null;
            int offset = 0;
            while (offset < il.Length)
            {
                try
                {
                    var instruction = DecodeIlInstruction(method, il, ref offset);
                    totalInstructionCount++;
                    if (instructions.Count < maxInstructions)
                    {
                        instructions.Add(instruction);
                    }
                    else
                    {
                        partial = true;
                    }
                }
                catch (Exception ex)
                {
                    partial = true;
                    error = "IL decode failed at " + FormatIlOffset(offset) + ": " + ex.Message;
                    break;
                }
            }
            return instructions;
        }

        private static ManagedIlInstructionInfo DecodeIlInstruction(MethodBase method, byte[] il, ref int offset)
        {
            int instructionOffset = offset;
            OpCode opcode = ReadOpCode(il, ref offset);
            var instruction = new ManagedIlInstructionInfo
            {
                Offset = instructionOffset,
                OffsetHex = FormatIlOffset(instructionOffset),
                Opcode = opcode.Name ?? "<unknown>",
                OperandType = opcode.OperandType.ToString()
            };

            switch (opcode.OperandType)
            {
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineI:
                    if (!RequireIlBytes(il, offset, 1, instruction, ref offset)) break;
                    instruction.Operand = ((sbyte)il[offset]).ToString();
                    offset += 1;
                    break;
                case OperandType.InlineI:
                    if (!RequireIlBytes(il, offset, 4, instruction, ref offset)) break;
                    instruction.Operand = BitConverter.ToInt32(il, offset).ToString();
                    offset += 4;
                    break;
                case OperandType.InlineI8:
                    if (!RequireIlBytes(il, offset, 8, instruction, ref offset)) break;
                    instruction.Operand = BitConverter.ToInt64(il, offset).ToString();
                    offset += 8;
                    break;
                case OperandType.ShortInlineR:
                    if (!RequireIlBytes(il, offset, 4, instruction, ref offset)) break;
                    instruction.Operand = BitConverter.ToSingle(il, offset).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    offset += 4;
                    break;
                case OperandType.InlineR:
                    if (!RequireIlBytes(il, offset, 8, instruction, ref offset)) break;
                    instruction.Operand = BitConverter.ToDouble(il, offset).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    offset += 8;
                    break;
                case OperandType.ShortInlineVar:
                    if (!RequireIlBytes(il, offset, 1, instruction, ref offset)) break;
                    instruction.Operand = il[offset].ToString();
                    offset += 1;
                    break;
                case OperandType.InlineVar:
                    if (!RequireIlBytes(il, offset, 2, instruction, ref offset)) break;
                    instruction.Operand = BitConverter.ToUInt16(il, offset).ToString();
                    offset += 2;
                    break;
                case OperandType.ShortInlineBrTarget:
                    if (!RequireIlBytes(il, offset, 1, instruction, ref offset)) break;
                    int shortTarget = offset + 1 + (sbyte)il[offset];
                    instruction.BranchTarget = FormatIlOffset(shortTarget);
                    instruction.Operand = instruction.BranchTarget;
                    offset += 1;
                    break;
                case OperandType.InlineBrTarget:
                    if (!RequireIlBytes(il, offset, 4, instruction, ref offset)) break;
                    int target = offset + 4 + BitConverter.ToInt32(il, offset);
                    instruction.BranchTarget = FormatIlOffset(target);
                    instruction.Operand = instruction.BranchTarget;
                    offset += 4;
                    break;
                case OperandType.InlineSwitch:
                    if (!RequireIlBytes(il, offset, 4, instruction, ref offset)) break;
                    int count = BitConverter.ToInt32(il, offset);
                    offset += 4;
                    if (count < 0 || count > 4096 || !RequireIlBytes(il, offset, count * 4, instruction, ref offset)) break;
                    int switchBase = offset + count * 4;
                    instruction.SwitchTargets = new List<string>();
                    for (int i = 0; i < count; i++)
                    {
                        int relativeTarget = BitConverter.ToInt32(il, offset);
                        instruction.SwitchTargets.Add(FormatIlOffset(switchBase + relativeTarget));
                        offset += 4;
                    }
                    instruction.Operand = string.Join(", ", instruction.SwitchTargets);
                    break;
                case OperandType.InlineString:
                case OperandType.InlineType:
                case OperandType.InlineField:
                case OperandType.InlineMethod:
                case OperandType.InlineTok:
                case OperandType.InlineSig:
                    if (!RequireIlBytes(il, offset, 4, instruction, ref offset)) break;
                    int token = BitConverter.ToInt32(il, offset);
                    instruction.Token = token;
                    instruction.TokenHex = FormatMetadataToken(token);
                    instruction.Operand = instruction.TokenHex;
                    instruction.ResolvedMember = ResolveIlToken(method, opcode.OperandType, token);
                    offset += 4;
                    break;
                default:
                    instruction.Operand = "<unsupported operand type>";
                    break;
            }

            return instruction;
        }

        private static OpCode ReadOpCode(byte[] il, ref int offset)
        {
            if (offset >= il.Length)
            {
                throw new InvalidOperationException("Missing opcode byte");
            }

            byte first = il[offset++];
            if (first != 0xFE)
            {
                var opcode = SingleByteOpCodes[first];
                if (opcode.Name == null)
                {
                    throw new InvalidOperationException($"Unknown opcode 0x{first:X2}");
                }
                return opcode;
            }

            if (offset >= il.Length)
            {
                throw new InvalidOperationException("Missing extended opcode byte");
            }

            byte second = il[offset++];
            var extended = MultiByteOpCodes[second];
            if (extended.Name == null)
            {
                throw new InvalidOperationException($"Unknown opcode 0xFE{second:X2}");
            }
            return extended;
        }

        private static bool RequireIlBytes(byte[] il, int offset, int count, ManagedIlInstructionInfo instruction, ref int currentOffset)
        {
            if (count >= 0 && offset >= 0 && offset <= il.Length && il.Length - offset >= count)
            {
                return true;
            }
            instruction.Operand = "<truncated operand>";
            currentOffset = il.Length;
            return false;
        }

        private static string ResolveIlToken(MethodBase method, OperandType operandType, int token)
        {
            try
            {
                Module module = method.Module;
                Type[] typeArguments = GetSafeGenericArguments(method.DeclaringType);
                Type[] methodArguments = method is MethodInfo methodInfo && methodInfo.IsGenericMethod
                    ? methodInfo.GetGenericArguments()
                    : Type.EmptyTypes;
                switch (operandType)
                {
                    case OperandType.InlineString:
                        return module.ResolveString(token);
                    case OperandType.InlineSig:
                        return "signature " + FormatMetadataToken(token);
                    case OperandType.InlineType:
                        return FormatTypeName(module.ResolveType(token, typeArguments, methodArguments));
                    case OperandType.InlineField:
                        return FormatResolvedMember(module.ResolveField(token, typeArguments, methodArguments));
                    case OperandType.InlineMethod:
                    case OperandType.InlineTok:
                        return FormatResolvedMember(module.ResolveMember(token, typeArguments, methodArguments));
                    default:
                        return null;
                }
            }
            catch (Exception ex)
            {
                return "unresolved " + FormatMetadataToken(token) + ": " + ex.GetType().Name + ": " + ex.Message;
            }
        }

        private static Type[] GetSafeGenericArguments(Type type)
        {
            try { return type != null && type.IsGenericType ? type.GetGenericArguments() : Type.EmptyTypes; }
            catch { return Type.EmptyTypes; }
        }

        private static string FormatResolvedMember(MemberInfo member)
        {
            if (member == null) return null;
            if (member is Type type) return FormatTypeName(type);
            if (member is MethodBase method) return FormatMethodSignature(method);
            if (member is FieldInfo field) return $"{FormatTypeName(field.FieldType)} {field.DeclaringType?.FullName}.{field.Name}";
            return member.ToString();
        }

        private static string FormatMetadataToken(int token)
        {
            return "0x" + token.ToString("X8");
        }

        private static string FormatIlOffset(int offset)
        {
            return "IL_" + Math.Max(0, offset).ToString("X4");
        }

        private static string CombineErrors(params string[] errors)
        {
            var nonEmpty = errors
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Distinct()
                .ToList();
            return nonEmpty.Count == 0 ? null : string.Join("; ", nonEmpty);
        }

        private static OpCode[] BuildOpCodeMap(bool multiByte)
        {
            var map = new OpCode[256];
            foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (!(field.GetValue(null) is OpCode opcode))
                {
                    continue;
                }
                ushort value = unchecked((ushort)opcode.Value);
                if (multiByte)
                {
                    if ((value & 0xFF00) == 0xFE00)
                    {
                        map[value & 0xFF] = opcode;
                    }
                }
                else if (value < 0x0100)
                {
                    map[value] = opcode;
                }
            }
            return map;
        }
    }

    internal sealed class ManagedInspectionRequest
    {
        public string Operation { get; set; }
        public string AssemblyPath { get; set; }
        public string TypeFilter { get; set; }
        public string TypeName { get; set; }
        public string MethodName { get; set; }
        public int MaxTypes { get; set; }
        public int MaxMethodsPerType { get; set; }
        public int MaxMethods { get; set; }
        public int OverloadIndex { get; set; }
        public int MaxInstructions { get; set; }
        public List<string> ProbeDirectories { get; set; }
    }
}
