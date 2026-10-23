using System.Text;

namespace IntelligentScheduler.Application;

/// <summary>
/// Reads what a model needs in memory from the header of its GGUF file: how many blocks it
/// has, how many of them keep a key/value cache, and how wide that cache is per token.
///
/// The figure matters because it is the difference between the models in the catalogue. A
/// conventional 8B transformer keeps a cache in every block; a hybrid such as Qwen3.5 keeps one
/// in every fourth, so the same memory holds four times the context. Guessing the conventional
/// figure for every file would hand a hybrid a quarter of the context it could have had.
///
/// Only the header is read, and a file that does not parse gives null rather than a guess.
/// </summary>
public static class GgufMetadata
{
    private const uint Magic = 0x46554747; // "GGUF", little-endian
    private const int LargestString = 64 * 1024 * 1024;

    public static ModelMemoryShape? ReadShape(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            return ReadShape(stream, stream.Length);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static ModelMemoryShape? ReadShape(Stream stream, long fileLength)
    {
        try
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (reader.ReadUInt32() != Magic) return null;
            if (reader.ReadUInt32() < 2) return null;
            reader.ReadUInt64(); // tensor count
            var count = reader.ReadUInt64();

            var architecture = string.Empty;
            var values = new Dictionary<string, object>(StringComparer.Ordinal);
            for (ulong index = 0; index < count; index++)
            {
                var key = ReadString(reader);
                var type = reader.ReadUInt32();
                var value = ReadValue(reader, type);
                if (key == "general.architecture" && value is string name) architecture = name;
                else if (value is not null) values[key] = value;

                // Everything wanted here comes before the tokenizer, which is most of the header.
                if (key.StartsWith("tokenizer.", StringComparison.Ordinal) && architecture.Length > 0 &&
                    values.ContainsKey($"{architecture}.block_count")) break;
            }
            if (architecture.Length == 0) return null;

            int? Number(string suffix) => values.TryGetValue($"{architecture}.{suffix}", out var found) ? AsInt(found) : null;
            var layers = Number("block_count") ?? 0;
            if (layers <= 0) return null;

            var heads = Math.Max(1, Number("attention.head_count") ?? 1);
            var embedding = Number("embedding_length") ?? 0;
            var keyLength = Number("attention.key_length") ?? (embedding > 0 ? embedding / heads : 128);
            var valueLength = Number("attention.value_length") ?? keyLength;

            // Heads per block. A hybrid lists 0 for the blocks without attention, or instead
            // says once how often a block with attention comes round.
            long kvHeads;
            if (values.TryGetValue($"{architecture}.attention.head_count_kv", out var perBlock) && perBlock is int[] list)
                kvHeads = list.Take(layers).Sum(heads => (long)Math.Max(0, heads));
            else
            {
                var perLayer = Number("attention.head_count_kv") ?? heads;
                var interval = Math.Max(1, Number("full_attention_interval") ?? 1);
                kvHeads = (long)perLayer * (layers / interval);
            }

            var perToken = Math.Max(1024, kvHeads * (keyLength + valueLength) * 2);
            var context = Number("context_length") ?? 32_768;
            return new ModelMemoryShape(fileLength, layers, perToken, context);
        }
        catch (Exception exception) when (exception is EndOfStreamException or InvalidDataException
                                               or IOException or OverflowException or ArgumentException)
        {
            return null;
        }
    }

    private static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadUInt64();
        if (length > LargestString) throw new InvalidDataException("A GGUF string is implausibly long.");
        return Encoding.UTF8.GetString(reader.ReadBytes((int)length));
    }

    /// <summary>
    /// Reads one value. Scalars and short integer arrays are kept; anything else — the
    /// tokenizer's vocabulary above all — is stepped over without being held.
    /// </summary>
    private static object? ReadValue(BinaryReader reader, uint type)
    {
        switch (type)
        {
            case 0: return (int)reader.ReadByte();
            case 1: return (int)reader.ReadSByte();
            case 2: return (int)reader.ReadUInt16();
            case 3: return (int)reader.ReadInt16();
            case 4: return (long)reader.ReadUInt32();
            case 5: return reader.ReadInt32();
            case 6: return reader.ReadSingle();
            case 7: return reader.ReadBoolean();
            case 8: return ReadString(reader);
            case 10: return (long)reader.ReadUInt64();
            case 11: return reader.ReadInt64();
            case 12: return reader.ReadDouble();
            case 9:
                var elementType = reader.ReadUInt32();
                var length = reader.ReadUInt64();
                if (elementType is 4 or 5 && length <= 4096)
                {
                    var items = new int[length];
                    for (ulong index = 0; index < length; index++)
                        items[index] = elementType == 4 ? (int)reader.ReadUInt32() : reader.ReadInt32();
                    return items;
                }
                SkipArray(reader, elementType, length);
                return null;
            default:
                throw new InvalidDataException($"Unknown GGUF value type {type}.");
        }
    }

    private static void SkipArray(BinaryReader reader, uint elementType, ulong length)
    {
        var width = elementType switch
        {
            0 or 1 or 7 => 1,
            2 or 3 => 2,
            4 or 5 or 6 => 4,
            10 or 11 or 12 => 8,
            _ => 0
        };
        if (width > 0)
        {
            reader.BaseStream.Seek(checked((long)length * width), SeekOrigin.Current);
            return;
        }
        for (ulong index = 0; index < length; index++)
        {
            if (elementType == 8)
            {
                var size = reader.ReadUInt64();
                if (size > LargestString) throw new InvalidDataException("A GGUF string is implausibly long.");
                reader.BaseStream.Seek((long)size, SeekOrigin.Current);
            }
            else if (elementType == 9)
            {
                var innerType = reader.ReadUInt32();
                SkipArray(reader, innerType, reader.ReadUInt64());
            }
            else throw new InvalidDataException($"Unknown GGUF array element type {elementType}.");
        }
    }

    private static int? AsInt(object value) => value switch
    {
        int number => number,
        long number when number is >= int.MinValue and <= int.MaxValue => (int)number,
        long => int.MaxValue,
        int[] { Length: > 0 } numbers => numbers.Max(),
        _ => null
    };
}
