using System.Buffers;
using Game;
using MessagePack;
using MessagePack.Formatters;

namespace Nordicandia.Contracts;

/// <summary>Preserves the client's custom multiplicative-attribute representation losslessly.</summary>
public sealed class AttributeArrayFormatter : IMessagePackFormatter<GameAttributeValueDoubleArray>
{
    public void Serialize(ref MessagePackWriter writer, GameAttributeValueDoubleArray value, MessagePackSerializerOptions options)
    {
        if (value?.WireData == null) writer.WriteNil();
        else writer.WriteRaw(value.WireData);
    }
    public GameAttributeValueDoubleArray Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil()) return null;
        return new GameAttributeValueDoubleArray { WireData = reader.ReadRaw().ToArray() };
    }
}
