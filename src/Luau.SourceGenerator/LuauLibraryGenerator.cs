using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Security.Cryptography;
using System.Text;

namespace Luau.SourceGenerator;

internal enum LuauLibraryMemberKind
{
    Field,
    Property,
    Method,
}

internal enum LuauLibraryReturnKind
{
    None,
    Value,
    Async,
    AsyncValue,
}

internal enum LuauLibraryParameterKind
{
    Argument,
    CallContext,
    CancellationToken,
    State,
}

internal enum LuauLibraryExposureKind
{
    Global,
    Capability,
}

internal enum LuauLibraryValueKind
{
    Standard,
    UnityVector3,
}

internal sealed record LuauLibraryParameter
{
    public required string TypeName { get; init; }
    public required LuauLibraryParameterKind Kind { get; init; }
    public required LuauLibraryValueKind ValueKind { get; init; }
}

internal sealed record LuauLibraryMember
{
    public required LuauLibraryMemberKind Kind { get; init; }
    public required string LuauName { get; init; }
    public required string ManagedName { get; init; }
    public required string TypeName { get; init; }
    public required bool IsStatic { get; init; }
    public required bool CanRead { get; init; }
    public required bool CanWrite { get; init; }
    public required LuauLibraryReturnKind ReturnKind { get; init; }
    public required LuauLibraryValueKind ValueKind { get; init; }
    public required EquatableArray<LuauLibraryParameter> Parameters { get; init; }
}

internal sealed record LuauLibraryContext
{
    public required EquatableArray<LuauGeneratorDiagnostic> Diagnostics { get; init; }
    public required string LibraryName { get; init; }
    public required string TypeName { get; init; }
    public required string FullTypeName { get; init; }
    public required string CanonicalMetadataName { get; init; }
    public required string? Namespace { get; init; }
    public required string DeclarationKeyword { get; init; }
    public required LuauLibraryExposureKind Exposure { get; init; }
    public required bool RequiresUnityObjectValidation { get; init; }
    public required EquatableArray<LuauLibraryMember> Members { get; init; }
}

[Generator(LanguageNames.CSharp)]
public sealed class LuauLibraryGenerator : IIncrementalGenerator
{
    const string LibraryAttributeName = "Luau.LuauLibraryAttribute";
    const string MemberAttributeName = "Luau.LuauMemberAttribute";
    const string FromStateAttributeName = "Luau.FromLuauStateAttribute";
    const string CallContextTypeName = "Luau.LuauCallContext";
    const string StateTypeName = "Luau.LuauState";
    const string CancellationTokenTypeName = "System.Threading.CancellationToken";
    const string TaskTypeName = "System.Threading.Tasks.Task";
    const string TaskOfTTypeName = "System.Threading.Tasks.Task`1";
    const string ValueTaskTypeName = "System.Threading.Tasks.ValueTask";
    const string ValueTaskOfTTypeName = "System.Threading.Tasks.ValueTask`1";
    const string UnityObjectTypeName = "UnityEngine.Object";
    const string UnityVector3TypeName = "global::UnityEngine.Vector3";

    static readonly SymbolDisplayFormat FullyQualifiedTypeFormat =
        SymbolDisplayFormat.FullyQualifiedFormat
            .WithMiscellaneousOptions(
                SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
                SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var libraries = context.SyntaxProvider.ForAttributeWithMetadataName(
            LibraryAttributeName,
            static (node, _) => node is TypeDeclarationSyntax,
            static (attributeContext, cancellationToken) =>
                Transform(attributeContext, cancellationToken))
            .WithTrackingName("LuauLibraryModels");

        context.RegisterSourceOutput(libraries, Emit);
    }

    static LuauLibraryContext Transform(
        GeneratorAttributeSyntaxContext context,
        CancellationToken cancellationToken)
    {
        var symbol = (INamedTypeSymbol)context.TargetSymbol;
        var syntax = (TypeDeclarationSyntax)context.TargetNode;
        var diagnostics = new List<LuauGeneratorDiagnostic>();
        var location = syntax.Identifier.GetLocation();
        var libraryAttribute = context.Attributes[0];
        var exposure = GetExposure(libraryAttribute, diagnostics, location);

        if (!syntax.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.PartialKeyword)))
        {
            diagnostics.ReportDiagnostic(DiagnosticDescriptors.MustBePartial, location);
        }

        if (symbol.ContainingType != null)
        {
            diagnostics.ReportDiagnostic(DiagnosticDescriptors.NestedNotAllowed, location);
        }

        if (exposure == LuauLibraryExposureKind.Capability && symbol.TypeKind != TypeKind.Class)
        {
            ReportUnsupported(
                diagnostics,
                location,
                symbol.Name,
                "capability types must be reference classes.");
        }
        else if (exposure == LuauLibraryExposureKind.Capability && symbol.IsStatic)
        {
            ReportUnsupported(
                diagnostics,
                location,
                symbol.Name,
                "static capability types are not supported.");
        }
        else if (symbol.IsAbstract)
        {
            diagnostics.ReportDiagnostic(DiagnosticDescriptors.AbstractNotAllowed, location);
        }

        if (symbol.TypeParameters.Length != 0)
        {
            diagnostics.ReportDiagnostic(
                DiagnosticDescriptors.UnsupportedSignature,
                location,
                symbol.Name,
                "generic host-library types are not supported.");
        }

        var libraryName = libraryAttribute.ConstructorArguments.FirstOrDefault().Value as string;
        if (!IsValidLuauName(libraryName))
        {
            diagnostics.ReportDiagnostic(DiagnosticDescriptors.InvalidName, location, "library");
            libraryName = string.Empty;
        }

        var memberAttribute = context.SemanticModel.Compilation.GetTypeByMetadataName(MemberAttributeName);
        var fromStateAttribute = context.SemanticModel.Compilation.GetTypeByMetadataName(FromStateAttributeName);
        var callContextType = context.SemanticModel.Compilation.GetTypeByMetadataName(CallContextTypeName);
        var stateType = context.SemanticModel.Compilation.GetTypeByMetadataName(StateTypeName);
        var cancellationTokenType = context.SemanticModel.Compilation.GetTypeByMetadataName(CancellationTokenTypeName);
        var taskType = context.SemanticModel.Compilation.GetTypeByMetadataName(TaskTypeName);
        var taskOfTType = context.SemanticModel.Compilation.GetTypeByMetadataName(TaskOfTTypeName);
        var valueTaskType = context.SemanticModel.Compilation.GetTypeByMetadataName(ValueTaskTypeName);
        var valueTaskOfTType = context.SemanticModel.Compilation.GetTypeByMetadataName(ValueTaskOfTTypeName);
        var unityObjectType = context.SemanticModel.Compilation.GetTypeByMetadataName(UnityObjectTypeName);

        var members = new List<LuauLibraryMember>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        if (memberAttribute == null || fromStateAttribute == null || callContextType == null ||
            stateType == null || cancellationTokenType == null || taskType == null ||
            taskOfTType == null || valueTaskType == null || valueTaskOfTType == null)
        {
            diagnostics.ReportDiagnostic(
                DiagnosticDescriptors.UnsupportedSignature,
                location,
                symbol.Name,
                "the referenced Luau callback API is incomplete.");
        }
        else
        {
            foreach (var memberSymbol in symbol.GetMembers())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!memberSymbol.TryGetAttribute(memberAttribute, out var attribute))
                {
                    continue;
                }

                var memberLocation = memberSymbol.Locations.FirstOrDefault() ?? location;
                var explicitName = attribute.ConstructorArguments.FirstOrDefault().Value as string;
                var luauName = explicitName ?? memberSymbol.Name;
                if (!IsValidLuauName(luauName))
                {
                    diagnostics.ReportDiagnostic(
                        DiagnosticDescriptors.InvalidName,
                        memberLocation,
                        "member");
                    continue;
                }

                if (!names.Add(luauName))
                {
                    diagnostics.ReportDiagnostic(
                        DiagnosticDescriptors.DuplicateMemberName,
                        memberLocation,
                        luauName);
                    continue;
                }

                var member = CreateMember(
                    memberSymbol,
                    luauName,
                    exposure,
                    fromStateAttribute,
                    callContextType,
                    stateType,
                    cancellationTokenType,
                    taskType,
                    taskOfTType,
                    valueTaskType,
                    valueTaskOfTType,
                    diagnostics,
                    memberLocation);
                if (member != null)
                {
                    members.Add(member);
                }
            }
        }

        return new LuauLibraryContext
        {
            Diagnostics = diagnostics.ToArray(),
            LibraryName = libraryName!,
            TypeName = EscapeIdentifier(symbol.Name),
            FullTypeName = symbol.ToDisplayString(FullyQualifiedTypeFormat),
            CanonicalMetadataName = GetCanonicalMetadataName(symbol),
            Namespace = symbol.ContainingNamespace.IsGlobalNamespace
                ? null
                : symbol.ContainingNamespace.ToDisplayString(),
            DeclarationKeyword = symbol.IsRecord ? "record class" : "class",
            Exposure = exposure,
            RequiresUnityObjectValidation =
                exposure == LuauLibraryExposureKind.Capability &&
                unityObjectType != null &&
                IsUnityObjectType(symbol, unityObjectType),
            Members = members.ToArray(),
        };
    }

    static LuauLibraryMember? CreateMember(
        ISymbol symbol,
        string luauName,
        LuauLibraryExposureKind exposure,
        INamedTypeSymbol fromStateAttribute,
        INamedTypeSymbol callContextType,
        INamedTypeSymbol stateType,
        INamedTypeSymbol cancellationTokenType,
        INamedTypeSymbol taskType,
        INamedTypeSymbol taskOfTType,
        INamedTypeSymbol valueTaskType,
        INamedTypeSymbol valueTaskOfTType,
        List<LuauGeneratorDiagnostic> diagnostics,
        Location location)
    {
        if (symbol.DeclaredAccessibility != Accessibility.Public)
        {
            diagnostics.ReportDiagnostic(
                DiagnosticDescriptors.InaccessibleMember,
                location,
                symbol.Name);
            return null;
        }

        if (exposure == LuauLibraryExposureKind.Capability && symbol.IsStatic)
        {
            ReportUnsupported(
                diagnostics,
                location,
                symbol.Name,
                "static members are not supported by object capabilities.");
            return null;
        }

        switch (symbol)
        {
            case IFieldSymbol field:
                if (field.IsFixedSizeBuffer || !TryGetValueKind(field.Type, exposure, out var fieldValueKind))
                {
                    ReportUnsupported(
                        diagnostics,
                        location,
                        field.Name,
                        "the field type is not supported by Luau value conversion.");
                    return null;
                }

                return new LuauLibraryMember
                {
                    Kind = LuauLibraryMemberKind.Field,
                    LuauName = luauName,
                    ManagedName = EscapeIdentifier(field.Name),
                    TypeName = field.Type.ToDisplayString(FullyQualifiedTypeFormat),
                    IsStatic = field.IsStatic,
                    CanRead = true,
                    CanWrite = !field.IsReadOnly && !field.IsConst,
                    ReturnKind = LuauLibraryReturnKind.None,
                    ValueKind = fieldValueKind,
                    Parameters = [],
                };

            case IPropertySymbol property:
                if (property.IsIndexer || !TryGetValueKind(property.Type, exposure, out var propertyValueKind))
                {
                    ReportUnsupported(
                        diagnostics,
                        location,
                        property.Name,
                        property.IsIndexer
                            ? "indexers are not supported."
                            : "the property type is not supported by Luau value conversion.");
                    return null;
                }

                var canRead = property.GetMethod?.DeclaredAccessibility == Accessibility.Public;
                var canWrite = property.SetMethod is
                {
                    DeclaredAccessibility: Accessibility.Public,
                    IsInitOnly: false,
                };
                if (exposure == LuauLibraryExposureKind.Capability && !canRead && !canWrite)
                {
                    ReportUnsupported(
                        diagnostics,
                        location,
                        property.Name,
                        "capability properties must have at least one public non-init accessor.");
                    return null;
                }

                return new LuauLibraryMember
                {
                    Kind = LuauLibraryMemberKind.Property,
                    LuauName = luauName,
                    ManagedName = EscapeIdentifier(property.Name),
                    TypeName = property.Type.ToDisplayString(FullyQualifiedTypeFormat),
                    IsStatic = property.IsStatic,
                    CanRead = canRead,
                    CanWrite = canWrite,
                    ReturnKind = LuauLibraryReturnKind.None,
                    ValueKind = propertyValueKind,
                    Parameters = [],
                };

            case IMethodSymbol method:
                return CreateMethod(
                    method,
                    luauName,
                    exposure,
                    fromStateAttribute,
                    callContextType,
                    stateType,
                    cancellationTokenType,
                    taskType,
                    taskOfTType,
                    valueTaskType,
                    valueTaskOfTType,
                    diagnostics,
                    location);

            default:
                ReportUnsupported(diagnostics, location, symbol.Name, "only fields, properties, and methods are supported.");
                return null;
        }
    }

    static LuauLibraryMember? CreateMethod(
        IMethodSymbol method,
        string luauName,
        LuauLibraryExposureKind exposure,
        INamedTypeSymbol fromStateAttribute,
        INamedTypeSymbol callContextType,
        INamedTypeSymbol stateType,
        INamedTypeSymbol cancellationTokenType,
        INamedTypeSymbol taskType,
        INamedTypeSymbol taskOfTType,
        INamedTypeSymbol valueTaskType,
        INamedTypeSymbol valueTaskOfTType,
        List<LuauGeneratorDiagnostic> diagnostics,
        Location location)
    {
        if (method.MethodKind != MethodKind.Ordinary || method.IsGenericMethod ||
            method.ReturnsByRef || method.ReturnsByRefReadonly)
        {
            ReportUnsupported(
                diagnostics,
                location,
                method.Name,
                "generic, operator, and by-reference-returning methods are not supported.");
            return null;
        }

        var returnKind = LuauLibraryReturnKind.Value;
        var returnType = method.ReturnType;
        if (method.ReturnsVoid)
        {
            if (method.IsAsync)
            {
                ReportUnsupported(diagnostics, location, method.Name, "async void methods are not supported.");
                return null;
            }

            returnKind = LuauLibraryReturnKind.None;
        }
        else if (SymbolEqualityComparer.Default.Equals(returnType, taskType) ||
                 SymbolEqualityComparer.Default.Equals(returnType, valueTaskType))
        {
            returnKind = LuauLibraryReturnKind.Async;
        }
        else if (returnType is INamedTypeSymbol namedReturn &&
                 namedReturn.TypeArguments.Length == 1 &&
                 (SymbolEqualityComparer.Default.Equals(namedReturn.OriginalDefinition, taskOfTType) ||
                  SymbolEqualityComparer.Default.Equals(namedReturn.OriginalDefinition, valueTaskOfTType)))
        {
            returnKind = LuauLibraryReturnKind.AsyncValue;
            returnType = namedReturn.TypeArguments[0];
        }
        else if (method.IsAsync)
        {
            ReportUnsupported(
                diagnostics,
                location,
                method.Name,
                "async methods must return Task, Task<T>, ValueTask, or ValueTask<T>.");
            return null;
        }

        var returnValueKind = LuauLibraryValueKind.Standard;
        if (returnKind is LuauLibraryReturnKind.Value or LuauLibraryReturnKind.AsyncValue &&
            !TryGetValueKind(returnType, exposure, out returnValueKind))
        {
            ReportUnsupported(
                diagnostics,
                location,
                method.Name,
                "the return type is not supported by Luau value conversion.");
            return null;
        }

        var parameters = new List<LuauLibraryParameter>(method.Parameters.Length);
        foreach (var parameter in method.Parameters)
        {
            if (parameter.RefKind != RefKind.None || IsUnsafeSignatureType(parameter.Type))
            {
                ReportUnsupported(
                    diagnostics,
                    parameter.Locations.FirstOrDefault() ?? location,
                    method.Name,
                    "ref, out, in, pointer, and ref-like parameters are not supported.");
                return null;
            }

            var fromState = parameter.TryGetAttribute(fromStateAttribute, out _);
            LuauLibraryParameterKind kind;
            if (fromState)
            {
                if (!SymbolEqualityComparer.Default.Equals(parameter.Type, stateType))
                {
                    ReportUnsupported(
                        diagnostics,
                        parameter.Locations.FirstOrDefault() ?? location,
                        method.Name,
                        "[FromLuauState] is valid only on a LuauState parameter.");
                    return null;
                }

                kind = LuauLibraryParameterKind.State;
            }
            else if (SymbolEqualityComparer.Default.Equals(parameter.Type, callContextType))
            {
                kind = LuauLibraryParameterKind.CallContext;
            }
            else if (SymbolEqualityComparer.Default.Equals(parameter.Type, cancellationTokenType))
            {
                kind = LuauLibraryParameterKind.CancellationToken;
            }
            else
            {
                kind = LuauLibraryParameterKind.Argument;
            }

            var parameterValueKind = LuauLibraryValueKind.Standard;
            if (kind == LuauLibraryParameterKind.Argument &&
                !TryGetValueKind(parameter.Type, exposure, out parameterValueKind))
            {
                ReportUnsupported(
                    diagnostics,
                    parameter.Locations.FirstOrDefault() ?? location,
                    method.Name,
                    "the script argument type is not supported by Luau value conversion.");
                return null;
            }

            if (kind == LuauLibraryParameterKind.Argument && (parameter.IsParams || parameter.IsOptional))
            {
                ReportUnsupported(
                    diagnostics,
                    parameter.Locations.FirstOrDefault() ?? location,
                    method.Name,
                    "params and optional script arguments are not supported.");
                return null;
            }

            parameters.Add(new LuauLibraryParameter
            {
                TypeName = parameter.Type.ToDisplayString(FullyQualifiedTypeFormat),
                Kind = kind,
                ValueKind = parameterValueKind,
            });
        }

        return new LuauLibraryMember
        {
            Kind = LuauLibraryMemberKind.Method,
            LuauName = luauName,
            ManagedName = EscapeIdentifier(method.Name),
            TypeName = returnType.ToDisplayString(FullyQualifiedTypeFormat),
            IsStatic = method.IsStatic,
            CanRead = true,
            CanWrite = false,
            ReturnKind = returnKind,
            ValueKind = returnValueKind,
            Parameters = parameters.ToArray(),
        };
    }

    static void Emit(SourceProductionContext context, LuauLibraryContext library)
    {
        if (library.Diagnostics.Length != 0)
        {
            foreach (var diagnostic in library.Diagnostics)
            {
                context.ReportDiagnostic(diagnostic.ToDiagnostic());
            }

            return;
        }

        if (library.Exposure == LuauLibraryExposureKind.Capability)
        {
            EmitCapability(context, library);
            return;
        }

        var builder = new CodeBuilder(0);
        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine();

        var namespaceBlock = library.Namespace == null
            ? builder.Nop
            : builder.BeginBlock($"namespace {library.Namespace}");

        using (builder.BeginBlock(
                   $"partial {library.DeclarationKeyword} {library.TypeName} : global::Luau.ILuauLibrary"))
        {
            using (builder.BeginBlock(
                       "void global::Luau.ILuauLibrary.RegisterTo(global::Luau.LuauState state)"))
            {
                builder.AppendLine("using var table = state.CreateTable();");
                if (library.Members.Length != 0)
                {
                    builder.AppendLine("using var metatable = state.CreateTable();");
                    builder.AppendLine();
                }

                var methods = library.Members
                    .Where(static member => member.Kind == LuauLibraryMemberKind.Method)
                    .ToArray();
                for (var index = 0; index < methods.Length; index++)
                {
                    EmitMethod(builder, library, methods[index], index);
                    builder.AppendLine();
                }

                if (library.Members.Length != 0)
                {
                    EmitIndexCallback(builder, library);
                    builder.AppendLine();
                    EmitNewIndexCallback(builder, library);
                    builder.AppendLine();
                    builder.AppendLine(
                        "metatable[\"__metatable\"] = \"protected Luau host library\";");
                    builder.AppendLine("state.SetMetatable(table, metatable);");
                }

                builder.AppendLine($"state[{Literal(library.LibraryName)}] = table;");
            }
        }

        namespaceBlock.Dispose();

        var hintPrefix = new string(
            library.CanonicalMetadataName
                .Select(static character => char.IsLetterOrDigit(character) ? character : '_')
                .ToArray());
        var hintHash = ComputeHintHash(library.CanonicalMetadataName);
        context.AddSource($"LuauLibrary.{hintPrefix}.{hintHash}.g.cs", builder.ToString());
    }

    static void EmitCapability(SourceProductionContext context, LuauLibraryContext library)
    {
        var builder = new CodeBuilder(0);
        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine();

        var namespaceBlock = library.Namespace == null
            ? builder.Nop
            : builder.BeginBlock($"namespace {library.Namespace}");

        using (builder.BeginBlock(
                   $"partial {library.DeclarationKeyword} {library.TypeName} : global::Luau.ILuauObjectCapability"))
        {
            builder.AppendLine(
                $"static readonly global::Luau.LuauObjectDescriptor<{library.FullTypeName}> s_luauObjectDescriptor = new global::Luau.LuauObjectDescriptor<{library.FullTypeName}>(");
            using (builder.BeginIndent())
            {
                builder.AppendLine($"{Literal(library.LibraryName)},");
                builder.AppendLine(
                    library.RequiresUnityObjectValidation
                        ? "global::Luau.Unity.LuauUnityObjectGuard.ThrowIfDestroyed,"
                        : "null,");
                builder.AppendLine($"new global::Luau.LuauObjectMember<{library.FullTypeName}>[]");
                builder.AppendLine("{");
                using (builder.BeginIndent())
                {
                    foreach (var member in library.Members)
                    {
                        EmitCapabilityMember(builder, library, member);
                    }
                }
                builder.AppendLine("});");
            }

            builder.AppendLine();
            builder.AppendLine(
                "global::Luau.LuauObjectDescriptor global::Luau.ILuauObjectCapability.LuauObjectDescriptor => s_luauObjectDescriptor;");
        }

        namespaceBlock.Dispose();

        var hintPrefix = new string(
            library.CanonicalMetadataName
                .Select(static character => char.IsLetterOrDigit(character) ? character : '_')
                .ToArray());
        var hintHash = ComputeHintHash(library.CanonicalMetadataName);
        context.AddSource($"LuauLibrary.{hintPrefix}.{hintHash}.g.cs", builder.ToString());
    }

    static void EmitCapabilityMember(
        CodeBuilder builder,
        LuauLibraryContext library,
        LuauLibraryMember member)
    {
        var memberType = $"global::Luau.LuauObjectMember<{library.FullTypeName}>";
        if (member.Kind is LuauLibraryMemberKind.Field or LuauLibraryMemberKind.Property)
        {
            builder.AppendLine($"{memberType}.Property(");
            using (builder.BeginIndent())
            {
                builder.AppendLine($"{Literal(member.LuauName)},");
                if (member.CanRead)
                {
                    builder.AppendLine("static (target, context) =>");
                    builder.AppendLine("{");
                    using (builder.BeginIndent())
                    {
                        EmitReturn(
                            builder,
                            member.ValueKind,
                            $"target.{member.ManagedName}");
                    }
                    builder.AppendLine("},");
                }
                else
                {
                    builder.AppendLine("null,");
                }

                if (member.CanWrite)
                {
                    builder.AppendLine("static (target, context) =>");
                    builder.AppendLine("{");
                    using (builder.BeginIndent())
                    {
                        builder.AppendLine(
                            $"target.{member.ManagedName} = {ReadExpression(member.TypeName, member.ValueKind, 2)};");
                    }
                    builder.AppendLine("}),");
                }
                else
                {
                    builder.AppendLine("null),");
                }
            }

            return;
        }

        var isAsync = member.ReturnKind is LuauLibraryReturnKind.Async or LuauLibraryReturnKind.AsyncValue;
        builder.AppendLine($"{memberType}.{(isAsync ? "AsyncMethod" : "Method")}(");
        using (builder.BeginIndent())
        {
            builder.AppendLine($"{Literal(member.LuauName)},");
            builder.AppendLine(isAsync
                ? "static async (target, context) =>"
                : "static (target, context) =>");
            builder.AppendLine("{");
            using (builder.BeginIndent())
            {
                EmitMethodBody(builder, library, member, "target", firstArgumentIndex: 1);
            }
            builder.AppendLine("}),");
        }
    }

    static void EmitMethodBody(
        CodeBuilder builder,
        LuauLibraryContext library,
        LuauLibraryMember method,
        string target,
        int firstArgumentIndex)
    {
        var argumentCount = method.Parameters.Count(
            static parameter => parameter.Kind == LuauLibraryParameterKind.Argument);
        if (argumentCount != 0)
        {
            var callbackName = $"{library.LibraryName}.{method.LuauName}";
            var message = $"Host function '{callbackName}' expects at least {argumentCount} argument(s).";
            using (builder.BeginBlock($"if (context.ArgumentCount < {argumentCount + firstArgumentIndex})"))
            {
                builder.AppendLine($"throw new global::Luau.LuauException({Literal(message)});");
            }
        }

        var argumentIndex = firstArgumentIndex;
        for (var parameterIndex = 0; parameterIndex < method.Parameters.Length; parameterIndex++)
        {
            var parameter = method.Parameters[parameterIndex];
            var expression = parameter.Kind switch
            {
                LuauLibraryParameterKind.Argument =>
                    ReadExpression(parameter.TypeName, parameter.ValueKind, argumentIndex++),
                LuauLibraryParameterKind.CallContext => "context",
                LuauLibraryParameterKind.CancellationToken => "context.CancellationToken",
                LuauLibraryParameterKind.State => "context.State",
                _ => throw new ArgumentOutOfRangeException(),
            };
            builder.AppendLine($"var arg{parameterIndex} = {expression};");
        }

        var arguments = string.Join(
            ", ",
            Enumerable.Range(0, method.Parameters.Length).Select(static index => $"arg{index}"));
        var invocation = $"{target}.{method.ManagedName}({arguments})";
        switch (method.ReturnKind)
        {
            case LuauLibraryReturnKind.None:
                builder.AppendLine($"{invocation};");
                break;
            case LuauLibraryReturnKind.Value:
                builder.AppendLine($"var result = {invocation};");
                EmitReturn(builder, method.ValueKind, "result");
                break;
            case LuauLibraryReturnKind.Async:
                builder.AppendLine($"await {invocation};");
                break;
            case LuauLibraryReturnKind.AsyncValue:
                builder.AppendLine($"var result = await {invocation};");
                EmitReturn(builder, method.ValueKind, "result");
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    static string ReadExpression(
        string typeName,
        LuauLibraryValueKind valueKind,
        int index) => valueKind == LuauLibraryValueKind.UnityVector3
        ? $"global::Luau.Unity.LuauUnityValue.ReadVector3(context, {index})"
        : $"context.Read<{typeName}>({index})";

    static void EmitReturn(
        CodeBuilder builder,
        LuauLibraryValueKind valueKind,
        string expression)
    {
        if (valueKind == LuauLibraryValueKind.UnityVector3)
        {
            builder.AppendLine(
                $"global::Luau.Unity.LuauUnityValue.ReturnVector3(context, {expression});");
        }
        else
        {
            builder.AppendLine($"context.Return({expression});");
        }
    }

    static void EmitMethod(
        CodeBuilder builder,
        LuauLibraryContext library,
        LuauLibraryMember method,
        int methodIndex)
    {
        var callbackName = $"{library.LibraryName}.{method.LuauName}";
        var factory = method.ReturnKind is LuauLibraryReturnKind.Async or LuauLibraryReturnKind.AsyncValue
            ? "CreateAsyncFunction"
            : "CreateFunction";
        var lambda = method.ReturnKind is LuauLibraryReturnKind.Async or LuauLibraryReturnKind.AsyncValue
            ? "async context =>"
            : "context =>";

        builder.AppendLine(
            $"var function{methodIndex} = state.{factory}({Literal(callbackName)}, {lambda}");
        builder.AppendLine("{");
        using (builder.BeginIndent())
        {
            var target = method.IsStatic ? library.FullTypeName : "this";
            EmitMethodBody(builder, library, method, target, firstArgumentIndex: 0);
        }
        builder.AppendLine("});");
    }

    static void EmitIndexCallback(
        CodeBuilder builder,
        LuauLibraryContext library)
    {
        builder.AppendLine(
            $"metatable[\"__index\"] = state.CreateFunction({Literal($"{library.LibraryName}.__index")}, context =>");
        builder.AppendLine("{");
        using (builder.BeginIndent())
        {
            builder.AppendLine("var key = context.Read<global::System.String>(1);");
            using (builder.BeginBlock("switch (key)"))
            {
                var methodIndex = 0;
                foreach (var member in library.Members)
                {
                    using (builder.BeginIndent($"case {Literal(member.LuauName)}:"))
                    {
                        if (member.Kind == LuauLibraryMemberKind.Method)
                        {
                            builder.AppendLine($"context.Return(function{methodIndex++});");
                            builder.AppendLine("break;");
                        }
                        else if (member.CanRead)
                        {
                            builder.AppendLine(
                                $"context.Return({MemberTarget(library, member)}.{member.ManagedName});");
                            builder.AppendLine("break;");
                        }
                        else
                        {
                            builder.AppendLine(
                                "throw new global::Luau.LuauException($\"cannot read write-only member '{key}'\");");
                        }
                    }
                }
            }
        }
        builder.AppendLine("});");
    }

    static void EmitNewIndexCallback(CodeBuilder builder, LuauLibraryContext library)
    {
        builder.AppendLine(
            $"metatable[\"__newindex\"] = state.CreateFunction({Literal($"{library.LibraryName}.__newindex")}, context =>");
        builder.AppendLine("{");
        using (builder.BeginIndent())
        {
            builder.AppendLine("var key = context.Read<global::System.String>(1);");
            using (builder.BeginBlock("switch (key)"))
            {
                foreach (var member in library.Members)
                {
                    using (builder.BeginIndent($"case {Literal(member.LuauName)}:"))
                    {
                        if (member.Kind != LuauLibraryMemberKind.Method && member.CanWrite)
                        {
                            builder.AppendLine(
                                $"{MemberTarget(library, member)}.{member.ManagedName} = context.Read<{member.TypeName}>(2);");
                            builder.AppendLine("break;");
                        }
                        else
                        {
                            builder.AppendLine(
                                "throw new global::Luau.LuauException($\"cannot set readonly member '{key}'\");");
                        }
                    }
                }

                using (builder.BeginIndent("default:"))
                {
                    builder.AppendLine(
                        "throw new global::Luau.LuauException($\"cannot set unknown member '{key}'\");");
                }
            }
        }
        builder.AppendLine("});");
    }

    static string MemberTarget(LuauLibraryContext library, LuauLibraryMember member) =>
        member.IsStatic ? library.FullTypeName : "this";

    static bool IsUnsafeSignatureType(ITypeSymbol type) =>
        type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer or TypeKind.Error or TypeKind.TypeParameter ||
        type.IsRefLikeType;

    static LuauLibraryExposureKind GetExposure(
        AttributeData libraryAttribute,
        List<LuauGeneratorDiagnostic> diagnostics,
        Location location)
    {
        foreach (var namedArgument in libraryAttribute.NamedArguments)
        {
            if (!string.Equals(namedArgument.Key, "Exposure", StringComparison.Ordinal))
            {
                continue;
            }

            if (namedArgument.Value.Value is int value)
            {
                if (value == (int)LuauLibraryExposureKind.Global)
                {
                    return LuauLibraryExposureKind.Global;
                }

                if (value == (int)LuauLibraryExposureKind.Capability)
                {
                    return LuauLibraryExposureKind.Capability;
                }
            }

            diagnostics.ReportDiagnostic(
                DiagnosticDescriptors.InvalidExposure,
                location,
                namedArgument.Value.Value?.ToString() ?? "null");
            return LuauLibraryExposureKind.Global;
        }

        return LuauLibraryExposureKind.Global;
    }

    static bool IsUnityObjectType(INamedTypeSymbol symbol, INamedTypeSymbol unityObjectType)
    {
        for (INamedTypeSymbol? current = symbol; current != null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, unityObjectType))
            {
                return true;
            }
        }

        return false;
    }

    static bool TryGetValueKind(
        ITypeSymbol type,
        LuauLibraryExposureKind exposure,
        out LuauLibraryValueKind valueKind)
    {
        if (IsSupportedValueType(type))
        {
            valueKind = LuauLibraryValueKind.Standard;
            return true;
        }

        if (exposure == LuauLibraryExposureKind.Capability &&
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == UnityVector3TypeName)
        {
            valueKind = LuauLibraryValueKind.UnityVector3;
            return true;
        }

        valueKind = LuauLibraryValueKind.Standard;
        return false;
    }

    static bool IsSupportedValueType(ITypeSymbol type)
    {
        // Generated host APIs use one value-type surface for arguments and
        // results, so this is the explicit overlap of LuauValue.TryRead<T> and
        // LuauState.CreateFrom<T>. Keep it synchronized with those branches.
        if (type.SpecialType is
            SpecialType.System_Boolean or
            SpecialType.System_Byte or
            SpecialType.System_SByte or
            SpecialType.System_Int16 or
            SpecialType.System_UInt16 or
            SpecialType.System_Int32 or
            SpecialType.System_UInt32 or
            SpecialType.System_Int64 or
            SpecialType.System_UInt64 or
            SpecialType.System_Single or
            SpecialType.System_Double or
            SpecialType.System_String)
        {
            return true;
        }

        return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) is
            "global::System.Numerics.Vector3" or
            "global::Luau.LuauValue" or
            "global::Luau.LuauFunction" or
            "global::Luau.LuauTable" or
            "global::Luau.LuauBuffer" or
            "global::Luau.LuauState" or
            "global::Luau.LuauObjectHandle" or
            "global::Luau.LuauUserData";
    }

    static bool IsValidLuauName(string? name) => name != null && name.IndexOf('\0') < 0;

    static void ReportUnsupported(
        List<LuauGeneratorDiagnostic> diagnostics,
        Location location,
        string memberName,
        string reason) =>
        diagnostics.ReportDiagnostic(
            DiagnosticDescriptors.UnsupportedSignature,
            location,
            memberName,
            reason);

    static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);

    static string EscapeIdentifier(string identifier) =>
        SyntaxFacts.GetKeywordKind(identifier) != SyntaxKind.None ||
        SyntaxFacts.GetContextualKeywordKind(identifier) != SyntaxKind.None
            ? "@" + identifier
            : identifier;

    static string GetCanonicalMetadataName(INamedTypeSymbol symbol)
    {
        var namespaceParts = new Stack<string>();
        for (var current = symbol.ContainingNamespace;
             current != null && !current.IsGlobalNamespace;
             current = current.ContainingNamespace)
        {
            namespaceParts.Push(current.MetadataName);
        }

        var typeParts = new Stack<string>();
        for (INamedTypeSymbol? current = symbol; current != null; current = current.ContainingType)
        {
            typeParts.Push(current.MetadataName);
        }

        var typeName = string.Join("+", typeParts);
        return namespaceParts.Count == 0
            ? typeName
            : string.Join(".", namespaceParts) + "." + typeName;
    }

    static string ComputeHintHash(string canonicalMetadataName)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(canonicalMetadataName));
        const string hex = "0123456789abcdef";
        var result = new char[16];
        for (var index = 0; index < result.Length / 2; index++)
        {
            result[index * 2] = hex[hash[index] >> 4];
            result[(index * 2) + 1] = hex[hash[index] & 0x0f];
        }

        return new string(result);
    }
}
