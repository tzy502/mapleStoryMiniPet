using System;
using System.IO;
using System.IO.Compression;

namespace MiniPet.Utils
{
    public static class PngEncoder
    {
        public static byte[] Encode(byte[] rawBgra, int width, int height)
        {
            using var ms = new MemoryStream();
            ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);

            var ihdr = new byte[13];
            WriteInt32(ihdr, 0, width);
            WriteInt32(ihdr, 4, height);
            ihdr[8] = 8;
            ihdr[9] = 6;
            WriteChunk(ms, "IHDR", ihdr);

            int stride = width * 4 + 1;
            var filtered = new byte[stride * height];
            for (int y = 0; y < height; y++)
            {
                filtered[y * stride] = 0;
                for (int x = 0; x < width; x++)
                {
                    int src = (y * width + x) * 4;
                    int dst = y * stride + 1 + x * 4;
                    filtered[dst] = rawBgra[src + 2];
                    filtered[dst + 1] = rawBgra[src + 1];
                    filtered[dst + 2] = rawBgra[src];
                    filtered[dst + 3] = rawBgra[src + 3];
                }
            }

            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, true))
                zlib.Write(filtered, 0, filtered.Length);
            WriteChunk(ms, "IDAT", compressed.ToArray());
            WriteChunk(ms, "IEND", Array.Empty<byte>());
            return ms.ToArray();
        }

        static void WriteChunk(Stream ms, string type, byte[] data)
        {
            WriteInt32(ms, data.Length);
            var tb = System.Text.Encoding.ASCII.GetBytes(type);
            ms.Write(tb, 0, 4);
            ms.Write(data, 0, data.Length);
            var crcIn = new byte[4 + data.Length];
            Array.Copy(tb, 0, crcIn, 0, 4);
            Array.Copy(data, 0, crcIn, 4, data.Length);
            WriteInt32(ms, (int)Crc32(crcIn));
        }

        static void WriteInt32(Stream ms, int v) { ms.WriteByte((byte)(v >> 24)); ms.WriteByte((byte)(v >> 16)); ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        static void WriteInt32(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

        static readonly uint[] CT = new uint[256];
        static PngEncoder()
        {
            for (int i = 0; i < 256; i++) { uint c = (uint)i; for (int j = 0; j < 8; j++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; CT[i] = c; }
        }
        static uint Crc32(byte[] d) { uint c = 0xFFFFFFFF; foreach (byte b in d) c = CT[(c ^ b) & 0xFF] ^ (c >> 8); return c ^ 0xFFFFFFFF; }
    }
}
