using System.Text.Json;
using Siemens.Simatic.Simulation.Runtime;

namespace SiemensEngineeringMcp;

internal static class TagValues
{
    public static object Read(IInstance target, STagInfo info)
    {
        var tag = info.Name;
        if (info.DataType.ToString() == "String")
            return target.ReadString(tag);
        if (info.DataType.ToString() == "WString")
            return target.ReadWString(tag);

        return info.PrimitiveDataType switch
        {
            EPrimitiveDataType.Bool => target.ReadBool(tag),
            EPrimitiveDataType.Int8 => target.ReadInt8(tag),
            EPrimitiveDataType.Int16 => target.ReadInt16(tag),
            EPrimitiveDataType.Int32 => target.ReadInt32(tag),
            EPrimitiveDataType.Int64 => target.ReadInt64(tag),
            EPrimitiveDataType.UInt8 => target.ReadUInt8(tag),
            EPrimitiveDataType.UInt16 => target.ReadUInt16(tag),
            EPrimitiveDataType.UInt32 => target.ReadUInt32(tag),
            EPrimitiveDataType.UInt64 => target.ReadUInt64(tag),
            EPrimitiveDataType.Float => target.ReadFloat(tag),
            EPrimitiveDataType.Double => target.ReadDouble(tag),
            EPrimitiveDataType.Char => target.ReadChar(tag),
            EPrimitiveDataType.WChar => target.ReadWChar(tag),
            _ => throw Unsupported(info)
        };
    }

    public static object Parse(STagInfo info, JsonElement value)
    {
        try
        {
            if (info.DataType.ToString() is "String" or "WString")
                return value.GetString() ?? throw new FormatException();

            return info.PrimitiveDataType switch
            {
                EPrimitiveDataType.Bool => value.GetBoolean(),
                EPrimitiveDataType.Int8 => value.GetSByte(),
                EPrimitiveDataType.Int16 => value.GetInt16(),
                EPrimitiveDataType.Int32 => value.GetInt32(),
                EPrimitiveDataType.Int64 => value.GetInt64(),
                EPrimitiveDataType.UInt8 => value.GetByte(),
                EPrimitiveDataType.UInt16 => value.GetUInt16(),
                EPrimitiveDataType.UInt32 => value.GetUInt32(),
                EPrimitiveDataType.UInt64 => value.GetUInt64(),
                EPrimitiveDataType.Float => ParseSingle(value),
                EPrimitiveDataType.Double => ParseDouble(value),
                EPrimitiveDataType.Char => value.GetSByte(),
                EPrimitiveDataType.WChar => value.GetString() is { Length: 1 } text && !char.IsSurrogate(text[0])
                    ? text[0] : throw new FormatException(),
                _ => throw Unsupported(info)
            };
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            throw new ArgumentException($"Value is invalid for '{info.Name}' ({info.DataType}).", ex);
        }
    }

    public static void Write(IInstance target, STagInfo info, object value)
    {
        var tag = info.Name;
        if (info.DataType.ToString() == "String")
        {
            target.WriteString(tag, (string)value);
            return;
        }
        if (info.DataType.ToString() == "WString")
        {
            target.WriteWString(tag, (string)value);
            return;
        }

        switch (info.PrimitiveDataType)
        {
            case EPrimitiveDataType.Bool: target.WriteBool(tag, (bool)value); break;
            case EPrimitiveDataType.Int8: target.WriteInt8(tag, (sbyte)value); break;
            case EPrimitiveDataType.Int16: target.WriteInt16(tag, (short)value); break;
            case EPrimitiveDataType.Int32: target.WriteInt32(tag, (int)value); break;
            case EPrimitiveDataType.Int64: target.WriteInt64(tag, (long)value); break;
            case EPrimitiveDataType.UInt8: target.WriteUInt8(tag, (byte)value); break;
            case EPrimitiveDataType.UInt16: target.WriteUInt16(tag, (ushort)value); break;
            case EPrimitiveDataType.UInt32: target.WriteUInt32(tag, (uint)value); break;
            case EPrimitiveDataType.UInt64: target.WriteUInt64(tag, (ulong)value); break;
            case EPrimitiveDataType.Float: target.WriteFloat(tag, (float)value); break;
            case EPrimitiveDataType.Double: target.WriteDouble(tag, (double)value); break;
            case EPrimitiveDataType.Char: target.WriteChar(tag, (sbyte)value); break;
            case EPrimitiveDataType.WChar: target.WriteWChar(tag, (char)value); break;
            default: throw Unsupported(info);
        }
    }

    private static float ParseSingle(JsonElement value)
    {
        var number = value.GetSingle();
        return float.IsFinite(number) ? number : throw new FormatException();
    }

    private static double ParseDouble(JsonElement value)
    {
        var number = value.GetDouble();
        return double.IsFinite(number) ? number : throw new FormatException();
    }

    private static NotSupportedException Unsupported(STagInfo info) =>
        new($"Symbolic tag '{info.Name}' has unsupported type {info.DataType}/{info.PrimitiveDataType}.");
}
