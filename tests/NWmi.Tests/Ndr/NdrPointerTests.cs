using NWmi.Ndr;

namespace NWmi.Tests.Ndr;

public class NdrPointerTests
{
    [Fact]
    public void Null_unique_pointer_is_a_zero_referent()
    {
        var w = new NdrWriter();
        w.WriteUniquePointer(false, null);
        Assert.Equal(Hex.Parse("00000000"), w.ToArray());

        var pointer = new NdrReader(w.ToArray()).ReadUniquePointer(r => r.ReadUInt32());
        Assert.True(pointer.IsNull);
        Assert.True(pointer.IsResolved);
    }

    [Fact]
    public void Top_level_unique_pointer_is_followed_by_its_pointee()
    {
        var w = new NdrWriter();
        w.WriteUniquePointer(true, ww => ww.WriteUInt32(0x12345678));
        Assert.Equal(Hex.Parse("00000200 | 78563412"), w.ToArray());

        var pointer = new NdrReader(w.ToArray()).ReadUniquePointer(r => r.ReadUInt32());
        Assert.Equal(0x00020000u, pointer.ReferentId);
        Assert.Equal(0x12345678u, pointer.Value);
    }

    [Fact]
    public void Referent_ids_increase_by_four()
    {
        var w = new NdrWriter();
        w.WriteUniquePointer(true, ww => ww.WriteByte(1));
        w.WriteUniquePointer(true, ww => ww.WriteByte(2));
        Assert.Equal(Hex.Parse("00000200 01 000000 | 04000200 02"), w.ToArray());
    }

    [Fact]
    public void Embedded_pointees_are_deferred_to_the_end_of_the_structure()
    {
        var w = new NdrWriter();
        w.Struct(s =>
        {
            s.WriteUniquePointer(true, p => p.WriteUInt32(0x11223344));
            s.WriteUInt32(7);
            s.WriteUniquePointer(true, p => p.WriteUInt32(0x55));
        });

        Assert.Equal(Hex.Parse("00000200 | 07000000 | 04000200 | 44332211 | 55000000"), w.ToArray());

        var r = new NdrReader(w.ToArray());
        NdrDeferred<uint>? first = null;
        NdrDeferred<uint>? second = null;
        var seven = r.Struct(s =>
        {
            first = s.ReadUniquePointer(p => p.ReadUInt32());
            var n = s.ReadUInt32();
            second = s.ReadUniquePointer(p => p.ReadUInt32());
            Assert.False(first!.IsResolved);
            Assert.Throws<InvalidOperationException>(() => first!.Value);
            return n;
        });

        Assert.Equal(7u, seven);
        Assert.Equal(0x11223344u, first!.Value);
        Assert.Equal(0x55u, second!.Value);
        Assert.True(r.IsAtEnd);
    }

    [Fact]
    public void Pointees_containing_pointers_are_written_depth_first()
    {
        // S { p1 -> A { q -> C }, p2 -> B }
        var w = new NdrWriter();
        w.Struct(s =>
        {
            s.WriteUniquePointer(true, a => a.Struct(aa => aa.WriteUniquePointer(true, c => c.WriteUInt32(0xCC))));
            s.WriteUniquePointer(true, b => b.WriteUInt32(0xBB));
        });

        Assert.Equal(Hex.Parse("00000200 04000200 | 08000200 | CC000000 | BB000000"), w.ToArray());

        var r = new NdrReader(w.ToArray());
        NdrDeferred<NdrDeferred<uint>>? p1 = null;
        NdrDeferred<uint>? p2 = null;
        r.Struct(s =>
        {
            p1 = s.ReadUniquePointer(a => a.Struct(aa => aa.ReadUniquePointer(c => c.ReadUInt32())));
            p2 = s.ReadUniquePointer(b => b.ReadUInt32());
            return 0;
        });

        Assert.Equal(0xCCu, p1!.Value.Value);
        Assert.Equal(0xBBu, p2!.Value);
        Assert.True(r.IsAtEnd);
    }

    [Fact]
    public void Reference_pointers_have_no_referent_at_top_level_but_do_when_embedded()
    {
        var w = new NdrWriter();
        w.WriteReferencePointer(ww => ww.WriteUInt32(9));
        Assert.Equal(Hex.Parse("09000000"), w.ToArray());

        w = new NdrWriter();
        w.Struct(s => s.WriteReferencePointer(ww => ww.WriteUInt32(9)));
        Assert.Equal(Hex.Parse("00000200 | 09000000"), w.ToArray());

        var embedded = new NdrReader(w.ToArray());
        NdrDeferred<uint>? value = null;
        embedded.Struct(s =>
        {
            value = s.ReadReferencePointer(p => p.ReadUInt32());
            return 0;
        });
        Assert.Equal(9u, value!.Value);
    }

    [Fact]
    public void Embedded_reference_pointer_must_not_be_null()
    {
        var r = new NdrReader(Hex.Parse("00000000"));
        Assert.Throws<NdrFormatException>(() => r.Struct(s => s.ReadReferencePointer(p => p.ReadUInt32())));
    }

    [Fact]
    public void Then_runs_when_the_pointee_arrives()
    {
        var w = new NdrWriter();
        w.Struct(s => s.WriteUniquePointer(true, p => p.WriteUInt32(5)));

        var seen = 0u;
        new NdrReader(w.ToArray()).Struct(s =>
        {
            s.ReadUniquePointer(p => p.ReadUInt32()).Then(v => seen = v);
            Assert.Equal(0u, seen);
            return 0;
        });
        Assert.Equal(5u, seen);
    }

    [Fact]
    public void Excessive_nesting_is_rejected()
    {
        // Each level: a referent id and then the pointee, which is again a pointer.
        var bytes = new byte[(NdrReader.MaxNesting + 8) * 4];
        for (var i = 0; i < bytes.Length / 4; i++)
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), 0x00020000u + (uint)i * 4);

        static object Chain(NdrReader r) => r.Struct(s => s.ReadUniquePointer(Chain));
        Assert.Throws<NdrFormatException>(() => Chain(new NdrReader(bytes)));
    }
}
