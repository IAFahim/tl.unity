using Microsoft.CodeAnalysis;

namespace Tl.Gen.Unity
{
    internal static class UnityLayout
    {
        internal static bool TrySize(ITypeSymbol type, out int size, out int align)
        {
            size = 0;
            align = 1;
            switch (type.SpecialType)
            {
                case SpecialType.System_Boolean:
                case SpecialType.System_Byte:
                case SpecialType.System_SByte:
                    size = 1;
                    align = 1;
                    return true;
                case SpecialType.System_Int16:
                case SpecialType.System_UInt16:
                case SpecialType.System_Char:
                    size = 2;
                    align = 2;
                    return true;
                case SpecialType.System_Int32:
                case SpecialType.System_UInt32:
                case SpecialType.System_Single:
                    size = 4;
                    align = 4;
                    return true;
                case SpecialType.System_Int64:
                case SpecialType.System_UInt64:
                case SpecialType.System_Double:
                    size = 8;
                    align = 8;
                    return true;
            }
            if (type is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType && enumType.EnumUnderlyingType != null)
                return TrySize(enumType.EnumUnderlyingType, out size, out align);
            if (type is not INamedTypeSymbol { TypeKind: TypeKind.Struct } named)
                return false;
            var offset = 0;
            var largest = 1;
            foreach (var member in named.GetMembers())
            {
                if (member is not IFieldSymbol { IsStatic: false, IsConst: false } field)
                    continue;
                if (!TrySize(field.Type, out var fieldSize, out var fieldAlign))
                    return false;
                offset = Round(offset, fieldAlign) + fieldSize;
                if (fieldAlign > largest)
                    largest = fieldAlign;
            }
            align = largest;
            size = Round(offset == 0 ? 1 : offset, largest);
            return true;
        }

        internal static int Round(int value, int alignment)
        {
            return (value + alignment - 1) / alignment * alignment;
        }

        internal static ITypeSymbol? Resolve(Compilation compilation, string displayName)
        {
            var name = displayName.StartsWith("global::") ? displayName.Substring(8) : displayName;
            switch (name)
            {
                case "bool": return compilation.GetSpecialType(SpecialType.System_Boolean);
                case "byte": return compilation.GetSpecialType(SpecialType.System_Byte);
                case "sbyte": return compilation.GetSpecialType(SpecialType.System_SByte);
                case "short": return compilation.GetSpecialType(SpecialType.System_Int16);
                case "ushort": return compilation.GetSpecialType(SpecialType.System_UInt16);
                case "int": return compilation.GetSpecialType(SpecialType.System_Int32);
                case "uint": return compilation.GetSpecialType(SpecialType.System_UInt32);
                case "long": return compilation.GetSpecialType(SpecialType.System_Int64);
                case "ulong": return compilation.GetSpecialType(SpecialType.System_UInt64);
                case "float": return compilation.GetSpecialType(SpecialType.System_Single);
                case "double": return compilation.GetSpecialType(SpecialType.System_Double);
                case "char": return compilation.GetSpecialType(SpecialType.System_Char);
            }
            var candidate = name;
            while (true)
            {
                var found = compilation.GetTypeByMetadataName(candidate);
                if (found != null)
                    return found;
                var dot = candidate.LastIndexOf('.');
                if (dot < 0)
                    return null;
                candidate = candidate.Substring(0, dot) + "+" + candidate.Substring(dot + 1);
            }
        }
    }
}
