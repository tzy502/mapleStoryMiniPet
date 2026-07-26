using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using WzComparerR2.WzLib;

namespace MiniPet.Utils
{
    /// <summary>
    /// 像素解码 + PNG 编码。像素格式解码逻辑完全参照 R2 的 Wz_Png.ExtractPng + ImageCodec。
    /// 输出 BGRA32（Format32bppArgb 字节序）。
    /// </summary>
    public static class PngEncoder
    {
        /// <summary>按 R2 ExtractPng 内部逻辑解码 Wz_Png → BGRA32</summary>
        public static byte[] DecodePixels(Wz_Png png)
        {
            var raw = png.GetRawData();
            int w = png.Width, h = png.Height;
            var bgra = new byte[w * h * 4];
            Wz_TextureFormat fmt = png.Format;

            switch (fmt)
            {
                case Wz_TextureFormat.ARGB4444:
                    // raw: 每像素 2 字节 [G4|B4][A4|R4]（小端）, 即 ushort = B4|G4<<4|R4<<8|A4<<12
                    // output: BGRA32, 4→8 位复制展开
                    for (int i = 0; i < w * h; i++)
                    {
                        int b0 = raw[i * 2], b1 = raw[i * 2 + 1];
                        bgra[i * 4]     = N4to8(b0 & 0xF);     // B（低半字节）
                        bgra[i * 4 + 1] = N4to8(b0 >> 4);       // G（高半字节）
                        bgra[i * 4 + 2] = N4to8(b1 & 0xF);     // R（低半字节）
                        bgra[i * 4 + 3] = N4to8(b1 >> 4);       // A（高半字节）
                    }
                    break;

                case Wz_TextureFormat.ARGB8888:
                    // raw 已经是 BGRA32，直接拷贝
                    Buffer.BlockCopy(raw, 0, bgra, 0, Math.Min(raw.Length, bgra.Length));
                    break;

                case Wz_TextureFormat.ARGB1555:
                    // raw: 每像素 2 字节 ushort 小端 [B5|G5|R5|A1]
                    for (int i = 0; i < w * h; i++)
                    {
                        ushort val = (ushort)(raw[i * 2] | (raw[i * 2 + 1] << 8));
                        bgra[i * 4]     = N5to8(val & 0x1F);          // B bits 0-4
                        bgra[i * 4 + 1] = N5to8((val >> 5) & 0x1F);   // G bits 5-9
                        bgra[i * 4 + 2] = N5to8((val >> 10) & 0x1F);  // R bits 10-14
                        bgra[i * 4 + 3] = (byte)((val >> 15) * 255);  // A bit 15
                    }
                    break;

                case Wz_TextureFormat.RGB565:
                    // raw: 每像素 2 字节 ushort 小端 [B5|G6|R5], A 不透明
                    for (int i = 0; i < w * h; i++)
                    {
                        ushort val = (ushort)(raw[i * 2] | (raw[i * 2 + 1] << 8));
                        bgra[i * 4]     = N5to8(val & 0x1F);           // B bits 0-4
                        bgra[i * 4 + 1] = N6to8((val >> 5) & 0x3F);    // G bits 5-10
                        bgra[i * 4 + 2] = N5to8((val >> 11) & 0x1F);   // R bits 11-15
                        bgra[i * 4 + 3] = 255;                          // A 不透明
                    }
                    break;

                case Wz_TextureFormat.DXT3:
                    WzComparerR2.WzLib.Utilities.ImageCodec.DXT3ToBGRA32(raw, bgra, w, w * 4, h);
                    break;

                case Wz_TextureFormat.DXT5:
                    WzComparerR2.WzLib.Utilities.ImageCodec.DXT5ToBGRA32(raw, bgra, w, w * 4, h);
                    break;

                case Wz_TextureFormat.RGBA1010102:
                    WzComparerR2.WzLib.Utilities.ImageCodec.R10G10B10A2ToBGRA32(raw, bgra);
                    break;

                case Wz_TextureFormat.BC7:
                    {
                        // BC7ToRGBA32 输出 RGBA，再转 BGRA
                        var rgba = new byte[w * h * 4];
                        WzComparerR2.WzLib.Utilities.ImageCodec.BC7ToRGBA32(raw, w * 4, rgba, w, w * 4, h);
                        WzComparerR2.WzLib.Utilities.ImageCodec.RGBA32ToBGRA32(rgba, bgra);
                    }
                    break;

                default:
                    throw new Exception($"Unsupported format ({fmt}, scale={png.ActualScale}).");
            }

            return bgra;
        }

        /// <summary>PNG 字节 → BGRA32 像素（简易解码器，处理 Encode 输出的格式）</summary>
        public static (byte[] bgra, int width, int height) DecodePng(byte[] pngBytes)
        {
            int pos = 8; // skip signature
            int w = 0, h = 0;
            var idatChunks = new System.Collections.Generic.List<byte[]>();

            while (pos + 8 <= pngBytes.Length)
            {
                int len = (pngBytes[pos] << 24) | (pngBytes[pos + 1] << 16) | (pngBytes[pos + 2] << 8) | pngBytes[pos + 3];
                var type = System.Text.Encoding.ASCII.GetString(pngBytes, pos + 4, 4);
                pos += 8;

                if (type == "IHDR")
                {
                    w = (pngBytes[pos] << 24) | (pngBytes[pos + 1] << 16) | (pngBytes[pos + 2] << 8) | pngBytes[pos + 3];
                    h = (pngBytes[pos + 4] << 24) | (pngBytes[pos + 5] << 16) | (pngBytes[pos + 6] << 8) | pngBytes[pos + 7];
                }
                else if (type == "IDAT")
                {
                    var data = new byte[len];
                    Buffer.BlockCopy(pngBytes, pos, data, 0, len);
                    idatChunks.Add(data);
                }
                else if (type == "IEND")
                    break;

                pos += len + 4; // data + CRC
            }

            if (w <= 0 || h <= 0 || idatChunks.Count == 0)
                return (Array.Empty<byte>(), 0, 0);

            // 合并 IDAT 并解压
            var combined = new byte[idatChunks.Sum(c => c.Length)];
            int offset = 0;
            foreach (var chunk in idatChunks)
            {
                Buffer.BlockCopy(chunk, 0, combined, offset, chunk.Length);
                offset += chunk.Length;
            }

            byte[] raw;
            using (var compressedMs = new MemoryStream(combined))
            using (var zs = new ZLibStream(compressedMs, CompressionMode.Decompress))
            using (var decompressedMs = new MemoryStream())
            {
                zs.CopyTo(decompressedMs);
                raw = decompressedMs.ToArray();
            }

            // 去 filter → RGBA → BGRA
            int stride = w * 4 + 1;
            var bgra = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                int srcRow = y * stride;
                byte filter = raw[srcRow];
                int src = srcRow + 1;
                int dst = y * w * 4;
                for (int x = 0; x < w; x++)
                {
                    int si = src + x * 4, di = dst + x * 4;
                    byte r = raw[si], g = raw[si + 1], b = raw[si + 2], a = raw[si + 3];
                    if (filter == 1) // Sub
                    {
                        if (x > 0) { r += bgra[di - 4]; g += bgra[di - 3]; b += bgra[di - 2]; a += bgra[di - 1]; }
                    }
                    bgra[di] = b; bgra[di + 1] = g; bgra[di + 2] = r; bgra[di + 3] = a; // RGBA→BGRA
                }
            }

            return (bgra, w, h);
        }

        /// <summary>BGRA32 → RGBA PNG 字节（交换 R↔B）</summary>
        public static byte[] Encode(byte[] bgra, int width, int height)
        {
            using var ms = new MemoryStream();
            // PNG signature
            ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);

            // IHDR: color type 6 (RGBA), bit depth 8
            var ihdr = new byte[13];
            WriteBE(ihdr, 0, width); WriteBE(ihdr, 4, height);
            ihdr[8] = 8; ihdr[9] = 6;
            WriteChunk(ms, "IHDR", ihdr);

            // BGRA → RGBA + filter byte per row
            int stride = width * 4 + 1;
            var filtered = new byte[stride * height];
            for (int y = 0; y < height; y++)
            {
                filtered[y * stride] = 0; // filter none
                for (int x = 0; x < width; x++)
                {
                    int s = (y * width + x) * 4, d = y * stride + 1 + x * 4;
                    filtered[d]     = bgra[s + 2]; // R
                    filtered[d + 1] = bgra[s + 1]; // G
                    filtered[d + 2] = bgra[s];     // B
                    filtered[d + 3] = bgra[s + 3]; // A
                }
            }

            // Deflate
            using var z = new MemoryStream();
            using (var zs = new ZLibStream(z, CompressionLevel.Optimal, true))
                zs.Write(filtered, 0, filtered.Length);
            WriteChunk(ms, "IDAT", z.ToArray());

            WriteChunk(ms, "IEND", Array.Empty<byte>());
            return ms.ToArray();
        }

        // ---- 位展开辅助 ----
        static byte N4to8(int v) => (byte)(v | (v << 4));          // 4→8: 0xF → 0xFF
        static byte N5to8(int v) => (byte)((v << 3) | (v >> 2));   // 5→8: 0x1F → 0xFF
        static byte N6to8(int v) => (byte)((v << 2) | (v >> 4));   // 6→8: 0x3F → 0xFF

        // ---- PNG chunk / CRC ----
        static void WriteChunk(Stream ms, string t, byte[] d)
        {
            WriteBE(ms, d.Length);
            var tb = System.Text.Encoding.ASCII.GetBytes(t);
            ms.Write(tb, 0, 4); ms.Write(d, 0, d.Length);
            var c = new byte[4 + d.Length];
            Array.Copy(tb, 0, c, 0, 4); Array.Copy(d, 0, c, 4, d.Length);
            WriteBE(ms, (int)Crc32(c));
        }

        static void WriteBE(Stream ms, int v)
        {
            ms.WriteByte((byte)(v >> 24)); ms.WriteByte((byte)(v >> 16));
            ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v);
        }

        static void WriteBE(byte[] b, int o, int v)
        {
            b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16);
            b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
        }

        static readonly uint[] CT = new uint[256];
        static PngEncoder()
        {
            for (int i = 0; i < 256; i++)
            {
                uint c = (uint)i;
                for (int j = 0; j < 8; j++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                CT[i] = c;
            }
        }

        static uint Crc32(byte[] d)
        {
            uint c = 0xFFFFFFFF;
            foreach (byte b in d) c = CT[(c ^ b) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFF;
        }
    }
}
