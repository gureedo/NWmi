using NWmi.Wmio;

namespace NWmi.Tests.Wmio;

/// <summary>Randomly mutated well-formed data must never produce anything other than <see cref="WmioFormatException"/>.</summary>
public class WmioCorruptionTests
{
    private static byte[] SampleClassBytes()
    {
        var @class = new WmioClass(
            "Sample",
            ["Base"],
            [new WmioQualifier("Description", QualifierFlavor.PropagateToInstance, WbemType.String, "a class")],
            [
                new WmioProperty(
                    "Id", WbemType.Sint32, true, 0, 0,
                    [new WmioQualifier("key", QualifierFlavor.NotOverridable, WbemType.Boolean, true)],
                    new NullAndDefaultFlag(false, false), 1, 0),
                new WmioProperty("Name", WbemType.String, false, 1, 1, [], new NullAndDefaultFlag(true, false), null, 0),
                new WmioProperty("Tags", WbemType.String | WbemType.ArrayFlag, false, 2, 1, [], new NullAndDefaultFlag(true, false), null, 0),
            ],
            [new WmioMethod("Go", false, 1, [1, 2], [3])]);

        var w = new WmioWriter();
        @class.Write(w, hasMethodsPart: true);
        return w.ToArray();
    }

    private static byte[] SampleInstanceBytes()
    {
        var @class = new WmioClass(
            "Sample", [], [],
            [
                new WmioProperty("Id", WbemType.Sint32, false, 0, 0, [], new NullAndDefaultFlag(false, false), 1, 0),
                new WmioProperty("Name", WbemType.String, false, 1, 0, [], new NullAndDefaultFlag(true, false), null, 0),
                new WmioProperty("Tags", WbemType.String | WbemType.ArrayFlag, false, 2, 0, [], new NullAndDefaultFlag(true, false), null, 0),
            ],
            []);
        var instance = WmioInstance.Create(@class, new Dictionary<string, object?>
        {
            ["Name"] = "hello",
            ["Tags"] = new object?[] { "a", "b" },
        });
        return WmioObjectBlock.ForInstance(instance).EncodeEncodingUnit();
    }

    private static void FuzzOnce(byte[] original, Action<byte[]> decode, int seed)
    {
        decode(original); // the pristine sample must decode without throwing

        var random = new Random(seed);
        for (var round = 0; round < 4000; round++)
        {
            var mutated = (byte[])original.Clone();
            for (var i = random.Next(1, 4); i > 0; i--)
                mutated[random.Next(mutated.Length)] = (byte)random.Next(256);

            if (random.Next(4) == 0)
                mutated = mutated[..random.Next(mutated.Length)];

            try
            {
                decode(mutated);
            }
            catch (WmioFormatException)
            {
                // Expected for corrupt input; any other exception type fails the test.
            }
        }
    }

    [Fact]
    public void Corrupt_classes_only_ever_throw_WmioFormatException()
    {
        FuzzOnce(SampleClassBytes(), bytes => WmioClass.Read(new WmioReader(bytes), hasMethodsPart: true), 101);
    }

    [Fact]
    public void Corrupt_instance_ObjectBlocks_only_ever_throw_WmioFormatException()
    {
        FuzzOnce(SampleInstanceBytes(), bytes => WmioObjectBlock.DecodeEncodingUnit(bytes), 202);
    }

    [Fact]
    public void Deeply_nested_embedded_objects_do_not_cause_unbounded_recursion()
    {
        // An embedded CIM-TYPE-OBJECT value can itself contain another, recursively; a hostile or malformed
        // blob should fail cleanly rather than overflow the stack.
        var innerClass = new WmioClass(
            "Leaf", [], [],
            [new WmioProperty("X", WbemType.Sint32, false, 0, 0, [], new NullAndDefaultFlag(true, false), null, 0)],
            []);
        var current = WmioObjectBlock.ForInstance(WmioInstance.Create(innerClass, new Dictionary<string, object?> { ["X"] = 0 }));

        var wrapperClass = new WmioClass(
            "Wrapper", [], [],
            [new WmioProperty("Nested", WbemType.Object, false, 0, 0, [], new NullAndDefaultFlag(true, false), null, 0)],
            []);

        for (var depth = 0; depth < 50; depth++)
        {
            var instance = WmioInstance.Create(wrapperClass, new Dictionary<string, object?> { ["Nested"] = current });
            current = WmioObjectBlock.ForInstance(instance);
        }

        var bytes = current.EncodeEncodingUnit();
        var decoded = WmioObjectBlock.DecodeEncodingUnit(bytes);
        Assert.False(decoded.IsClass);
    }
}
