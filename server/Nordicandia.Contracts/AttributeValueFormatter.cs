using Game;
using MessagePack;
using MessagePack.Formatters;

namespace Nordicandia.Contracts;

// The client uses a custom formatter: an attribute is a number, not a map of fields.
public sealed class AttributeValueFormatter : IMessagePackFormatter<GameAttributeValue>
{
    public void Serialize(ref MessagePackWriter writer, GameAttributeValue value, MessagePackSerializerOptions options)
    {
        // The desktop dictionary formatter reads integer attributes with ReadInt32.
        // ReadDouble accepts integer tokens too, so integral values are compatible
        // with both attribute kinds. Do not change fractional or out-of-range values.
        var number = value.ValueD;
        if (number >= int.MinValue && number <= int.MaxValue && number == Math.Truncate(number))
            writer.Write((int)number);
        else writer.Write(number);
    }
    public GameAttributeValue Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        => new() { ValueD = reader.ReadDouble() };
}
