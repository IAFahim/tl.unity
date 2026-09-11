using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using NUnit.Framework;
using UnityEngine;

namespace Tl.Unity.Tests
{
    public sealed class PublicApiTests
    {
        [Test]
        public void RuntimeAssembliesMatchApprovals()
        {
            Match(typeof(TimelineState).Assembly, "Tl.Unity.approved.txt");
        }

        private static void Match(Assembly assembly, string file)
        {
            var path = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Api", file);
            var actual = Render(assembly);
            Assert.AreEqual(File.ReadAllText(path).Replace("\r\n", "\n"), actual);
        }

        private static string Render(Assembly assembly)
        {
            var text = new StringBuilder();
            foreach (var type in assembly.GetExportedTypes().OrderBy(value => value.FullName, StringComparer.Ordinal))
            {
                text.Append(TypeDeclaration(type)).Append(Constraints(type.GetGenericArguments())).Append('\n');
                AppendLayout(text, type);
                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Where(value => value.Name != "value__")
                    .OrderBy(value => value.MetadataToken))
                    text.Append("  field ").Append(field.IsLiteral ? "const " : field.IsStatic ? "static " : "")
                        .Append(field.IsInitOnly && !field.IsLiteral ? "readonly " : "")
                        .Append(TypeName(field.FieldType)).Append(' ').Append(field.Name)
                        .Append(FieldOffset(type, field))
                        .Append(field.IsLiteral ? " = " + Convert.ToString(field.GetRawConstantValue(), System.Globalization.CultureInfo.InvariantCulture) : "")
                        .Append('\n');
                foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .OrderBy(value => value.MetadataToken))
                    text.Append("  constructor ").Append(TypeName(type)).Append('(').Append(Parameters(constructor)).Append(")\n");
                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .OrderBy(value => value.MetadataToken))
                    text.Append("  property ").Append(Return(property.GetMethod.ReturnParameter, property.PropertyType))
                        .Append(' ').Append(property.Name).Append('\n');
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Where(value => !value.IsSpecialName || value.Name.StartsWith("op_", StringComparison.Ordinal))
                    .OrderBy(value => value.MetadataToken))
                    text.Append("  method ").Append(method.IsStatic ? "static " : "")
                        .Append(Return(method.ReturnParameter, method.ReturnType)).Append(' ').Append(MethodName(method))
                        .Append('(').Append(Parameters(method)).Append(')')
                        .Append(Constraints(method.GetGenericArguments())).Append('\n');
            }
            return text.ToString();
        }

        private static string TypeDeclaration(Type type)
        {
            if (type.IsEnum)
                return "public enum " + TypeName(type);
            if (type.IsInterface)
                return "public interface " + TypeName(type);
            if (!type.IsValueType)
                return "public " + (type.IsAbstract && type.IsSealed ? "static " : type.IsSealed ? "sealed " : type.IsAbstract ? "abstract " : "")
                    + "class " + TypeName(type);
            var prefix = type.IsDefined(typeof(System.Runtime.CompilerServices.IsReadOnlyAttribute), false) ? "readonly " : "";
            if (type.IsByRefLike)
                prefix += "ref ";
            return "public " + prefix + "struct " + TypeName(type);
        }

        private static void AppendLayout(StringBuilder text, Type type)
        {
            if (!type.IsValueType || type.IsEnum)
                return;
            var layout = type.StructLayoutAttribute;
            text.Append("  layout ").Append(layout.Value)
                .Append(" pack ").Append(layout.Pack)
                .Append(" size ").Append(layout.Size);
            var concrete = Concrete(type);
            if (concrete != null && !type.IsByRefLike)
                text.Append(" runtime-size ").Append(Marshal.SizeOf(concrete));
            text.Append('\n');
        }

        private static string Parameters(MethodBase method)
        {
            return string.Join(", ", method.GetParameters().Select(Parameter));
        }

        private static string Parameter(ParameterInfo parameter)
        {
            var prefix = parameter.IsOut ? "out " : parameter.ParameterType.IsByRef && parameter.IsIn ? "in " : parameter.ParameterType.IsByRef ? "ref " : "";
            return prefix + TypeName(parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType() : parameter.ParameterType);
        }

        private static string Return(ParameterInfo parameter, Type type)
        {
            if (!type.IsByRef)
                return TypeName(type);
            var readOnly = parameter.GetRequiredCustomModifiers()
                .Any(value => value.FullName == "System.Runtime.CompilerServices.IsReadOnlyAttribute")
                || parameter.GetCustomAttributesData()
                    .Any(value => value.AttributeType.FullName == "System.Runtime.CompilerServices.IsReadOnlyAttribute")
                || parameter.IsIn;
            return (readOnly ? "ref readonly " : "ref ") + TypeName(type.GetElementType());
        }

        private static string MethodName(MethodInfo method)
        {
            if (!method.IsGenericMethodDefinition)
                return method.Name;
            return method.Name + "<" + string.Join(", ", method.GetGenericArguments().Select(value => value.Name)) + ">";
        }

        private static string Constraints(Type[] parameters)
        {
            var text = new StringBuilder();
            foreach (var parameter in parameters.Where(value => value.IsGenericParameter))
            {
                var values = new System.Collections.Generic.List<string>();
                var attributes = parameter.GenericParameterAttributes & GenericParameterAttributes.SpecialConstraintMask;
                var unmanaged = parameter.GetCustomAttributesData()
                    .Any(value => value.AttributeType.FullName == "System.Runtime.CompilerServices.IsUnmanagedAttribute");
                if (unmanaged)
                    values.Add("unmanaged");
                else if ((attributes & GenericParameterAttributes.ReferenceTypeConstraint) != 0)
                    values.Add("class");
                else if ((attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0)
                    values.Add("struct");
                values.AddRange(parameter.GetGenericParameterConstraints()
                    .Where(value => value != typeof(ValueType))
                    .Select(TypeName));
                if (!unmanaged
                    && (attributes & GenericParameterAttributes.DefaultConstructorConstraint) != 0
                    && (attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) == 0)
                    values.Add("new()");
                if (values.Count != 0)
                    text.Append(" where ").Append(parameter.Name).Append(" : ").Append(string.Join(", ", values));
            }
            return text.ToString();
        }

        private static string FieldOffset(Type type, FieldInfo field)
        {
            if (field.IsStatic)
                return "";
            var concrete = Concrete(type);
            if (concrete == null || type.IsByRefLike)
                return "";
            return " offset " + Marshal.OffsetOf(concrete, field.Name).ToInt64();
        }

        private static Type Concrete(Type type)
        {
            if (!type.ContainsGenericParameters)
                return type;
            return null;
        }

        private static string TypeName(Type type)
        {
            if (type.IsPointer)
                return TypeName(type.GetElementType()) + "*";
            if (type.IsByRef)
                return TypeName(type.GetElementType()) + "&";
            if (type.IsGenericParameter)
                return type.Name;
            if (!type.IsGenericType)
                return type.FullName ?? type.Name;
            var definition = type.GetGenericTypeDefinition().FullName;
            var name = definition.Substring(0, definition.IndexOf('`'));
            return name + "<" + string.Join(", ", type.GetGenericArguments().Select(TypeName)) + ">";
        }
    }
}
