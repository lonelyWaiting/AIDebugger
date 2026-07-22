using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;
using Mono.Cecil;
using Mono.Cecil.Cil;
using VSDebuggerMCP.Protocol;

namespace ManagedInspectionHelper
{
    /// <summary>
    /// Reads ECMA-335 metadata and IL directly from PE files. Target assemblies
    /// are never loaded into the CLR, so module initializers and dependency code
    /// cannot execute in the helper process.
    /// </summary>
    internal static class Program
    {
        private const string ReadOnlyCapability = "appcontainer_managed_metadata_and_il_read_only";
        private const long MaxAssemblyBytes = 64L * 1024L * 1024L;
        private const int MaxMetadataTypes = 100000;
        private const int MaxResponsePayloadBytes = 3 * 1024 * 1024;
        private const int MaxOutputTextChars = 1024;
        private const int MaxOutputParameters = 32;
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        private static int Main(string[] args)
        {
            try
            {
                using (FileStream assemblyStream = OpenBrokeredAssembly(args))
                {
                string requestJson = Console.In.ReadToEnd();
                var request = Serializer.Deserialize<ManagedInspectionRequest>(requestJson);
                if (request == null || string.IsNullOrWhiteSpace(request.Operation))
                {
                    Console.Error.WriteLine("Invalid managed inspection request");
                    return 2;
                }

                object result;
                switch (request.Operation)
                {
                    case "inspect_managed_assembly":
                        result = InspectManagedAssembly(request, assemblyStream);
                        break;
                    case "inspect_managed_type":
                        result = InspectManagedType(request, assemblyStream);
                        break;
                    case "inspect_managed_method_il":
                        result = InspectManagedMethodIl(request, assemblyStream);
                        break;
                    default:
                        Console.Error.WriteLine("Unknown managed inspection operation: " + request.Operation);
                        return 3;
                }

                Console.Out.Write(Serializer.Serialize(result));
                return 0;
                }
            }
            catch (Exception ex)
            {
                // Never emit exception messages or stacks: the parent process may
                // surface stderr through a remote protocol and paths can be sensitive.
                Console.Error.WriteLine("MANAGED_INSPECTION_FAILED:" + ex.GetType().Name);
                return 1;
            }
        }

        private static ManagedAssemblyResult InspectManagedAssembly(ManagedInspectionRequest request, Stream assemblyStream)
        {
            string path = NormalizeAssemblyPath(request.AssemblyPath);
            string filter = string.IsNullOrWhiteSpace(request.TypeFilter) ? null : request.TypeFilter.Trim();
            int maxTypes = Clamp(request.MaxTypes, 1, 128);
            int maxMethods = Clamp(request.MaxMethodsPerType, 0, 32);
            var result = new ManagedAssemblyResult
            {
                AssemblyPath = path,
                TypeFilter = filter,
                Capability = ReadOnlyCapability,
            };

            if (!ValidateAssemblyStream(assemblyStream, out string pathError))
            {
                result.Error = pathError;
                result.Partial = true;
                return result;
            }

            try
            {
                using (var context = OpenAssembly(assemblyStream))
                {
                    result.AssemblyFullName = context.Assembly.FullName;
                    result.ModuleVersionId = context.Assembly.MainModule.Mvid.ToString("D");
                    var allTypes = EnumerateTypes(context.Assembly).ToList();
                    result.TotalTypeCount = allTypes.Count;
                    var matches = allTypes
                        .Where(type => MatchesTypeFilter(type, filter))
                        .OrderBy(GetFullTypeName, StringComparer.Ordinal)
                        .ToList();
                    result.MatchedTypeCount = matches.Count;
                    int outputBytes = 32768;
                    foreach (var type in matches.Take(maxTypes))
                    {
                        var typeInfo = BuildTypeInfo(type, context.Assembly, maxMethods);
                        if (!TryAddWithinBudget(result.Types, typeInfo, ref outputBytes)) break;
                    }
                    result.Partial = matches.Count > result.Types.Count;
                }
            }
            catch (Exception ex)
            {
                result.Partial = true;
                result.Error = SanitizeMetadataError(ex);
            }

            return result;
        }

        private static ManagedTypeResult InspectManagedType(ManagedInspectionRequest request, Stream assemblyStream)
        {
            string path = NormalizeAssemblyPath(request.AssemblyPath);
            string typeName = string.IsNullOrWhiteSpace(request.TypeName) ? null : request.TypeName.Trim();
            var result = new ManagedTypeResult
            {
                AssemblyPath = path,
                TypeName = typeName,
                Capability = ReadOnlyCapability,
            };

            if (!ValidateAssemblyStream(assemblyStream, out string pathError))
            {
                result.Error = pathError;
                return result;
            }
            if (string.IsNullOrEmpty(typeName))
            {
                result.Error = "typeName is required";
                return result;
            }

            try
            {
                using (var context = OpenAssembly(assemblyStream))
                {
                    var type = FindType(context.Assembly, typeName);
                    if (type == null)
                    {
                        result.Error = "Type not found: " + typeName;
                    }
                    else
                    {
                        result.Type = BuildTypeInfo(type, context.Assembly, Clamp(request.MaxMethods, 0, 32));
                    }
                }
            }
            catch (Exception ex)
            {
                result.Error = SanitizeMetadataError(ex);
            }

            return result;
        }

        private static ManagedMethodIlResult InspectManagedMethodIl(ManagedInspectionRequest request, Stream assemblyStream)
        {
            string path = NormalizeAssemblyPath(request.AssemblyPath);
            string typeName = string.IsNullOrWhiteSpace(request.TypeName) ? null : request.TypeName.Trim();
            string methodName = string.IsNullOrWhiteSpace(request.MethodName) ? null : request.MethodName.Trim();
            int overloadIndex = Math.Max(0, request.OverloadIndex);
            int maxInstructions = Clamp(request.MaxInstructions, 1, 2048);
            var result = new ManagedMethodIlResult
            {
                AssemblyPath = path,
                TypeName = typeName,
                MethodName = methodName,
                OverloadIndex = overloadIndex,
                Capability = ReadOnlyCapability,
            };

            if (!ValidateAssemblyStream(assemblyStream, out string pathError))
            {
                result.Partial = true;
                result.Error = pathError;
                return result;
            }
            if (string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(methodName))
            {
                result.Partial = true;
                result.Error = "typeName and methodName are required";
                return result;
            }

            try
            {
                using (var context = OpenAssembly(assemblyStream))
                {
                    var type = FindType(context.Assembly, typeName);
                    if (type == null)
                    {
                        result.Partial = true;
                        result.Error = "Type not found: " + typeName;
                        return result;
                    }

                    var candidates = type.Methods
                        .Where(candidate => MatchesMethodName(candidate, methodName))
                        .OrderBy(candidate => candidate.MetadataToken.ToInt32())
                        .ToList();
                    result.MatchedOverloadCount = candidates.Count;
                    if (candidates.Count == 0)
                    {
                        result.Partial = true;
                        result.Error = "Method not found: " + methodName;
                        return result;
                    }
                    if (overloadIndex >= candidates.Count)
                    {
                        result.Partial = true;
                        result.Error = $"overloadIndex {overloadIndex} is out of range; matched {candidates.Count} overload(s)";
                        return result;
                    }

                    var method = candidates[overloadIndex];
                    result.Method = BuildMethodInfo(method);
                    if (!method.HasBody || method.Body == null)
                    {
                        result.Partial = true;
                        result.Error = "Method body unavailable; method may be abstract, extern, or runtime-provided";
                        return result;
                    }

                    int outputBytes = 65536;
                    foreach (var variable in method.Body.Variables.Take(1024))
                    {
                        var local = new ManagedLocalInfo
                        {
                            Index = variable.Index,
                            Type = LimitText(FormatTypeName(UnwrapPinnedType(variable.VariableType))),
                            IsPinned = variable.VariableType is PinnedType,
                        };
                        if (!TryAddWithinBudget(result.Locals, local, ref outputBytes)) break;
                    }

                    var instructions = method.Body.Instructions;
                    result.TotalInstructionCount = instructions.Count;
                    foreach (var instruction in instructions.Take(maxInstructions))
                    {
                        var instructionInfo = BuildInstructionInfo(instruction);
                        if (!TryAddWithinBudget(result.Instructions, instructionInfo, ref outputBytes)) break;
                    }
                    result.Partial = instructions.Count > result.Instructions.Count ||
                        method.Body.Variables.Count > result.Locals.Count ||
                        result.Instructions.Any(instruction => instruction.SwitchTargetsPartial);
                }
            }
            catch (Exception ex)
            {
                result.Partial = true;
                result.Error = SanitizeMetadataError(ex);
            }

            return result;
        }

        private static AssemblyContext OpenAssembly(Stream assemblyStream)
        {
            var resolver = new DefaultAssemblyResolver();
            var parameters = new ReaderParameters
            {
                AssemblyResolver = resolver,
                InMemory = true,
                ReadSymbols = false,
                ReadingMode = ReadingMode.Immediate,
                ReadWrite = false,
            };
            try
            {
                assemblyStream.Position = 0;
                return new AssemblyContext(AssemblyDefinition.ReadAssembly(assemblyStream, parameters), resolver);
            }
            catch
            {
                resolver.Dispose();
                throw;
            }
        }

        private static IEnumerable<TypeDefinition> EnumerateTypes(AssemblyDefinition assembly)
        {
            int visited = 0;
            foreach (var module in assembly.Modules)
            {
                var pending = new Stack<TypeDefinition>(module.Types.Reverse());
                while (pending.Count > 0)
                {
                    var type = pending.Pop();
                    if (++visited > MaxMetadataTypes)
                    {
                        throw new InvalidDataException(
                            $"Assembly metadata exceeds the {MaxMetadataTypes}-type traversal limit.");
                    }
                    yield return type;
                    for (int index = type.NestedTypes.Count - 1; index >= 0; index--)
                    {
                        pending.Push(type.NestedTypes[index]);
                    }
                }
            }
        }

        private static TypeDefinition FindType(AssemblyDefinition assembly, string typeName)
        {
            return EnumerateTypes(assembly).FirstOrDefault(type =>
                string.Equals(GetFullTypeName(type), typeName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(GetFullTypeName(type).Replace("+", "."), typeName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type.Name, typeName, StringComparison.OrdinalIgnoreCase));
        }

        private static bool MatchesTypeFilter(TypeDefinition type, string filter)
        {
            return string.IsNullOrEmpty(filter) ||
                   ContainsIgnoreCase(GetFullTypeName(type), filter) ||
                   ContainsIgnoreCase(type.Name, filter) ||
                   ContainsIgnoreCase(type.Namespace, filter);
        }

        private static bool MatchesMethodName(MethodDefinition method, string methodName)
        {
            if (string.Equals(method.Name, methodName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (method.IsConstructor &&
                (string.Equals(methodName, ".ctor", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(methodName, ".cctor", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(methodName, "#ctor", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(methodName, method.DeclaringType.Name, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
            string fullName = GetFullTypeName(method.DeclaringType);
            return string.Equals(fullName + "." + method.Name, methodName, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(fullName.Replace("+", ".") + "." + method.Name, methodName, StringComparison.OrdinalIgnoreCase);
        }

        private static ManagedTypeInfo BuildTypeInfo(TypeDefinition type, AssemblyDefinition assembly, int maxMethods)
        {
            var info = new ManagedTypeInfo
            {
                Name = LimitText(type.Name),
                FullName = LimitText(GetFullTypeName(type)),
                NamespaceName = LimitText(type.Namespace),
                AssemblyQualifiedName = LimitText(GetFullTypeName(type) + ", " + assembly.Name.FullName),
                BaseType = LimitText(FormatTypeName(type.BaseType)),
                IsPublic = type.IsPublic || type.IsNestedPublic,
                IsInterface = type.IsInterface,
                IsEnum = type.IsEnum,
                IsValueType = type.IsValueType,
            };
            var methods = type.Methods
                .OrderBy(method => method.Name, StringComparer.Ordinal)
                .ThenBy(method => method.MetadataToken.ToInt32())
                .ToList();
            int outputBytes = 16384;
            foreach (var method in methods.Take(maxMethods))
            {
                var methodInfo = BuildMethodInfo(method);
                if (!TryAddWithinBudget(info.Methods, methodInfo, ref outputBytes)) break;
            }
            info.Partial = methods.Count > info.Methods.Count;
            return info;
        }

        private static ManagedMethodInfo BuildMethodInfo(MethodDefinition method)
        {
            var info = new ManagedMethodInfo
            {
                Name = LimitText(method.Name),
                Signature = LimitText(FormatMethodSignature(method)),
                DeclaringType = LimitText(GetFullTypeName(method.DeclaringType)),
                ReturnType = method.IsConstructor ? null : LimitText(FormatTypeName(method.ReturnType)),
                MetadataToken = method.MetadataToken.ToInt32(),
                MetadataTokenHex = FormatMetadataToken(method.MetadataToken.ToInt32()),
                IsPublic = method.IsPublic,
                IsStatic = method.IsStatic,
                IsConstructor = method.IsConstructor,
            };
            foreach (var parameter in method.Parameters.Take(MaxOutputParameters))
            {
                info.Parameters.Add(new ManagedParameterInfo
                {
                    Name = LimitText(parameter.Name),
                    Type = LimitText(FormatTypeName(parameter.ParameterType)),
                    Position = parameter.Index,
                });
            }
            info.ParametersPartial = method.Parameters.Count > info.Parameters.Count;
            return info;
        }

        private static ManagedIlInstructionInfo BuildInstructionInfo(Instruction instruction)
        {
            var info = new ManagedIlInstructionInfo
            {
                Offset = instruction.Offset,
                OffsetHex = FormatIlOffset(instruction.Offset),
                Opcode = instruction.OpCode.Name ?? "<unknown>",
                OperandType = instruction.OpCode.OperandType.ToString(),
            };

            if (instruction.Operand is Instruction branch)
            {
                info.BranchTarget = FormatIlOffset(branch.Offset);
                info.Operand = info.BranchTarget;
            }
            else if (instruction.Operand is Instruction[] targets)
            {
                info.SwitchTargets = targets.Take(256).Select(target => FormatIlOffset(target.Offset)).ToList();
                info.SwitchTargetsPartial = targets.Length > info.SwitchTargets.Count;
                info.Operand = string.Join(", ", info.SwitchTargets);
            }
            else if (instruction.Operand is IMetadataTokenProvider tokenProvider)
            {
                int token = tokenProvider.MetadataToken.ToInt32();
                info.Token = token;
                info.TokenHex = FormatMetadataToken(token);
                info.Operand = info.TokenHex;
                info.ResolvedMember = FormatMetadataOperand(instruction.Operand);
            }
            else if (instruction.Operand is string text)
            {
                info.Operand = LimitText(text);
                info.ResolvedMember = LimitText(text);
            }
            else if (instruction.Operand != null)
            {
                info.Operand = LimitText(Convert.ToString(instruction.Operand, System.Globalization.CultureInfo.InvariantCulture));
            }
            info.Operand = LimitText(info.Operand);
            info.ResolvedMember = LimitText(info.ResolvedMember);
            return info;
        }

        private static bool TryAddWithinBudget<T>(ICollection<T> destination, T value, ref int usedBytes)
        {
            int bytes = Encoding.UTF8.GetByteCount(Serializer.Serialize(value));
            if (bytes > MaxResponsePayloadBytes - usedBytes) return false;
            destination.Add(value);
            usedBytes += bytes;
            return true;
        }

        private static string LimitText(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= MaxOutputTextChars) return value;
            return value.Substring(0, MaxOutputTextChars - 12) + "...<truncated>";
        }

        private static string FormatMetadataOperand(object operand)
        {
            if (operand is MethodReference method) return FormatMethodSignature(method);
            if (operand is FieldReference field) return FormatTypeName(field.FieldType) + " " + GetFullTypeName(field.DeclaringType) + "." + field.Name;
            if (operand is TypeReference type) return FormatTypeName(type);
            if (operand is CallSite callSite) return FormatTypeName(callSite.ReturnType) + " (" +
                string.Join(", ", callSite.Parameters.Take(MaxOutputParameters).Select(p => FormatTypeName(p.ParameterType))) +
                (callSite.Parameters.Count > MaxOutputParameters ? ", ..." : string.Empty) + ")";
            return operand == null ? null : operand.ToString();
        }

        private static string FormatMethodSignature(MethodReference method)
        {
            string returnType = method.Name == ".ctor" || method.Name == ".cctor" ? string.Empty : FormatTypeName(method.ReturnType) + " ";
            string declaring = method.DeclaringType == null ? string.Empty : GetFullTypeName(method.DeclaringType) + ".";
            string parameters = string.Join(", ", method.Parameters.Take(MaxOutputParameters).Select(parameter =>
                FormatTypeName(parameter.ParameterType) + (string.IsNullOrEmpty(parameter.Name) ? string.Empty : " " + parameter.Name)));
            if (method.Parameters.Count > MaxOutputParameters) parameters += ", ...";
            return returnType + declaring + method.Name + "(" + parameters + ")";
        }

        private static string FormatTypeName(TypeReference type)
        {
            return FormatTypeName(type, 0);
        }

        private static string FormatTypeName(TypeReference type, int depth)
        {
            if (type == null) return null;
            if (depth >= 32) return "<type-depth-limit>";
            if (type is GenericInstanceType generic)
            {
                string name = GetFullTypeName(generic.ElementType);
                int tick = name.IndexOf('`');
                if (tick >= 0) name = name.Substring(0, tick);
                return name + "<" + string.Join(", ", generic.GenericArguments.Take(MaxOutputParameters)
                    .Select(argument => FormatTypeName(argument, depth + 1))) +
                    (generic.GenericArguments.Count > MaxOutputParameters ? ", ..." : string.Empty) + ">";
            }
            if (type is ArrayType array) return FormatTypeName(array.ElementType, depth + 1) + "[" + new string(',', Math.Min(31, Math.Max(0, array.Rank - 1))) + "]";
            if (type is ByReferenceType byRef) return FormatTypeName(byRef.ElementType, depth + 1) + "&";
            if (type is PointerType pointer) return FormatTypeName(pointer.ElementType, depth + 1) + "*";
            if (type is OptionalModifierType optional) return FormatTypeName(optional.ElementType, depth + 1);
            if (type is RequiredModifierType required) return FormatTypeName(required.ElementType, depth + 1);
            if (type is GenericParameter parameter) return parameter.Name;
            return GetFullTypeName(type);
        }

        private static TypeReference UnwrapPinnedType(TypeReference type)
        {
            return type is PinnedType pinned ? pinned.ElementType : type;
        }

        private static string GetFullTypeName(TypeReference type)
        {
            return type == null ? null : (type.FullName ?? type.Name).Replace("/", "+");
        }

        private static bool ValidateAssemblyStream(Stream assemblyStream, out string error)
        {
            if (assemblyStream == null || !assemblyStream.CanRead || !assemblyStream.CanSeek)
            {
                error = "Brokered assembly handle is unavailable";
                return false;
            }
            long length = assemblyStream.Length;
            if (length <= 0 || length > MaxAssemblyBytes)
            {
                error = $"Assembly size {length} bytes is outside the allowed range 1..{MaxAssemblyBytes}";
                return false;
            }
            error = null;
            return true;
        }

        private static FileStream OpenBrokeredAssembly(string[] args)
        {
            if (args == null || args.Length != 2 || args[0] != "--assembly-handle" ||
                !long.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out long handleValue) ||
                handleValue <= 0)
            {
                throw new InvalidDataException("A brokered assembly handle is required");
            }
            var handle = new SafeFileHandle(new IntPtr(handleValue), ownsHandle: true);
            try
            {
                var stream = new FileStream(handle, FileAccess.Read, 4096, false);
                if (!stream.CanRead || !stream.CanSeek)
                {
                    stream.Dispose();
                    throw new InvalidDataException("Brokered assembly handle is not a seekable file");
                }
                return stream;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        private static string NormalizeAssemblyPath(string assemblyPath)
        {
            if (string.IsNullOrWhiteSpace(assemblyPath)) return null;
            try { return Path.GetFullPath(assemblyPath.Trim().Trim('"')); }
            catch { return assemblyPath.Trim().Trim('"'); }
        }

        private static IEnumerable<string> NormalizeProbeDirectories(IEnumerable<string> directories, string assemblyPath)
        {
            var result = new List<string>();
            AddProbeDirectory(result, Path.GetDirectoryName(assemblyPath));
            if (directories != null)
            {
                foreach (string directory in directories) AddProbeDirectory(result, directory);
            }
            return result;
        }

        private static void AddProbeDirectory(ICollection<string> directories, string directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) return;
            try
            {
                string path = Path.GetFullPath(directory);
                if (Directory.Exists(path) && !directories.Contains(path, StringComparer.OrdinalIgnoreCase)) directories.Add(path);
            }
            catch { }
        }

        private static bool ContainsIgnoreCase(string value, string expected)
        {
            return !string.IsNullOrEmpty(value) && value.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Max(minimum, Math.Min(value, maximum));
        }

        private static string FormatMetadataToken(int token) => "0x" + token.ToString("X8");
        private static string FormatIlOffset(int offset) => "IL_" + Math.Max(0, offset).ToString("X4");

        private static string SanitizeMetadataError(Exception exception)
        {
            return exception.GetType().Name + ": " + exception.Message;
        }

        private sealed class AssemblyContext : IDisposable
        {
            public AssemblyContext(AssemblyDefinition assembly, DefaultAssemblyResolver resolver)
            {
                Assembly = assembly;
                Resolver = resolver;
            }

            public AssemblyDefinition Assembly { get; }
            private DefaultAssemblyResolver Resolver { get; }

            public void Dispose()
            {
                Assembly.Dispose();
                Resolver.Dispose();
            }
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
