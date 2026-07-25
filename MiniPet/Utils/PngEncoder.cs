using System;
using System.IO;
using System.IO.Compression;

namespace MiniPet.Utils
{
    /// <summary>
    /// 纯 C# PNG 编码器，不依赖 System.Drawing
    /// </summary>
    public static class PngEncoder
    {
        public static byte[] Encode(byte[] rawPixels, int width, int height, PngPixelFormat format)
        {
            using var ms = new MemoryStream();
            // PNG signature
            ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);

            // IHDR
            var ihdr = new byte[13];
            WriteInt32(ihdr, 0, width);
            WriteInt32(ihdr, 4, height);
            ihdr[8] = 8; // bit depth
            ihdr[9] = (byte)(format == PngPixelFormat.Bgra ? 6 : 2); // 6=RGBA, 2=RGB
            ihdr[10] = 0; // compression
            ihdr[11] = 0; // filter
            ihdr[12] = 0; // interlace
            WriteChunk(ms, "IHDR", ihdr);

            // IDAT — apply filter byte before each row, then deflate
            int bpp = format == PngPixelFormat.Bgra ? 4 : 3;
            int rawStride = width * bpp;
            int outStride = rawStride + 1; // +1 for filter byte
            var filtered = new byte[outStride * height];

            for (int y = 0; y < height; y++)
            {
                filtered[y * outStride] = 0; // filter: None
                for (int x = 0; x < rawStride; x++)
                {
                    if (format == PngPixelFormat.Bgra)
                    {
                        // BGR → RGB swap: pixel is [B,G,R,A]
                        int px = x % 4;
                        if (px == 0) filtered[y * outStride + 1 + x + 2] = rawPixels[y * rawStride + x]; // B → R pos
                        else if (px == 2) filtered[y * outStride + 1 + x - 2] = rawPixels[y * rawStride + x]; // R → B pos
                        else filtered[y * outStride + 1 + x] = rawPixels[y * rawStride + x]; // G, A stay
                    }
                    else
                    {
                        filtered[y * outStride + 1 + x] = rawPixels[y * rawStride + x];
                    }
                }
            }

            using var compressed = new MemoryStream();
            using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, true))
            {
                deflate.Write(filtered, 0, filtered.Length);
            }
            WriteChunk(ms, "IDAT", compressed.ToArray());

            // IEND
            WriteChunk(ms, "IEND", Array.Empty<byte>());

            return ms.ToArray();
        }

        private static void WriteChunk(Stream ms, string type, byte[] data)
        {
            WriteInt32(ms, data.Length);
            var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            ms.Write(typeBytes, 0, 4);
            ms.Write(data, 0, data.Length);

            // CRC over type + data
            var crcData = new byte[4 + data.Length];
            Array.Copy(typeBytes, 0, crcData, 0, 4);
            Array.Copy(data, 0, crcData, 4, data.Length);
            var crc = Crc32(crcData);
            WriteInt32(ms, (int)crc);
        }

        private static void WriteInt32(Stream ms, int value)
        {
            var buf = new byte[4];
            WriteInt32(buf, 0, value);
            ms.Write(buf, 0, 4);
        }

        private static void WriteInt32(byte[] buf, int offset, int value)
        {
            buf[offset] = (byte)(value >> 24);
            buf[offset + 1] = (byte)(value >> 16);
            buf[offset + 2] = (byte)(value >> 8);
            buf[offset + 3] = (byte)value;
        }

        private static readonly uint[] CrcTable = new uint[256];
        static PngEncoder()
        {
            for (int i = 0; i < 256; i++)
            {
                uint c = (uint)i;
                for (int j = 0; j < 8; j++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                CrcTable[i] = c;
            }
        }

        private static uint Crc32(byte[] data)
        {
            uint c = 0xFFFFFFFF;
            foreach (byte b in data)
                c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFF;
        }
    }

    public enum PngPixelFormat
    {
        Rgb,
        Bgra
    }
}
