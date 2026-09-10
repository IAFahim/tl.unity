using System;
using System.IO;
using System.Linq;
using System.Reflection;
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
            Match(typeof(Timeline).Assembly, "Tl.Unity.approved.txt");
            Match(typeof(TimelineBlob).Assembly, "Tl.Unity.Entities.approved.txt");
        }

        private static void Match(Assembly assembly, string file)
        {
            var path = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Api", file);
            Assert.AreEqual(File.ReadAllText(path).Replace("\r\n", "\n"), Render(assembly));
        }

        private static string Render(Assembly assembly)
        {
            var text = new StringBuilder();
            foreach (var type in assembly.GetExportedTypes().OrderBy(value => value.FullName, StringComparer.Ordinal))
            {
                text.Append(TypeKind(type)).Append(' ').Append(TypeName(type)).Append('\n');
                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Where(value => value.Name != "value__")
                    .OrderBy(value => value.Name, StringComparer.Ordinal))
                    text.Append("  field ").Append(field.IsStatic ? "static " : "")
                        .Append(field.IsInitOnly ? "readonly " : "")
                        .Append(TypeName(field.FieldType)).Append(' ').Append(field.Name)
                        .Append(field.IsLiteral ? " = " + Convert.ToString(field.GetRawConstantValue(), System.Globalization.CultureInfo.InvariantCulture) : "")
                        .Append('\n');
                foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .OrderBy(value => Signature(value), StringComparer.Ordinal))
                    text.Append("  constructor ").Append(TypeName(type)).Append('(').Append(Parameters(constructor)).Append(")\n");
                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .OrderBy(value => value.Name, StringComparer.Ordinal))
                    text.Append("  property ").Append(TypeName(property.PropertyType)).Append(' ').Append(property.Name).Append('\n');
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Where(value => !value.IsSpecialName)
                    .OrderBy(value => Signature(value), StringComparer.Ordinal))
                    text.Append("  method ").Append(method.IsStatic ? "static " : "")
                        .Append(TypeName(method.ReturnType)).Append(' ').Append(method.Name)
                        .Append('(').Append(Parameters(method)).Append(")\n");
            }
            return text.ToString();
        }

        private static string TypeKind(Type type)
        {
            if (type.IsEnum)
                return "enum";
            if (type.IsInterface)
                return "interface";
            return type.IsValueType ? "struct" : "class";
        }

        private static string Signature(MethodBase method)
        {
            return method.Name + "(" + Parameters(method) + ")";
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

        private static string TypeName(Type type)
        {
            if (type.IsPointer)
                return TypeName(type.GetElementType()) + "*";
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
