using System.Buffers.Binary;
using System.Text;
using NWmi.Ndr;
using NWmi.Rpc;

namespace NWmi.Dcom;

/// <summary>
/// The list of string bindings followed by the list of security bindings that describes how to reach an
/// object resolver or exporter (MS-DCOM 2.2.19). It appears byte-packed inside an OBJREF and as an NDR
/// structure in some method signatures; both forms are handled here.
/// </summary>
internal sealed class DualStringArray(
    IEnumerable<StringBinding> stringBindings,
    IEnumerable<SecurityBinding> securityBindings)
{
    /// <summary>Protocol sequence identifier of ncacn_ip_tcp (C706 table I-2, which is not among the downloaded specs).</summary>
    public const ushort TcpTowerId = 0x07;

    private const ushort SecurityBindingReserved = 0xFFFF;

    public IReadOnlyList<StringBinding> StringBindings { get; } = [.. stringBindings];

    public IReadOnlyList<SecurityBinding> SecurityBindings { get; } = [.. securityBindings];

    /// <summary>Parses the array of unsigned shorts that follows wNumEntries and wSecurityOffset.</summary>
    public static DualStringArray FromEntries(ReadOnlySpan<ushort> entries, int securityOffset)
    {
        if (securityOffset < 0 || securityOffset > entries.Length)
            throw new FormatException("The security offset of a string array lies outside the array.");

        var stringBindings = new List<StringBinding>();
        var index = 0;
        while (index < securityOffset)
        {
            var tower = entries[index++];
            if (tower == 0)
                break; // nullterm1 ends the string bindings

            var (address, next) = ReadString(entries, index, securityOffset);
            stringBindings.Add(new StringBinding(tower, address));
            index = next;
        }

        var securityBindings = new List<SecurityBinding>();
        index = securityOffset;
        while (index < entries.Length)
        {
            var authn = entries[index++];
            if (authn == 0)
                break; // nullterm2

            if (index >= entries.Length)
                throw new FormatException("A security binding is cut short.");

            index++; // reserved
            var (principal, next) = ReadString(entries, index, entries.Length);
            securityBindings.Add(new SecurityBinding((RpcAuthType)authn, principal));
            index = next;
        }

        return new DualStringArray(stringBindings, securityBindings);
    }

    /// <summary>The array of unsigned shorts and the offset of the security bindings, ready to be written.</summary>
    public (ushort[] Entries, ushort SecurityOffset) ToEntries()
    {
        var entries = new List<ushort>();
        foreach (var binding in StringBindings)
        {
            entries.Add(binding.TowerId);
            AppendString(entries, binding.NetworkAddress);
        }

        if (StringBindings.Count == 0)
            entries.Add(0); // the list holds "one or more" bindings; an empty one has a zero tower id

        entries.Add(0); // nullterm1
        var securityOffset = checked((ushort)entries.Count);
        foreach (var binding in SecurityBindings)
        {
            entries.Add((ushort)binding.AuthnService);
            entries.Add(SecurityBindingReserved);
            AppendString(entries, binding.PrincipalName);
        }

        if (SecurityBindings.Count == 0)
            entries.Add(0);

        entries.Add(0); // nullterm2
        return ([.. entries], securityOffset);
    }

    /// <summary>Parses the byte-packed form: wNumEntries, wSecurityOffset, then the entries.</summary>
    /// <param name="data">The bytes starting at wNumEntries; extra bytes after the array are ignored.</param>
    /// <param name="consumed">How many bytes the array occupies.</param>
    public static DualStringArray ParsePacked(ReadOnlySpan<byte> data, out int consumed)
    {
        if (data.Length < 4)
            throw new FormatException("A string array needs at least its 4-byte header.");

        var count = BinaryPrimitives.ReadUInt16LittleEndian(data);
        var securityOffset = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        consumed = 4 + count * 2;
        if (data.Length < consumed)
            throw new FormatException("A string array is cut short.");

        var entries = new ushort[count];
        for (var i = 0; i < count; i++)
            entries[i] = BinaryPrimitives.ReadUInt16LittleEndian(data[(4 + i * 2)..]);

        return FromEntries(entries, securityOffset);
    }

    public byte[] ToPacked()
    {
        var (entries, securityOffset) = ToEntries();
        var bytes = new byte[4 + entries.Length * 2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, checked((ushort)entries.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), securityOffset);
        for (var i = 0; i < entries.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4 + i * 2), entries[i]);

        return bytes;
    }

    /// <summary>
    /// Reads the NDR form (MS-DCOM 2.2.19.2): the conformance of the entries, wNumEntries, wSecurityOffset, then
    /// the entries. The reader is positioned at the conformance, i.e. at the pointee of a pointer to the array.
    /// </summary>
    internal static DualStringArray ReadNdr(NdrReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var maxCount = reader.ReadMaxCount();
        var count = reader.ReadUInt16();
        var securityOffset = reader.ReadUInt16();
        if (maxCount != count)
            throw new NdrFormatException($"A string array announces {count} entries but is sized for {maxCount}.");

        reader.EnsureCount(count, 2);
        var entries = new ushort[count];
        for (var i = 0; i < count; i++)
            entries[i] = reader.ReadUInt16();

        try
        {
            return FromEntries(entries, securityOffset);
        }
        catch (FormatException ex)
        {
            throw new NdrFormatException("The string array is malformed.", ex);
        }
    }

    /// <summary>Writes the NDR form read by <see cref="ReadNdr"/>.</summary>
    internal void WriteNdr(NdrWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var (entries, securityOffset) = ToEntries();
        writer.WriteMaxCount(entries.Length);
        writer.WriteUInt16((ushort)entries.Length);
        writer.WriteUInt16(securityOffset);
        foreach (var entry in entries)
            writer.WriteUInt16(entry);
    }

    /// <summary>Reads a null-terminated UTF-16 string made of consecutive entries, stopping at <paramref name="limit"/>.</summary>
    private static (string Text, int Next) ReadString(ReadOnlySpan<ushort> entries, int start, int limit)
    {
        var end = start;
        while (end < limit && entries[end] != 0)
            end++;

        if (end >= limit)
            throw new FormatException("A string in a string array lacks its terminator.");

        var builder = new StringBuilder(end - start);
        for (var i = start; i < end; i++)
            builder.Append((char)entries[i]);

        return (builder.ToString(), end + 1);
    }

    private static void AppendString(List<ushort> entries, string text)
    {
        foreach (var character in text)
            entries.Add(character);

        entries.Add(0);
    }
}
