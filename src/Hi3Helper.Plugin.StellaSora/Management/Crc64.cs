using System.Buffers;
using System.Buffers.Binary;

namespace Hi3Helper.Plugin.StellaSora.Management;

// OSS CRC64-ECMA (reflected ECMA polynomial, initial/final xor all ones).
internal static class Crc64
{
    private static readonly ulong[] Table = CreateTable();

    private static ulong[] CreateTable()
    {
        ulong[] table = new ulong[8 * 256];
        for (int i = 0; i < 256; i++)
        {
            ulong value = (ulong)i;
            for (int bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) != 0 ? 0xC96C5795D7870F42UL : 0);
            table[i] = value;
        }

        for (int slice = 1; slice < 8; slice++)
        for (int i = 0; i < 256; i++)
        {
            ulong value = table[(slice - 1) * 256 + i];
            table[slice * 256 + i] = table[(byte)value] ^ (value >> 8);
        }

        return table;
    }

    internal static ulong Hash(ReadOnlySpan<byte> bytes)
    {
        return ~Append(ulong.MaxValue, bytes);
    }

    private static ulong Append(ulong crc, ReadOnlySpan<byte> bytes)
    {
        int offset = 0;
        for (; offset <= bytes.Length - 8; offset += 8)
        {
            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]) ^ crc;
            crc = Table[7 * 256 + (byte)value] ^ Table[6 * 256 + (byte)(value >> 8)] ^
                  Table[5 * 256 + (byte)(value >> 16)] ^ Table[4 * 256 + (byte)(value >> 24)] ^
                  Table[3 * 256 + (byte)(value >> 32)] ^ Table[2 * 256 + (byte)(value >> 40)] ^
                  Table[256 + (byte)(value >> 48)] ^ Table[(byte)(value >> 56)];
        }

        for (; offset < bytes.Length; offset++) crc = Table[(byte)(crc ^ bytes[offset])] ^ (crc >> 8);
        return crc;
    }

    internal static async Task<ulong> HashFileAsync(string path, CancellationToken token, Action<long>? progress = null)
    {
        await using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(131072);
        try
        {
            ulong crc = ulong.MaxValue;
            int read;
            while ((read = await file.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                crc = Append(crc, buffer.AsSpan(0, read));
                progress?.Invoke(read);
            }

            return ~crc;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static async Task<bool> MatchesAsync(string path, GameFile file, CancellationToken token,
        Action<long>? progress = null)
    {
        FileInfo info = new(path);
        if (!info.Exists || info.Length != file.Size)
        {
            progress?.Invoke(file.Size);
            return false;
        }

        return await HashFileAsync(path, token, progress).ConfigureAwait(false) == file.Hash;
    }
}