using System.Buffers.Binary;
using System.Text;
using NWmi.Dcom;
using NWmi.Ndr;
using NWmi.Ndr.Ole;

namespace NWmi.Wmi;

/// <summary>
/// An IWbemContext property bag (MS-WMI 2.2.13): named values a client attaches to a call for the server (or a
/// provider) to read, e.g. <c>__ProviderArchitecture</c> to reach the 64-bit registry through StdRegProv. It
/// travels custom-marshaled as an <c>IWbemContextBuffer</c> (2.2.13.1) wrapped in an OBJREF_CUSTOM.
/// </summary>
internal sealed class WmiContext
{
    private readonly List<(string Name, OleVarType Type, object? Value)> _properties = [];

    /// <summary>A context with no properties; equivalent to passing a null <c>pCtx</c>.</summary>
    public static WmiContext Empty => new();

    public WmiContext SetInt32(string name, int value)
    {
        Add(name, OleVarType.I4, value);
        return this;
    }

    public WmiContext SetBoolean(string name, bool value)
    {
        Add(name, OleVarType.Bool, value);
        return this;
    }

    public WmiContext SetString(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Add(name, OleVarType.Bstr, value);
        return this;
    }

    private void Add(string name, OleVarType type, object? value)
    {
        ArgumentNullException.ThrowIfNull(name);
        _properties.RemoveAll(p => p.Name == name);
        _properties.Add((name, type, value));
    }

    internal bool IsEmpty => _properties.Count == 0;

    /// <summary>Writes the <c>pCtx</c> parameter: a unique pointer to the context's marshaled interface pointer, or null.</summary>
    internal static void WriteParameter(NdrWriter writer, WmiContext? context) =>
        writer.WriteMInterfacePointer(context is { IsEmpty: false } ? context.ToObjRefBytes() : null);

    private byte[] ToObjRefBytes() =>
        new CustomObjRef(WmiIds.IidWbemContext, WmiIds.ClsidWbemContext, ToBufferBytes()).ToBytes();

    /// <summary>IWbemContextBuffer = NumGuids *GUID NumProps *IWbemContextProperty (MS-WMI 2.2.13.1).</summary>
    internal byte[] ToBufferBytes()
    {
        using var stream = new MemoryStream();
        WriteUInt32(stream, 1); // NumGuids: fixed at 1 (2.2.13.1)
        stream.Write(new byte[16]); // the one required GUID; the spec gives no value for it, so this uses all zero
        WriteUInt32(stream, (uint)_properties.Count);
        foreach (var (name, type, value) in _properties)
        {
            WriteContextString(stream, name);
            WriteUInt32(stream, 0); // PropertyFlags: MUST be 0
            stream.WriteByte((byte)((ushort)type & 0xFF));
            stream.WriteByte((byte)((ushort)type >> 8));
            WritePropertyValue(stream, type, value);
        }

        return stream.ToArray();
    }

    private static void WriteContextString(Stream stream, string value)
    {
        WriteUInt32(stream, (uint)value.Length);
        stream.Write(Encoding.Unicode.GetBytes(value));
    }

    private static void WritePropertyValue(Stream stream, OleVarType type, object? value)
    {
        switch (type)
        {
            case OleVarType.Bstr:
                WriteContextString(stream, (string)value!);
                break;
            case OleVarType.I4:
                var int32Buffer = new byte[8];
                BinaryPrimitives.WriteInt32LittleEndian(int32Buffer, (int)value!);
                stream.Write(int32Buffer);
                break;
            case OleVarType.Bool:
                var boolBuffer = new byte[8];
                BinaryPrimitives.WriteInt16LittleEndian(boolBuffer, (bool)value! ? (short)-1 : (short)0);
                stream.Write(boolBuffer);
                break;
            default:
                throw new NotSupportedException($"IWbemContext properties of type {type} are not supported.");
        }
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }
}
