using System.Globalization;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Luau.Tests;

public sealed class PublicApiContractTests
{
    const BindingFlags PublicDeclared =
        BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    const BindingFlags ContractDeclared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    const string BaselineResource = "Luau.Tests.PublicApi.approved.txt";

    [Fact]
    public void ManagedPublicApiMatchesApprovedBaseline()
    {
        var actual = Snapshot(typeof(LuauState).Assembly);

        // Deliberate maintainer-only refresh; ordinary test runs are read-only.
        if (Environment.GetEnvironmentVariable("LUAU_UPDATE_PUBLIC_API") == "1")
        {
            File.WriteAllLines(BaselinePath(), actual, new UTF8Encoding(false));
            return;
        }

        using var stream = typeof(PublicApiContractTests).Assembly.GetManifestResourceStream(BaselineResource);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        var expected = reader.ReadToEnd()
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.True(expected.SequenceEqual(actual, StringComparer.Ordinal), Diff(expected, actual));
    }

    [Fact]
    public void ManagedPublicApiContainsNoNativeSurface()
    {
        var assembly = typeof(LuauState).Assembly;
        var types = assembly.GetExportedTypes();
        var leaks = types
            .Where(type => IsNativeType(type) ||
                new[] { type.BaseType }.Where(candidate => candidate != null).Cast<Type>()
                    .Concat(type.GetInterfaces()).Any(IsNativeType))
            .Select(type => $"type {FormatType(type)}")
            .Concat(types
                .SelectMany(type => PublicMembers(type).Where(member => SignatureTypes(member).Any(IsNativeType)))
                .Select(FormatMember))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(leaks.Length == 0, string.Join(Environment.NewLine, leaks));
    }

    [Fact]
    public void InteropAssemblyExportsNoPublicTypes()
    {
        var reference = Assert.Single(
            typeof(LuauState).Assembly.GetReferencedAssemblies(),
            name => name.Name == "Luau.Interop");

        Assert.Empty(Assembly.Load(reference).ExportedTypes);
    }

    [Fact]
    public void RemovedRawAndAmbiguousEntryPointsStayRemoved()
    {
        var stateMethods = typeof(LuauState).GetMethods(PublicDeclared);
        string[] forbiddenNames =
        [
            "OpenLibraries", "Sandbox", "Call", "GetTop", "SetTop", "GetAbsIndex",
            "Insert", "Replace", "Remove", "CheckStack", "GetLuauType", "ToValue",
            "ToBoolean", "ToNumber", "ToInteger", "ToVector", "ToStringUtf8",
            "ToTable", "ToFunction", "ToUserData", "ToBuffer", "ToThread", "ToPointer",
            "ToCFunction", "ToLightUserData", "Push", "PushNil", "PushBoolean",
            "PushNumber", "PushInteger", "PushVector", "PushString", "PushThread",
            "PushTable", "PushFunction", "PushUserData", "PushBuffer", "PushCClosure",
            "PushCFunction", "PushLightUserData", "Pop", "XMove",
        ];

        Assert.DoesNotContain(stateMethods, method => forbiddenNames.Contains(method.Name));
        Assert.All(
            stateMethods.Where(method => method.Name is "CreateFunction" or "CreateAsyncFunction"),
            method =>
            {
                var callback = Assert.Single(
                    method.GetParameters(),
                    parameter => typeof(Delegate).IsAssignableFrom(parameter.ParameterType));
                var invoke = callback.ParameterType.GetMethod("Invoke")!;
                Assert.Equal(typeof(LuauCallContext), Assert.Single(invoke.GetParameters()).ParameterType);
                Assert.NotEqual(typeof(int), invoke.ReturnType);
                Assert.NotEqual(typeof(ValueTask<int>), invoke.ReturnType);
            });
    }

    [Fact]
    public void ResultDestinationsUseOnlyIntoNamesAndResumeScopeBindingIsUnambiguous()
    {
        var methods = typeof(LuauState).GetMethods(PublicDeclared);
        string[] allocatingNames =
        [
            "Resume", "ResumeAsync", "DoString", "DoStringAsync",
            "ExecuteCompilerOutput", "ExecuteCompilerOutputAsync",
            "ExecuteVerifiedBytecode", "ExecuteVerifiedBytecodeAsync",
        ];

        Assert.DoesNotContain(
            methods.Where(method => allocatingNames.Contains(method.Name, StringComparer.Ordinal)),
            method => method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(Span<LuauValue>) ||
                parameter.ParameterType == typeof(Memory<LuauValue>)));

        string[] intoNames =
        [
            "ResumeInto", "ResumeIntoAsync", "DoStringInto", "DoStringIntoAsync",
            "ExecuteCompilerOutputInto", "ExecuteCompilerOutputIntoAsync",
            "ExecuteVerifiedBytecodeInto", "ExecuteVerifiedBytecodeIntoAsync",
        ];
        Assert.All(intoNames, name => Assert.Contains(methods, method => method.Name == name));

        var resume = Assert.Single(methods, method => method.Name == "Resume");
        Assert.Equal(typeof(LuauResultScope), resume.ReturnType);
        Assert.Equal(typeof(ReadOnlySpan<LuauValue>), resume.GetParameters()[0].ParameterType);

        var resumeAsync = Assert.Single(methods, method => method.Name == "ResumeAsync");
        Assert.Equal(typeof(ValueTask<LuauResultScope>), resumeAsync.ReturnType);
        Assert.Equal(typeof(ReadOnlyMemory<LuauValue>), resumeAsync.GetParameters()[0].ParameterType);
    }

    [Fact]
    public void FunctionInvocationAndCoroutineLifecycleHaveClosedPublicShapes()
    {
        var functionMethods = typeof(LuauFunction).GetMethods(PublicDeclared);
        var invoke = Assert.Single(functionMethods, method => method.Name == "Invoke");
        var invokeAsync = Assert.Single(functionMethods, method => method.Name == "InvokeAsync");

        Assert.Equal(typeof(LuauResultScope), invoke.ReturnType);
        Assert.Equal(typeof(ReadOnlySpan<LuauValue>), invoke.GetParameters()[0].ParameterType);
        Assert.Equal(typeof(ValueTask<LuauResultScope>), invokeAsync.ReturnType);
        Assert.Equal(typeof(ReadOnlyMemory<LuauValue>), invokeAsync.GetParameters()[0].ParameterType);
        Assert.All(
            typeof(LuauFunction).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            constructor => Assert.False(
                constructor.IsPublic || constructor.IsFamily || constructor.IsFamilyOrAssembly,
                "LuauFunction must not expose a constructor usable by external subclasses."));
        Assert.DoesNotContain(
            typeof(LuauFunction).Assembly.GetExportedTypes(),
            type => type != typeof(LuauFunction) && typeof(LuauFunction).IsAssignableFrom(type));
        Assert.Null(typeof(LuauFunction).Assembly.GetType("Luau.LuauFunctionExtensions"));

        Assert.Equal(
            [nameof(LuauThreadStatus.Suspended), nameof(LuauThreadStatus.Running), nameof(LuauThreadStatus.Dead)],
            Enum.GetNames<LuauThreadStatus>());
        Assert.Null(typeof(LuauTable).GetProperty("Count", PublicDeclared));
        Assert.Equal(typeof(int), typeof(LuauTable).GetProperty("Length", PublicDeclared)!.PropertyType);
        Assert.Equal(typeof(int), typeof(LuauTable).GetProperty("EntryCount", PublicDeclared)!.PropertyType);
    }

    [Fact]
    public void BytecodeCapabilitiesAreSeparatedAndRawLoadingIsNotPublic()
    {
        var names = typeof(LuauState).GetMethods(PublicDeclared)
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("LoadCompilerOutput", names);
        Assert.Contains("ExecuteCompilerOutput", names);
        Assert.Contains("ExecuteCompilerOutputAsync", names);
        Assert.Contains("LoadVerifiedBytecode", names);
        Assert.Contains("ExecuteVerifiedBytecode", names);
        Assert.Contains("ExecuteVerifiedBytecodeAsync", names);
        Assert.DoesNotContain("Load", names);
        Assert.DoesNotContain("Execute", names);
        Assert.DoesNotContain("ExecuteAsync", names);
        Assert.DoesNotContain("LoadTrustedBytecode", names);
        Assert.DoesNotContain("ExecuteTrustedBytecode", names);
        Assert.DoesNotContain("ExecuteTrustedBytecodeAsync", names);
        Assert.DoesNotContain("LoadBytecode", names);
        Assert.DoesNotContain("ExecuteBytecode", names);
        Assert.DoesNotContain("ExecuteBytecodeAsync", names);

        Assert.Empty(typeof(LuauCompilerOutput).GetConstructors(PublicDeclared));
        Assert.Empty(typeof(LuauCompileResult).GetConstructors(PublicDeclared));
        var compile = Assert.Single(typeof(LuauCompiler).GetMethods(PublicDeclared));
        Assert.Equal(typeof(LuauCompilerOutput), compile.ReturnType);
        Assert.Equal(
            EditorBrowsableState.Advanced,
            compile.GetCustomAttribute<EditorBrowsableAttribute>()?.State);

        var serviceCompile = Assert.Single(
            typeof(ILuauCompilationService).GetMethods(PublicDeclared));
        Assert.Equal(typeof(ValueTask<LuauCompileResult>), serviceCompile.ReturnType);

        var factories = typeof(LuauCompileResult).GetMethods(PublicDeclared)
            .Where(method => method.IsStatic)
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains(nameof(LuauCompileResult.Success), factories);
        Assert.Contains(nameof(LuauCompileResult.Diagnostic), factories);
        Assert.Contains(nameof(LuauCompileResult.Canceled), factories);
        Assert.Contains(nameof(LuauCompileResult.InfrastructureFailure), factories);
        Assert.NotNull(typeof(LuauCompileResult).GetProperty(
            nameof(LuauCompileResult.CompilationDiagnostic),
            PublicDeclared));
    }

    static string[] Snapshot(Assembly assembly) => assembly.GetExportedTypes()
        .SelectMany(type => new[] { FormatTypeDeclaration(type) }.Concat(PublicMembers(type).Select(FormatMember)))
        .Order(StringComparer.Ordinal)
        .ToArray();

    static MemberInfo[] PublicMembers(Type type)
    {
        var includeProtected = type.IsClass &&
            !type.IsSealed &&
            type.GetConstructors(ContractDeclared).Any(constructor =>
                constructor.IsPublic || constructor.IsFamily || constructor.IsFamilyOrAssembly);
        return type.GetConstructors(ContractDeclared)
            // Delegate constructors are CLR implementation machinery with an
            // object/IntPtr signature, not callable source-level API. Keep the
            // delegate's Invoke method in the inventory while excluding that
            // synthetic native-looking constructor.
            .Where(constructor =>
                !typeof(MulticastDelegate).IsAssignableFrom(type.BaseType) &&
                IsContractVisible(constructor, includeProtected))
            .Cast<MemberInfo>()
            .Concat(type.GetMethods(ContractDeclared).Where(method =>
                IsContractVisible(method, includeProtected) &&
                (!method.IsSpecialName || method.Name.StartsWith("op_", StringComparison.Ordinal))))
            .Concat(type.GetProperties(ContractDeclared).Where(property =>
                property.GetAccessors(nonPublic: true)
                    .Any(accessor => IsContractVisible(accessor, includeProtected))))
            .Concat(type.GetEvents(ContractDeclared).Where(@event =>
                new[] { @event.AddMethod, @event.RemoveMethod, @event.RaiseMethod }
                    .Where(accessor => accessor != null)
                    .Cast<MethodInfo>()
                    .Any(accessor => IsContractVisible(accessor, includeProtected))))
            .Concat(type.GetFields(ContractDeclared).Where(field =>
                !field.IsSpecialName &&
                (field.IsPublic || includeProtected && (field.IsFamily || field.IsFamilyOrAssembly))))
            .OrderBy(FormatMember, StringComparer.Ordinal)
            .ToArray();
    }

    static string FormatTypeDeclaration(Type type)
    {
        var kind = type.IsEnum ? "enum"
            : type.IsInterface ? "interface"
            : typeof(MulticastDelegate).IsAssignableFrom(type.BaseType) ? "delegate"
            : type.IsValueType ? type.IsByRefLike ? "ref struct"
                : type.IsDefined(typeof(IsReadOnlyAttribute)) ? "readonly struct" : "struct"
            : type.IsAbstract && type.IsSealed ? "static class"
            : type.IsAbstract ? "abstract class"
            : type.IsSealed ? "sealed class"
            : "class";
        var bases = new[] { type.BaseType }
            .Where(candidate => candidate != null &&
                candidate != typeof(object) && candidate != typeof(ValueType) &&
                candidate != typeof(Enum) && candidate != typeof(MulticastDelegate))
            .Cast<Type>()
            .Concat(type.GetInterfaces().Where(IsExternallyVisible))
            .Distinct()
            .OrderBy(FormatType, StringComparer.Ordinal)
            .Select(FormatType)
            .ToArray();
        return $"type {kind} {FormatType(type)}" +
            (bases.Length == 0 ? string.Empty : $" : {string.Join(", ", bases)}");
    }

    static string FormatMember(MemberInfo member)
    {
        var kind = member switch
        {
            ConstructorInfo => "ctor",
            MethodInfo => "method",
            PropertyInfo => "property",
            EventInfo => "event",
            FieldInfo => "field",
            _ => throw new ArgumentOutOfRangeException(nameof(member)),
        };
        var detail = member switch
        {
            PropertyInfo property => $"{property} {{ " +
                FormatAccessor(property.GetMethod, "get") +
                FormatAccessor(property.SetMethod, "set") + "}",
            FieldInfo field when field.IsLiteral =>
                $"{field} = {FormatConstant(field.GetRawConstantValue(), field.FieldType)}",
            _ => member.ToString()!,
        };
        return $"{FormatVisibility(member)}{kind} {FormatType(member.DeclaringType!)} :: {detail}";
    }

    static bool IsContractVisible(MethodBase method, bool includeProtected) =>
        method.IsPublic || includeProtected && (method.IsFamily || method.IsFamilyOrAssembly);

    static string FormatVisibility(MemberInfo member)
    {
        return member switch
        {
            MethodBase method => FormatVisibility(method),
            FieldInfo field when field.IsFamilyOrAssembly => "protected internal ",
            FieldInfo field when field.IsFamily => "protected ",
            PropertyInfo property => FormatVisibility(MostVisibleAccessor(property.GetAccessors(nonPublic: true))),
            EventInfo @event => FormatVisibility(MostVisibleAccessor(
                new[] { @event.AddMethod, @event.RemoveMethod, @event.RaiseMethod }
                    .Where(accessor => accessor != null)
                    .Cast<MethodInfo>())),
            _ => string.Empty,
        };
    }

    static string FormatVisibility(MethodBase? method)
    {
        if (method == null || method.IsPublic) return string.Empty;
        if (method.IsFamilyOrAssembly) return "protected internal ";
        return method.IsFamily ? "protected " : string.Empty;
    }

    static MethodInfo? MostVisibleAccessor(IEnumerable<MethodInfo> accessors) => accessors
        .OrderBy(accessor => accessor.IsPublic ? 0 : accessor.IsFamilyOrAssembly ? 1 : accessor.IsFamily ? 2 : 3)
        .FirstOrDefault();

    static string FormatAccessor(MethodInfo? accessor, string name)
    {
        if (accessor == null ||
            !(accessor.IsPublic || accessor.IsFamily || accessor.IsFamilyOrAssembly))
        {
            return string.Empty;
        }

        return $"{FormatVisibility(accessor)}{name}; ";
    }

    static string FormatType(Type type)
    {
        if (type.IsByRef || type.IsPointer || type.IsArray)
        {
            var suffix = type.IsByRef ? "&" : type.IsPointer ? "*" : $"[{new string(',', type.GetArrayRank() - 1)}]";
            return FormatType(type.GetElementType()!) + suffix;
        }
        if (type.IsGenericParameter)
        {
            return type.Name;
        }

        var name = StripArity((type.IsGenericType ? type.GetGenericTypeDefinition() : type).FullName ?? type.Name)
            .Replace('+', '.');
        return type.IsGenericType
            ? $"{name}<{string.Join(", ", type.GetGenericArguments().Select(FormatType))}>"
            : name;
    }

    static string StripArity(string value)
    {
        var result = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '`')
            {
                while (index + 1 < value.Length && char.IsDigit(value[index + 1])) index++;
            }
            else
            {
                result.Append(value[index]);
            }
        }
        return result.ToString();
    }

    static string FormatConstant(object? value, Type type) => value switch
    {
        null or DBNull or Missing => type.IsValueType ? "default" : "null",
        string text => $"\"{text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"",
        char character => $"'{character}'",
        bool boolean => boolean ? "true" : "false",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? value.ToString()!,
    };

    static IEnumerable<Type> SignatureTypes(MemberInfo member) => member switch
    {
        ConstructorInfo constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType),
        MethodInfo method => new[] { method.ReturnType }.Concat(method.GetParameters().Select(parameter => parameter.ParameterType)),
        PropertyInfo property => new[] { property.PropertyType }.Concat(property.GetIndexParameters().Select(parameter => parameter.ParameterType)),
        EventInfo @event => [@event.EventHandlerType!],
        FieldInfo field => [field.FieldType],
        _ => [],
    };

    static bool IsNativeType(Type type)
    {
        if (type.IsPointer || type.IsFunctionPointer || type == typeof(IntPtr) || type == typeof(UIntPtr)) return true;
        if (type.IsByRef || type.IsArray) return IsNativeType(type.GetElementType()!);
        if (type.IsGenericType && type.GetGenericArguments().Any(IsNativeType)) return true;
        var ns = type.Namespace ?? string.Empty;
        return type.Assembly.GetName().Name == "Luau.Interop" || ns == "Luau.Native" ||
            ns.StartsWith("Luau.Internal.Interop", StringComparison.Ordinal) ||
            (typeof(Delegate).IsAssignableFrom(type) && type.IsDefined(typeof(UnmanagedFunctionPointerAttribute)));
    }

    static bool IsExternallyVisible(Type type)
    {
        if (type.IsGenericType && !type.IsGenericTypeDefinition) type = type.GetGenericTypeDefinition();
        return type.IsNested ? type.IsNestedPublic && IsExternallyVisible(type.DeclaringType!) : type.IsPublic;
    }

    static string Diff(string[] expected, string[] actual)
    {
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        var actualSet = actual.ToHashSet(StringComparer.Ordinal);
        return string.Join(Environment.NewLine,
            new[] { "Public API differs from PublicApi.approved.txt." }
                .Concat(expected.Where(line => !actualSet.Contains(line)).Select(line => $"- {line}"))
                .Concat(actual.Where(line => !expectedSet.Contains(line)).Select(line => $"+ {line}")));
    }

    static string BaselinePath([CallerFilePath] string sourcePath = "") =>
        Path.Combine(Path.GetDirectoryName(sourcePath)!, "PublicApi.approved.txt");
}
