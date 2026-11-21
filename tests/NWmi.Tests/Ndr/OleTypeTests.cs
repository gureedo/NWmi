using NWmi.Ndr;
using NWmi.Ndr.Ole;

namespace NWmi.Tests.Ndr;

public class OleTypeTests
{
    private static byte[] Write(Action<NdrWriter> action)
    {
        var w = new NdrWriter();
        action(w);
        return w.ToArray();
    }

    [Fact]
    public void Bstr_is_a_pointer_to_a_flagged_word_blob()
    {
        var bytes = Write(w => w.WriteBstr("abc"));
        Assert.Equal(Hex.Parse("00000200 | 03000000 | 06000000 | 03000000 | 610062006300"), bytes);
        Assert.Equal("abc", new NdrReader(bytes).ReadBstr().Value);
    }

    [Fact]
    public void Null_and_empty_bstr_are_distinct_on_the_wire()
    {
        var nullBstr = Write(w => w.WriteBstr(null));
        Assert.Equal(Hex.Parse("00000200 | 00000000 | FFFFFFFF | 00000000"), nullBstr);
        Assert.Null(new NdrReader(nullBstr).ReadBstr().Value);

        var empty = Write(w => w.WriteBstr(""));
        Assert.Equal(Hex.Parse("00000200 | 00000000 | 00000000 | 00000000"), empty);
        Assert.Equal("", new NdrReader(empty).ReadBstr().Value);
    }

    [Fact]
    public void Null_bstr_pointer_reads_as_null()
    {
        Assert.Null(new NdrReader(Hex.Parse("00000000")).ReadBstr().Value);
    }

    [Fact]
    public void Bstr_with_inconsistent_sizes_is_rejected()
    {
        // cBytes 5 is odd: a binary BSTR.
        var odd = Hex.Parse("00000200 | 03000000 | 05000000 | 03000000 | 610062006300");
        Assert.Throws<NdrFormatException>(() => new NdrReader(odd).ReadBstr());

        // max_count differs from clSize.
        var mismatch = Hex.Parse("00000200 | 04000000 | 06000000 | 03000000 | 610062006300");
        Assert.Throws<NdrFormatException>(() => new NdrReader(mismatch).ReadBstr());
    }

    [Fact]
    public void MInterfacePointer_repeats_the_size_for_the_hoisted_conformance()
    {
        var bytes = Write(w => w.WriteMInterfacePointer([1, 2, 3]));
        Assert.Equal(Hex.Parse("00000200 | 03000000 | 03000000 | 010203"), bytes);
        Assert.Equal(new byte[] { 1, 2, 3 }, new NdrReader(bytes).ReadMInterfacePointer().Value);

        Assert.Null(new NdrReader(Write(w => w.WriteMInterfacePointer(null))).ReadMInterfacePointer().Value);
    }

    [Fact]
    public void Int32_variant_layout()
    {
        var bytes = Write(w => w.WriteVariant(OleVariant.FromInt32(5)));
        Assert.Equal(
            Hex.Parse("00000200 | 00000000 | 03000000 | 00000000 | 0300 000000000000 | 03000000 | 05000000"),
            bytes);

        var read = new NdrReader(bytes).ReadVariant().Value!;
        Assert.Equal(OleVarType.I4, read.Type);
        Assert.Equal(5, read.Value);
    }

    [Fact]
    public void Empty_variant_layout()
    {
        var bytes = Write(w => w.WriteVariant(OleVariant.Empty));
        Assert.Equal(
            Hex.Parse("00000200 | 00000000 | 03000000 | 00000000 | 0000 000000000000 | 00000000"),
            bytes);
        Assert.Equal(OleVarType.Empty, new NdrReader(bytes).ReadVariant().Value!.Type);
    }

    [Fact]
    public void Bstr_variant_defers_the_string_after_the_structure()
    {
        var bytes = Write(w => w.WriteVariant(OleVariant.FromString("a")));
        Assert.Equal(
            Hex.Parse(
                "00000200 | 00000000 | 03000000 | 00000000 | 0800 000000000000 | 08000000 | 04000200 | " +
                "01000000 | 02000000 | 01000000 | 6100"),
            bytes);
        Assert.Equal("a", new NdrReader(bytes).ReadVariant().Value!.Value);
    }

    [Fact]
    public void Null_variant_pointer_reads_as_null()
    {
        Assert.Null(new NdrReader(Write(w => w.WriteVariant(null))).ReadVariant().Value);
    }

    public static TheoryData<int> ScalarVariantIndexes() => [.. Enumerable.Range(0, ScalarVariants().Length)];

    private static OleVariant[] ScalarVariants() =>
    [
        OleVariant.Null,
        OleVariant.FromSByte(-3),
        OleVariant.FromByte(200),
        OleVariant.FromInt16(-30000),
        OleVariant.FromUInt16(60000),
        OleVariant.FromInt32(int.MinValue),
        OleVariant.FromUInt32(uint.MaxValue),
        OleVariant.FromInt64(long.MinValue),
        OleVariant.FromUInt64(ulong.MaxValue),
        OleVariant.FromSingle(3.5f),
        OleVariant.FromDouble(-0.125),
        OleVariant.FromBoolean(true),
        OleVariant.FromBoolean(false),
        OleVariant.FromError(unchecked((int)0x80041001)),
        OleVariant.FromString("héllo wörld"),
        OleVariant.FromString(null),
        OleVariant.FromString(""),
        OleVariant.FromUnknown([9, 8, 7]),
        OleVariant.FromDispatch(null),
        new(OleVarType.Cy, 52500L),
        new(OleVarType.Date, 45000.5),
        new(OleVarType.Int, -1),
        new(OleVarType.UInt, 1u)
    ];

    [Theory]
    [MemberData(nameof(ScalarVariantIndexes))]
    public void Scalar_variants_round_trip(int index)
    {
        var variant = ScalarVariants()[index];
        var bytes = Write(w => w.WriteVariant(variant));
        var reader = new NdrReader(bytes);
        var read = reader.ReadVariant().Value!;

        Assert.True(reader.IsAtEnd);
        Assert.Equal(variant.Type, read.Type);
        Assert.Equal(variant.Value, read.Value);
    }

    [Fact]
    public void Boolean_variant_uses_variant_bool()
    {
        var bytes = Write(w => w.WriteVariant(OleVariant.FromBoolean(true)));
        Assert.Equal(Hex.Parse("FFFF"), bytes[^2..]);
    }

    [Fact]
    public void Array_variant_masks_the_element_type_in_the_discriminant()
    {
        var variant = OleVariant.FromArray(OleSafeArray.FromVector(OleVarType.I4, [1, 2]));
        var bytes = Write(w => w.WriteVariant(variant));

        Assert.Equal(Hex.Parse("0320"), bytes[16..18]); // vt = VT_ARRAY | VT_I4
        Assert.Equal(Hex.Parse("00200000"), bytes[24..28]); // discriminant = VT_ARRAY
    }

    [Fact]
    public void Int32_safearray_layout()
    {
        var array = OleSafeArray.FromVector(OleVarType.I4, [1, 2, 3]);
        var bytes = Write(w => w.WriteSafeArray(array));

        Assert.Equal(
            Hex.Parse(
                "00000200 | 01000000 | 0100 8000 | 04000000 | 00000300 | 03000000 | " +
                "03000000 | 04000200 | 03000000 00000000 | " +
                "03000000 | 01000000 02000000 03000000"),
            bytes);

        var read = new NdrReader(bytes).ReadSafeArray().Value!;
        Assert.Equal(OleVarType.I4, read.ElementType);
        Assert.Equal([1, 2, 3], read.Elements);
        Assert.Equal(new OleSafeArrayBound(3, 0), Assert.Single(read.Bounds));
    }

    [Fact]
    public void Safearray_bounds_are_sent_in_reverse_order_and_restored()
    {
        var array = new OleSafeArray(
            OleVarType.I2,
            [new OleSafeArrayBound(2, 1), new OleSafeArrayBound(3, 0)],
            [(short)1, (short)2, (short)3, (short)4, (short)5, (short)6]);

        var bytes = Write(w => w.WriteSafeArray(array));
        var read = new NdrReader(bytes).ReadSafeArray().Value!;

        Assert.Equal(array.Bounds, read.Bounds);
        Assert.Equal(array.Elements, read.Elements);
    }

    [Fact]
    public void Eight_byte_elements_are_aligned_after_the_count()
    {
        var array = OleSafeArray.FromVector(OleVarType.R8, [1.5, -2.5]);
        var bytes = Write(w => w.WriteSafeArray(array));
        var read = new NdrReader(bytes).ReadSafeArray().Value!;
        Assert.Equal([1.5, -2.5], read.Elements);
    }

    [Fact]
    public void Bstr_safearray_round_trips_including_null_and_empty()
    {
        var array = OleSafeArray.FromStrings(["one", null, "", "four"]);
        var bytes = Write(w => w.WriteSafeArray(array));
        var reader = new NdrReader(bytes);
        var read = reader.ReadSafeArray().Value!;

        Assert.True(reader.IsAtEnd);
        Assert.Equal(OleVarType.Bstr, read.ElementType);
        Assert.Equal(["one", null, "", "four"], read.Elements);
    }

    [Fact]
    public void Variant_safearray_round_trips_with_nested_values()
    {
        var array = OleSafeArray.FromVector(
            OleVarType.Variant,
            [
                OleVariant.FromInt32(1),
                OleVariant.FromString("two"),
                null,
                OleVariant.FromArray(OleSafeArray.FromStrings(["x", "y"])),
            ]);

        var bytes = Write(w => w.WriteVariant(OleVariant.FromArray(array)));
        var reader = new NdrReader(bytes);
        var read = reader.ReadVariant().Value!;
        Assert.True(reader.IsAtEnd);

        var elements = ((OleSafeArray)read.Value!).Elements;
        Assert.Equal(1, ((OleVariant)elements[0]!).Value);
        Assert.Equal("two", ((OleVariant)elements[1]!).Value);
        Assert.Null(elements[2]);
        var inner = (OleSafeArray)((OleVariant)elements[3]!).Value!;
        Assert.Equal(["x", "y"], inner.Elements);
    }

    [Fact]
    public void Interface_pointer_safearrays_round_trip()
    {
        var array = OleSafeArray.FromVector(OleVarType.Unknown, [new byte[] { 1, 2 }, null]);
        var read = new NdrReader(Write(w => w.WriteSafeArray(array))).ReadSafeArray().Value!;
        Assert.Equal(new byte[] { 1, 2 }, read.Elements[0]);
        Assert.Null(read.Elements[1]);
    }

    [Fact]
    public void Embedded_in_a_structure_the_bstr_follows_the_structure()
    {
        // struct { BSTR a; uint32 n; BSTR b; }: both blobs come after n.
        var bytes = Write(w => w.Struct(s =>
        {
            s.WriteBstr("a");
            s.WriteUInt32(7);
            s.WriteBstr("b");
        }));

        Assert.Equal(
            Hex.Parse(
                "00000200 | 07000000 | 04000200 | " +
                "01000000 | 02000000 | 01000000 | 6100 0000 | " + // blob 'a' padded to 4 for next max_count
                "01000000 | 02000000 | 01000000 | 6200"),
            bytes);

        string? a = null;
        string? b = null;
        uint n = 0;
        new NdrReader(bytes).Struct(s =>
        {
            s.ReadBstr().Then(v => a = v);
            n = s.ReadUInt32();
            s.ReadBstr().Then(v => b = v);
            return 0;
        });

        Assert.Equal("a", a);
        Assert.Equal(7u, n);
        Assert.Equal("b", b);
    }

    [Fact]
    public void Invalid_values_are_rejected_when_building()
    {
        Assert.Throws<ArgumentException>(() => new OleVariant(OleVarType.I4, "x"));
        Assert.Throws<ArgumentException>(() => new OleVariant(OleVarType.Empty, 1));
        Assert.Throws<NotSupportedException>(() => new OleVariant(OleVarType.Decimal));
        Assert.Throws<NotSupportedException>(() => new OleVariant(OleVarType.I4 | OleVarType.ByRef, 1));
        Assert.Throws<ArgumentException>(() => OleSafeArray.FromVector(OleVarType.I4, []));
        Assert.Throws<ArgumentException>(() => OleSafeArray.FromVector(OleVarType.I4, ["x"]));
        Assert.Throws<ArgumentException>(
            () => new OleSafeArray(OleVarType.I4, [new OleSafeArrayBound(2, 0)], [1, 2, 3]));
    }

    [Fact]
    public void Corrupt_input_only_ever_throws_NdrFormatException()
    {
        var samples = new (byte[] Bytes, Action<NdrReader> Read)[]
        {
            (Write(w => w.WriteVariant(OleVariant.FromString("hello"))), r => r.ReadVariant()),
            (Write(w => w.WriteVariant(OleVariant.FromArray(OleSafeArray.FromStrings(["a", "bb", null])))),
                r => r.ReadVariant()),
            (Write(w => w.WriteVariant(OleVariant.FromArray(
                OleSafeArray.FromVector(OleVarType.Variant, [OleVariant.FromInt64(1), OleVariant.FromString("z")])))),
                r => r.ReadVariant()),
            (Write(w => w.WriteSafeArray(OleSafeArray.FromVector(OleVarType.R8, [1.0, 2.0, 3.0]))),
                r => r.ReadSafeArray()),
            (Write(w => w.WriteMInterfacePointer([1, 2, 3, 4])), r => r.ReadMInterfacePointer()),
        };

        var random = new Random(12345);
        foreach (var (bytes, read) in samples)
        {
            read(new NdrReader(bytes)); // the pristine sample must parse

            for (var round = 0; round < 3000; round++)
            {
                var mutated = (byte[])bytes.Clone();
                for (var i = random.Next(1, 4); i > 0; i--)
                    mutated[random.Next(mutated.Length)] = (byte)random.Next(256);

                if (random.Next(4) == 0)
                    mutated = mutated[..random.Next(mutated.Length)];

                try
                {
                    read(new NdrReader(mutated));
                }
                catch (NdrFormatException)
                {
                    // Rejecting bad input is the expected outcome; any other exception fails the test.
                }
            }
        }
    }
}
