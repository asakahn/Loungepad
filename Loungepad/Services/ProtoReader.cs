using System.Text;

namespace Loungepad.Services;

/// <summary>
/// Just enough of the protobuf wire format to read the store clients' own cache files: Battle.net's
/// product.db and Ubisoft Connect's configuration and ownership caches. No schema, no generated
/// classes -- each reader walks the fields it knows by number and skips the rest, which is also what
/// keeps it working when a client adds fields of its own. A malformed file ends the walk; it never
/// throws past <see cref="Fields"/>.
/// </summary>
internal static class ProtoReader
{
    /// <summary>One field: its number, its wire type, and either the number it carries (varint and
    /// fixed types) or where its bytes are (length-delimited).</summary>
    public readonly record struct Field(int Number, int Wire, ulong Value, int Start, int Length)
    {
        public bool IsBytes => Wire == 2;
    }

    /// <summary>Every field in <paramref name="data"/>[start..end), in order. Stops quietly at the
    /// first thing it cannot read, returning what came before it.</summary>
    public static List<Field> Fields(byte[] data, int start, int end)
    {
        var fields = new List<Field>();
        var i = start;
        end = Math.Min(end, data.Length);
        try
        {
            while (i < end)
            {
                var tag = Varint(data, ref i);
                var number = (int)(tag >> 3);
                var wire = (int)(tag & 7);
                if (number <= 0) break;
                switch (wire)
                {
                    case 0:
                        fields.Add(new Field(number, wire, Varint(data, ref i), 0, 0));
                        break;
                    case 1:
                        if (i + 8 > end) return fields;
                        fields.Add(new Field(number, wire, BitConverter.ToUInt64(data, i), 0, 0));
                        i += 8;
                        break;
                    case 2:
                        var len = Varint(data, ref i);
                        if (len > (ulong)(end - i)) return fields;
                        fields.Add(new Field(number, wire, 0, i, (int)len));
                        i += (int)len;
                        break;
                    case 5:
                        if (i + 4 > end) return fields;
                        fields.Add(new Field(number, wire, BitConverter.ToUInt32(data, i), 0, 0));
                        i += 4;
                        break;
                    default:
                        return fields;
                }
            }
        }
        catch (IndexOutOfRangeException) { /* a truncated last field */ }
        return fields;
    }

    public static List<Field> Fields(byte[] data, Field message) => Fields(data, message.Start, message.Start + message.Length);

    public static string Text(byte[] data, Field f) => Encoding.UTF8.GetString(data, f.Start, f.Length);

    private static ulong Varint(byte[] data, ref int i)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            var b = data[i++];
            value |= (ulong)(b & 0x7f) << shift;
            if ((b & 0x80) == 0) return value;
        }
        throw new IndexOutOfRangeException("varint too long");
    }
}
