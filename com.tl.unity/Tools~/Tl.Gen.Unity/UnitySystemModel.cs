using System.Collections.Generic;

namespace Tl.Gen.Unity
{
    public sealed class UnityDiagnostic
    {
        public UnityDiagnostic(string id, string message, bool isError)
        {
            Id = id;
            Message = message;
            IsError = isError;
        }

        public readonly string Id;
        public readonly string Message;
        public readonly bool IsError;
    }

    public sealed class UnityColumn
    {
        public UnityColumn(string name, string elementTypeName, bool readOnly, bool direct = false)
        {
            Name = name;
            ElementTypeName = elementTypeName;
            ReadOnly = readOnly;
            Direct = direct;
        }

        public readonly string Name;
        public readonly string ElementTypeName;
        public readonly bool ReadOnly;
        public readonly bool Direct;
    }

    public sealed class UnityContext
    {
        public UnityContext(string name, string typeName)
        {
            Name = name;
            TypeName = typeName;
        }

        public readonly string Name;
        public readonly string TypeName;
    }

    public sealed class UnitySystemModel
    {
        public UnitySystemModel(
            string ns,
            string systemTypeName,
            IReadOnlyList<UnityColumn> columns,
            IReadOnlyList<UnityContext> contexts,
            IReadOnlyList<UnityContext> events,
            IReadOnlyList<UnityDiagnostic> diagnostics,
            bool hasErrors)
        {
            Namespace = ns;
            SystemTypeName = systemTypeName;
            Columns = columns;
            Contexts = contexts;
            Events = events;
            Diagnostics = diagnostics;
            HasErrors = hasErrors;
        }

        public readonly string Namespace;
        public readonly string SystemTypeName;
        public readonly IReadOnlyList<UnityColumn> Columns;
        public readonly IReadOnlyList<UnityContext> Contexts;
        public readonly IReadOnlyList<UnityContext> Events;
        public readonly IReadOnlyList<UnityDiagnostic> Diagnostics;
        public readonly bool HasErrors;
    }
}
