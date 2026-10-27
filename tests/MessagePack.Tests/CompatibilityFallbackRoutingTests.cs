using System.Buffers;
using SerializerFoundation;

namespace MessagePack.Tests;

public struct RoutedValue
{
    public int X;
}

// A formatter with the DOWNLEVEL generic shape: no `allows ref struct` on the buffer
// type parameters — exactly what a netstandard-compiled formatter library presents after
// assembly unification on net9+. It can only close over the Compatible buffers.
// The nested int formatter exercises resolution INSIDE the compatibility graph.
sealed class RoutedValueFormatter<TWriteBuffer, TReadBuffer> : IMessagePackFormatter<TWriteBuffer, TReadBuffer, RoutedValue>
    where TWriteBuffer : struct, IWriteBuffer
    where TReadBuffer : struct, IReadBuffer
{
    IMessagePackFormatter<TWriteBuffer, TReadBuffer, int> intFormatter = default!;

    public void Initialize(MessagePackFormatterResolver resolver)
    {
        intFormatter = resolver.GetFormatter<TWriteBuffer, TReadBuffer, int>();
    }

    public void Serialize(ref TWriteBuffer buffer, ref SerializeState state, RoutedValue value)
    {
        intFormatter.Serialize(ref buffer, ref state, value.X);
    }

    public void Deserialize(ref TReadBuffer buffer, ref DeserializeState state, ref RoutedValue value)
    {
        var x = 0;
        intFormatter.Deserialize(ref buffer, ref state, ref x);
        value = new RoutedValue { X = x };
    }
}

// SIMULATION of a downlevel-compiled GENERATED factory: Type-based member only, and its
// dispatch knows only the Compatible buffer pairs — unknown pairs (the ref struct ones a
// downlevel compilation cannot even name) return null. This is the contract shape the
// resolver's fallback probe keys on.
#pragma warning disable MsgPack102 // simulating a factory compiled against the downlevel surface
sealed class CompatiblePairOnlyFactory : MessagePackFormatterFactory
{
    public override object? CreateFormatter(Type writeBufferType, Type readBufferType, Type valueType)
    {
        if (valueType != typeof(RoutedValue))
        {
            return null;
        }
        if (writeBufferType == typeof(CompatibleArrayPoolListWriteBuffer) && readBufferType == typeof(CompatibleReadOnlySpanReadBuffer))
        {
            return new RoutedValueFormatter<CompatibleArrayPoolListWriteBuffer, CompatibleReadOnlySpanReadBuffer>();
        }
        if (writeBufferType == typeof(CompatibleArrayPoolListWriteBuffer) && readBufferType == typeof(CompatibleReadOnlySequenceReadBuffer))
        {
            return new RoutedValueFormatter<CompatibleArrayPoolListWriteBuffer, CompatibleReadOnlySequenceReadBuffer>();
        }
        if (writeBufferType == typeof(CompatibleBufferWriterWriteBuffer) && readBufferType == typeof(CompatibleReadOnlySpanReadBuffer))
        {
            return new RoutedValueFormatter<CompatibleBufferWriterWriteBuffer, CompatibleReadOnlySpanReadBuffer>();
        }
        return null;
    }
}
#pragma warning restore MsgPack102

public class CompatibilityFallbackRoutingTests
{
    static MessagePackSerializerOptions MakeOptions(out List<Type> fallbackTypes, bool throwOnLegacyFormatter = false)
    {
        var resolver = new MessagePackFormatterResolver(
            MessagePackFormatterFactory.Combine(
                new CompatiblePairOnlyFactory(),
                BuiltInFormatterFactory.Instance,
                GenericFormatterFactory.Instance)) { ThrowOnLegacyFormatter = throwOnLegacyFormatter };
        var captured = new List<Type>();
        resolver.CompatibilityFallback += captured.Add;
        fallbackTypes = captured;
        return new MessagePackSerializerOptions(resolver);
    }

    [Fact]
    public void DownlevelOnlyFormatterRoundTripsViaCompatibilityPath()
    {
        var options = MakeOptions(out var fallbackTypes);

        var payload = MessagePackSerializer.Serialize(new RoutedValue { X = 12345 }, options);
        var result = MessagePackSerializer.Deserialize<RoutedValue>(payload, options);

        Assert.Equal(12345, result.X);
        Assert.Contains(typeof(RoutedValue), fallbackTypes);
    }

    [Fact]
    public void TaintPropagatesFromNestedNodeToRoot()
    {
        // the ROOT (List<>) is fully fast-capable; only a child is downlevel-only. The
        // whole root graph must route, and the event reports the ROOT type.
        var options = MakeOptions(out var fallbackTypes);

        var value = new List<RoutedValue> { new() { X = 1 }, new() { X = -2 }, new() { X = 3 } };
        var payload = MessagePackSerializer.Serialize(value, options);
        var result = MessagePackSerializer.Deserialize<List<RoutedValue>>(payload, options);

        Assert.Equal(value.Select(v => v.X), result!.Select(v => v.X));
        Assert.Contains(typeof(List<RoutedValue>), fallbackTypes);
    }

    [Fact]
    public void UntaintedTypesStayOnTheFastPath()
    {
        var options = MakeOptions(out var fallbackTypes);

        // routes RoutedValue first so its decision is cached...
        MessagePackSerializer.Serialize(new RoutedValue { X = 1 }, options);
        // ...then an unrelated type must resolve clean: no new fallback notification
        var payload = MessagePackSerializer.Serialize(42, options);
        Assert.Equal(42, MessagePackSerializer.Deserialize<int>(payload, options));

        Assert.Equal([typeof(RoutedValue)], fallbackTypes);
    }

    [Fact]
    public void FallbackEventFiresOncePerRootType()
    {
        var options = MakeOptions(out var fallbackTypes);

        MessagePackSerializer.Serialize(new RoutedValue { X = 1 }, options);
        MessagePackSerializer.Serialize(new RoutedValue { X = 2 }, options);
        var payload = MessagePackSerializer.Serialize(new RoutedValue { X = 3 }, options);
        MessagePackSerializer.Deserialize<RoutedValue>(payload, options);

        Assert.Equal([typeof(RoutedValue)], fallbackTypes);
    }

    [Fact]
    public void BufferWriterEntryRoutesToo()
    {
        var options = MakeOptions(out _);

        var writer = new ArrayBufferWriter<byte>();
        MessagePackSerializer.Serialize(writer, new RoutedValue { X = 777 }, options);
        var result = MessagePackSerializer.Deserialize<RoutedValue>(writer.WrittenSpan, options);

        Assert.Equal(777, result.X);
    }

    [Fact]
    public void SequenceEntryRoutesToo()
    {
        var options = MakeOptions(out _);

        var payload = MessagePackSerializer.Serialize(new RoutedValue { X = 31337 }, options);
        var result = MessagePackSerializer.Deserialize<RoutedValue>(new ReadOnlySequence<byte>(payload), options);

        Assert.Equal(31337, result.X);
    }

#if NET9_0_OR_GREATER
    [Fact]
    public void ThrowOnLegacyFormatterRestoresTheThrow()
    {
        var options = MakeOptions(out var fallbackTypes, throwOnLegacyFormatter: true);

        var ex = Assert.Throws<InvalidOperationException>(
            () => MessagePackSerializer.Serialize(new RoutedValue { X = 1 }, options));
        Assert.Contains("net9.0", ex.Message);
        Assert.Empty(fallbackTypes);
    }

    /// <summary>
    /// Ensures that the ref TWriteBuffer entry point serializes a downlevel-only formatter's value by staging it through the
    /// Compatible buffers, producing the same bytes as the byte[] entry point instead of throwing.
    /// </summary>
    [Fact]
    public void RefBufferEntryRoutesToo()
    {
        var options = MakeOptions(out var fallbackTypes);
        var value = new RoutedValue { X = 4242 };

        var writer = new ArrayBufferWriter<byte>();
        var buffer = new BufferWriterWriteBuffer(writer);
        MessagePackSerializer.Serialize(ref buffer, value, options);
        buffer.Flush();

        Assert.Equal(MessagePackSerializer.Serialize(value, options), writer.WrittenSpan.ToArray());
        Assert.Contains(typeof(RoutedValue), fallbackTypes);
    }

    /// <summary>
    /// Ensures that the non-generic ref TWriteBuffer entry point, which forwards to the generic one, routes a downlevel-only
    /// formatter's value too.
    /// </summary>
    [Fact]
    public void NonGenericRefBufferEntryRoutesToo()
    {
        var options = MakeOptions(out _);
        var value = new RoutedValue { X = -99 };

        var writer = new ArrayBufferWriter<byte>();
        var buffer = new BufferWriterWriteBuffer(writer);
        MessagePackSerializer.Serialize(typeof(RoutedValue), ref buffer, value, options);
        buffer.Flush();

        Assert.Equal(MessagePackSerializer.Serialize(value, options), writer.WrittenSpan.ToArray());
    }

    /// <summary>
    /// Ensures that the routed value is appended at the caller's current position: bytes the caller wrote before and after it
    /// stay in place, as when MessagePack is embedded inside another protocol.
    /// </summary>
    [Fact]
    public void RefBufferEntryAppendsRoutedValueInPlace()
    {
        var options = MakeOptions(out _);
        var value = new List<RoutedValue> { new() { X = 1 }, new() { X = 70000 } };

        var writer = new ArrayBufferWriter<byte>();
        var buffer = new BufferWriterWriteBuffer(writer);
        buffer.WriteArrayHeader(3);
        buffer.WriteNil();
        MessagePackSerializer.Serialize(ref buffer, value, options);
        buffer.WriteBoolean(true);
        buffer.Flush();

        byte[] expected = [0x93, 0xc0, .. MessagePackSerializer.Serialize(value, options), 0xc3];
        Assert.Equal(expected, writer.WrittenSpan.ToArray());
    }

    /// <summary>
    /// Ensures that ThrowOnLegacyFormatter still turns the reroute into an error on the ref TWriteBuffer entry point.
    /// </summary>
    [Fact]
    public void ThrowOnLegacyFormatterRestoresTheThrowOnRefBufferEntry()
    {
        var options = MakeOptions(out var fallbackTypes, throwOnLegacyFormatter: true);

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            var buffer = new BufferWriterWriteBuffer(new ArrayBufferWriter<byte>());
            MessagePackSerializer.Serialize(ref buffer, new RoutedValue { X = 1 }, options);
        });
        Assert.Contains("net9.0", ex.Message);
        Assert.Empty(fallbackTypes);
    }
#endif
}
