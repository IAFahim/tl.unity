using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace Tl.Unity
{
    [Serializable]
    public sealed class TlField
    {
        public string Name = string.Empty;
        public string Value = string.Empty;

        public TlField()
        {
        }

        public TlField(string name, string value)
        {
            Name = name;
            Value = value;
        }
    }

    public interface ITlData
    {
        Type DataType { get; }
        List<TlField> Fields { get; set; }
    }

    public static class TlFields
    {
        public static FieldInfo[] Of(Type type)
        {
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            Array.Sort(fields, (a, b) => a.MetadataToken.CompareTo(b.MetadataToken));
            return fields;
        }

        public static bool IsSupported(Type type)
        {
            if (type.IsEnum)
                return true;
            switch (Type.GetTypeCode(type))
            {
                case TypeCode.Boolean:
                case TypeCode.Byte:
                case TypeCode.SByte:
                case TypeCode.Int16:
                case TypeCode.UInt16:
                case TypeCode.Int32:
                case TypeCode.UInt32:
                case TypeCode.Int64:
                case TypeCode.UInt64:
                case TypeCode.Single:
                case TypeCode.Double:
                    return true;
                default:
                    return false;
            }
        }

        public static object Parse(Type type, string text)
        {
            var value = (text ?? string.Empty).Trim();
            if (type.IsEnum)
                return Enum.Parse(type, value.Length == 0 ? "0" : value, false);
            if (value.Length == 0)
                return Activator.CreateInstance(type);
            var culture = CultureInfo.InvariantCulture;
            switch (Type.GetTypeCode(type))
            {
                case TypeCode.Boolean: return bool.Parse(value);
                case TypeCode.Byte: return byte.Parse(value, NumberStyles.Integer, culture);
                case TypeCode.SByte: return sbyte.Parse(value, NumberStyles.Integer, culture);
                case TypeCode.Int16: return short.Parse(value, NumberStyles.Integer, culture);
                case TypeCode.UInt16: return ushort.Parse(value, NumberStyles.Integer, culture);
                case TypeCode.Int32: return int.Parse(value, NumberStyles.Integer, culture);
                case TypeCode.UInt32: return uint.Parse(value, NumberStyles.Integer, culture);
                case TypeCode.Int64: return long.Parse(value, NumberStyles.Integer, culture);
                case TypeCode.UInt64: return ulong.Parse(value, NumberStyles.Integer, culture);
                case TypeCode.Single: return Finite(float.Parse(value, NumberStyles.Float, culture), value);
                case TypeCode.Double: return Finite(double.Parse(value, NumberStyles.Float, culture), value);
                default: throw new NotSupportedException("Field type '" + type.Name + "' is not supported. Use a number, bool or enum field.");
            }
        }

        public static string Format(object value)
        {
            var culture = CultureInfo.InvariantCulture;
            switch (value)
            {
                case bool flag: return flag ? "true" : "false";
                case float single: return single.ToString("R", culture);
                case double number: return number.ToString("R", culture);
                case Enum member: return Enum.IsDefined(member.GetType(), member) ? member.ToString() : Convert.ToInt64(member, culture).ToString(culture);
                case IFormattable formattable: return formattable.ToString(null, culture);
                default: return value == null ? string.Empty : value.ToString();
            }
        }

        public static T Read<T>(List<TlField> fields) where T : unmanaged
        {
            object boxed = default(T);
            foreach (var field in Of(typeof(T)))
            {
                var stored = Find(fields, field.Name);
                if (stored != null)
                    field.SetValue(boxed, Parse(field.FieldType, stored.Value));
            }
            return (T)boxed;
        }

        public static List<TlField> Write<T>(T value) where T : unmanaged
        {
            var list = new List<TlField>();
            object boxed = value;
            foreach (var field in Of(typeof(T)))
                list.Add(new TlField(field.Name, Format(field.GetValue(boxed))));
            return list;
        }

        public static TlField Find(List<TlField> fields, string name)
        {
            if (fields != null)
                foreach (var field in fields)
                    if (field != null && field.Name == name)
                        return field;
            return null;
        }

        static float Finite(float value, string text)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new FormatException("'" + text + "' is not a finite number.");
            return value;
        }

        static double Finite(double value, string text)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new FormatException("'" + text + "' is not a finite number.");
            return value;
        }
    }
}
