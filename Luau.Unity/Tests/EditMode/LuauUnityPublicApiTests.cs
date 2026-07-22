using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;

namespace Luau.Unity.Tests
{
    public sealed class LuauUnityPublicApiTests
    {
        const string ApprovedApiSha256 =
            "faa1316c54571151a0964575c33debce2f82d9893fe9e259e99df9d5c38bcb72";

        [Test]
        public void RuntimePublicAndProtectedApiMatchesApprovedInventory()
        {
            var inventory = BuildInventory(typeof(LuauUnity).Assembly);
            var hash = ComputeSha256(inventory);

            Assert.That(
                hash,
                Is.EqualTo(ApprovedApiSha256),
                "Luau.Unity public API changed. Review and approve this complete inventory:\n" +
                inventory);
        }

        [Test]
        public void ExecuteExtensionSurfaceContainsOnlyApprovedShapes()
        {
            var actual = typeof(LuauStateExtensions)
                .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(method =>
                    method.Name.StartsWith("Execute", StringComparison.Ordinal))
                .Select(FormatMethodShape)
                .OrderBy(signature => signature, StringComparer.Ordinal)
                .ToArray();

            Assert.That(actual, Is.EqualTo(new[]
            {
                "Execute(Luau.LuauState,Luau.Unity.LuauAsset)->Luau.LuauResultScope",
                "ExecuteAsync(Luau.LuauState,Luau.Unity.LuauAsset,System.Threading.CancellationToken)->System.Threading.Tasks.ValueTask<Luau.LuauResultScope>",
                "ExecuteInto(Luau.LuauState,Luau.Unity.LuauAsset,System.Span<Luau.LuauValue>)->System.Int32",
                "ExecuteIntoAsync(Luau.LuauState,Luau.Unity.LuauAsset,System.Memory<Luau.LuauValue>,System.Threading.CancellationToken)->System.Threading.Tasks.ValueTask<System.Int32>",
                "ExecuteIntoWithCompilationServiceAsync(Luau.LuauState,Luau.Unity.LuauAsset,Luau.ILuauCompilationService,System.Memory<Luau.LuauValue>,Luau.LuauCompileOptions,System.Threading.CancellationToken,Luau.LuauExecutionOptions)->System.Threading.Tasks.ValueTask<System.Int32>",
                "ExecuteWithCompilationServiceAsync(Luau.LuauState,Luau.Unity.LuauAsset,Luau.ILuauCompilationService,Luau.LuauCompileOptions,System.Threading.CancellationToken,Luau.LuauExecutionOptions)->System.Threading.Tasks.ValueTask<Luau.LuauResultScope>",
            }));
        }

        static string BuildInventory(Assembly assembly)
        {
            var lines = new List<string>();
            foreach (var type in assembly
                .GetExportedTypes()
                .Where(type => type.Namespace != null &&
                    type.Namespace.StartsWith("Luau.Unity", StringComparison.Ordinal))
                .OrderBy(type => type.FullName, StringComparer.Ordinal))
            {
                lines.Add(FormatType(type));

                var declared = BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.Instance |
                    BindingFlags.Static |
                    BindingFlags.DeclaredOnly;
                foreach (var constructor in type.GetConstructors(declared)
                    .Where(IsPublicOrProtected))
                {
                    lines.Add(
                        "ctor " + Visibility(constructor) + " " + TypeName(type) +
                        "(" + FormatParameters(constructor.GetParameters(), false) + ")");
                }

                foreach (var field in type.GetFields(declared)
                    .Where(IsPublicOrProtected))
                {
                    var modifiers = field.IsStatic ? " static" : string.Empty;
                    modifiers += field.IsLiteral ? " const" : string.Empty;
                    modifiers += field.IsInitOnly ? " readonly" : string.Empty;
                    var value = field.IsLiteral
                        ? " = " + FormatConstant(field.GetRawConstantValue())
                        : string.Empty;
                    lines.Add(
                        "field " + Visibility(field) + modifiers + " " +
                        TypeName(field.FieldType) + " " + TypeName(type) + "." +
                        field.Name + value);
                }

                foreach (var property in type.GetProperties(declared)
                    .Where(property =>
                        IsPublicOrProtected(property.GetMethod) ||
                        IsPublicOrProtected(property.SetMethod)))
                {
                    var accessors = new List<string>();
                    if (IsPublicOrProtected(property.GetMethod))
                    {
                        accessors.Add(Visibility(property.GetMethod) + " get;");
                    }
                    if (IsPublicOrProtected(property.SetMethod))
                    {
                        accessors.Add(Visibility(property.SetMethod) + " set;");
                    }
                    var indexParameters = property.GetIndexParameters();
                    var indexer = indexParameters.Length == 0
                        ? string.Empty
                        : "[" + FormatParameters(indexParameters, false) + "]";
                    lines.Add(
                        "property " + TypeName(property.PropertyType) + " " +
                        TypeName(type) + "." + property.Name + indexer + " { " +
                        string.Join(" ", accessors) + " }");
                }

                foreach (var method in type.GetMethods(declared)
                    .Where(method => !method.IsSpecialName && IsPublicOrProtected(method)))
                {
                    lines.Add(FormatMethod(method));
                }

                foreach (var eventInfo in type.GetEvents(declared)
                    .Where(eventInfo =>
                        IsPublicOrProtected(eventInfo.AddMethod) ||
                        IsPublicOrProtected(eventInfo.RemoveMethod)))
                {
                    lines.Add(
                        "event " + TypeName(eventInfo.EventHandlerType) + " " +
                        TypeName(type) + "." + eventInfo.Name);
                }
            }

            lines.Sort(StringComparer.Ordinal);
            return string.Join("\n", lines);
        }

        static string FormatType(Type type)
        {
            var kind = type.IsInterface
                ? "interface"
                : type.IsEnum
                    ? "enum"
                    : type.IsValueType
                        ? "struct"
                        : type.IsAbstract && type.IsSealed
                            ? "static class"
                            : type.IsAbstract
                                ? "abstract class"
                                : type.IsSealed
                                    ? "sealed class"
                                    : "class";
            var baseType = type.BaseType == null || type.BaseType == typeof(object)
                ? string.Empty
                : " : " + TypeName(type.BaseType);
            return "type public " + kind + " " + TypeName(type) + baseType;
        }

        static string FormatMethod(MethodInfo method)
        {
            var modifiers = method.IsStatic ? " static" : string.Empty;
            if (method.IsAbstract)
            {
                modifiers += " abstract";
            }
            else if (method.GetBaseDefinition() != method)
            {
                modifiers += " override";
            }
            else if (method.IsVirtual && !method.IsFinal)
            {
                modifiers += " virtual";
            }
            if (method.IsDefined(typeof(ExtensionAttribute), false))
            {
                modifiers += " extension";
            }

            var genericArguments = method.IsGenericMethodDefinition
                ? "<" + string.Join(",", method.GetGenericArguments().Select(argument => argument.Name)) + ">"
                : string.Empty;
            var constraints = method.IsGenericMethodDefinition
                ? string.Concat(method.GetGenericArguments().Select(FormatConstraints))
                : string.Empty;
            return "method " + Visibility(method) + modifiers + " " +
                TypeName(method.ReturnType) + " " + TypeName(method.DeclaringType) + "." +
                method.Name + genericArguments + "(" +
                FormatParameters(
                    method.GetParameters(),
                    method.IsDefined(typeof(ExtensionAttribute), false)) + ")" + constraints;
        }

        static string FormatMethodShape(MethodInfo method)
        {
            return method.Name + "(" +
                string.Join(",", method.GetParameters().Select(parameter => TypeName(parameter.ParameterType))) +
                ")->" + TypeName(method.ReturnType);
        }

        static string FormatParameters(ParameterInfo[] parameters, bool extensionMethod)
        {
            return string.Join(", ", parameters.Select((parameter, index) =>
            {
                var modifier = parameter.IsOut
                    ? "out "
                    : parameter.ParameterType.IsByRef
                        ? "ref "
                        : extensionMethod && index == 0
                            ? "this "
                            : string.Empty;
                var optional = parameter.IsOptional ? " [optional]" : string.Empty;
                return modifier + TypeName(parameter.ParameterType) + " " + parameter.Name + optional;
            }));
        }

        static string FormatConstraints(Type argument)
        {
            var constraints = new List<string>();
            var attributes = argument.GenericParameterAttributes &
                GenericParameterAttributes.SpecialConstraintMask;
            if ((attributes & GenericParameterAttributes.ReferenceTypeConstraint) != 0)
            {
                constraints.Add("class");
            }
            if ((attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0)
            {
                constraints.Add("struct");
            }
            constraints.AddRange(argument.GetGenericParameterConstraints().Select(TypeName));
            if ((attributes & GenericParameterAttributes.DefaultConstructorConstraint) != 0 &&
                (attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) == 0)
            {
                constraints.Add("new()");
            }
            return constraints.Count == 0
                ? string.Empty
                : " where " + argument.Name + " : " + string.Join(",", constraints);
        }

        static string TypeName(Type type)
        {
            if (type.IsByRef)
            {
                return TypeName(type.GetElementType());
            }
            if (type.IsArray)
            {
                return TypeName(type.GetElementType()) + "[]";
            }
            if (type.IsGenericParameter)
            {
                return type.Name;
            }
            if (!type.IsGenericType)
            {
                return type.FullName ?? type.Name;
            }

            var definitionName = type.GetGenericTypeDefinition().FullName;
            var tick = definitionName.IndexOf('`');
            if (tick >= 0)
            {
                definitionName = definitionName.Substring(0, tick);
            }
            return definitionName + "<" +
                string.Join(",", type.GetGenericArguments().Select(TypeName)) + ">";
        }

        static bool IsPublicOrProtected(MethodBase method)
        {
            return method != null &&
                (method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly);
        }

        static bool IsPublicOrProtected(FieldInfo field)
        {
            return field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly;
        }

        static string Visibility(MethodBase method)
        {
            if (method.IsPublic) return "public";
            if (method.IsFamilyOrAssembly) return "protected internal";
            return "protected";
        }

        static string Visibility(FieldInfo field)
        {
            if (field.IsPublic) return "public";
            if (field.IsFamilyOrAssembly) return "protected internal";
            return "protected";
        }

        static string FormatConstant(object value)
        {
            if (value == null) return "null";
            if (value is string text) return "\"" + text.Replace("\"", "\\\"") + "\"";
            if (value is char character) return "'" + character + "'";
            if (value is bool boolean) return boolean ? "true" : "false";
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        static string ComputeSha256(string value)
        {
            using var sha256 = SHA256.Create();
            var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(value));
            return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
        }
    }
}
