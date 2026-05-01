using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace Unity.Robotics
{
    public static class CompressedDepthPNGDecoder
    {
        private static readonly byte[] s_pngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        public static bool TryExtractPngPayload(byte[] data, out byte[] payload, out int pngStartOffset)
        {
            payload = null;
            pngStartOffset = -1;
            if (data == null || data.Length < s_pngSignature.Length)
            {
                return false;
            }

            int pngStart = -1;
            int maxStart = data.Length - s_pngSignature.Length;
            for (int i = 0; i <= maxStart; i++)
            {
                bool signatureMatches = true;
                for (int j = 0; j < s_pngSignature.Length; j++)
                {
                    if (data[i + j] != s_pngSignature[j])
                    {
                        signatureMatches = false;
                        break;
                    }
                }

                if (signatureMatches)
                {
                    pngStart = i;
                    break;
                }
            }

            if (pngStart < 0)
            {
                return false;
            }

            pngStartOffset = pngStart;
            if (pngStart == 0)
            {
                payload = data;
                return true;
            }

            int length = data.Length - pngStart;
            payload = new byte[length];
            System.Buffer.BlockCopy(data, pngStart, payload, 0, length);
            return true;
        }

        public static bool TryDecode16Uc1PngToR16(byte[] pngData, out int width, out int height, out byte[] littleEndianDepth)
        {
            width = 0;
            height = 0;
            littleEndianDepth = null;

            if (!TryGetPngIhdr(pngData, out width, out height, out int bitDepth, out int colorType))
            {
                return false;
            }

            if (bitDepth != 16 || colorType != 0 || width <= 0 || height <= 0)
            {
                return false;
            }

            if (!TryCollectAndInflateIdat(pngData, out byte[] inflated))
            {
                return false;
            }

            int bytesPerPixel = 2;
            int stride = width * bytesPerPixel;
            int expectedInflated = height * (stride + 1);
            if (inflated.Length != expectedInflated)
            {
                return false;
            }

            littleEndianDepth = new byte[width * height * 2];
            byte[] prevRow = new byte[stride];
            byte[] curRow = new byte[stride];
            int src = 0;
            int dst = 0;

            for (int y = 0; y < height; y++)
            {
                byte filterType = inflated[src++];
                for (int x = 0; x < stride; x++)
                {
                    byte raw = inflated[src++];
                    byte left = x >= bytesPerPixel ? curRow[x - bytesPerPixel] : (byte)0;
                    byte up = prevRow[x];
                    byte upLeft = x >= bytesPerPixel ? prevRow[x - bytesPerPixel] : (byte)0;

                    byte recon;
                    switch (filterType)
                    {
                        case 0:
                            recon = raw;
                            break;
                        case 1:
                            recon = (byte)(raw + left);
                            break;
                        case 2:
                            recon = (byte)(raw + up);
                            break;
                        case 3:
                            recon = (byte)(raw + ((left + up) >> 1));
                            break;
                        case 4:
                            recon = (byte)(raw + PaethPredictor(left, up, upLeft));
                            break;
                        default:
                            return false;
                    }

                    curRow[x] = recon;
                }

                // PNG grayscale16 is big-endian; Unity R16 expects little-endian bytes.
                for (int x = 0; x < stride; x += 2)
                {
                    byte msb = curRow[x];
                    byte lsb = curRow[x + 1];
                    littleEndianDepth[dst++] = lsb;
                    littleEndianDepth[dst++] = msb;
                }

                byte[] swap = prevRow;
                prevRow = curRow;
                curRow = swap;
            }

            return true;
        }

        private static bool TryCollectAndInflateIdat(byte[] pngData, out byte[] inflated)
        {
            inflated = null;
            if (pngData == null || pngData.Length < 20)
            {
                return false;
            }

            List<byte> idatCompressed = new List<byte>(pngData.Length / 2);
            int offset = 8; // skip PNG signature
            while (offset + 12 <= pngData.Length)
            {
                int length = ReadInt32BigEndian(pngData, offset);
                if (length < 0 || offset + 12 + length > pngData.Length)
                {
                    return false;
                }

                int typeOffset = offset + 4;
                int dataOffset = offset + 8;
                uint chunkType =
                    ((uint)pngData[typeOffset] << 24) |
                    ((uint)pngData[typeOffset + 1] << 16) |
                    ((uint)pngData[typeOffset + 2] << 8) |
                    pngData[typeOffset + 3];

                // "IDAT"
                if (chunkType == 0x49444154)
                {
                    for (int i = 0; i < length; i++)
                    {
                        idatCompressed.Add(pngData[dataOffset + i]);
                    }
                }
                // "IEND"
                else if (chunkType == 0x49454E44)
                {
                    break;
                }

                offset += 12 + length;
            }

            if (idatCompressed.Count == 0)
            {
                return false;
            }

            byte[] idatBytes = idatCompressed.ToArray();
            if (idatBytes.Length <= 6)
            {
                return false;
            }

            // PNG IDAT is zlib-wrapped DEFLATE: skip 2-byte zlib header and 4-byte Adler-32 trailer.
            int rawDeflateLength = idatBytes.Length - 6;
            byte[] rawDeflate = new byte[rawDeflateLength];
            System.Buffer.BlockCopy(idatBytes, 2, rawDeflate, 0, rawDeflateLength);
            return TryInflate(rawDeflate, out inflated);
        }

        private static bool TryInflate(byte[] compressed, out byte[] inflated)
        {
            inflated = null;
            try
            {
                using MemoryStream input = new MemoryStream(compressed);
                using DeflateStream zlib = new DeflateStream(input, CompressionMode.Decompress);
                using MemoryStream output = new MemoryStream();
                zlib.CopyTo(output);
                inflated = output.ToArray();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetPngIhdr(byte[] pngData, out int width, out int height, out int bitDepth, out int colorType)
        {
            width = 0;
            height = 0;
            bitDepth = 0;
            colorType = 0;

            if (pngData == null || pngData.Length < 33)
            {
                return false;
            }

            const int ihdrTypeOffset = 12;
            if (pngData[ihdrTypeOffset] != (byte)'I' ||
                pngData[ihdrTypeOffset + 1] != (byte)'H' ||
                pngData[ihdrTypeOffset + 2] != (byte)'D' ||
                pngData[ihdrTypeOffset + 3] != (byte)'R')
            {
                return false;
            }

            const int ihdrDataOffset = 16;
            width = ReadInt32BigEndian(pngData, ihdrDataOffset);
            height = ReadInt32BigEndian(pngData, ihdrDataOffset + 4);
            bitDepth = pngData[ihdrDataOffset + 8];
            colorType = pngData[ihdrDataOffset + 9];
            // interlace method byte at +12 must be 0 for this decoder path
            return pngData[ihdrDataOffset + 12] == 0;
        }

        private static int ReadInt32BigEndian(byte[] data, int index)
        {
            return (data[index] << 24) | (data[index + 1] << 16) | (data[index + 2] << 8) | data[index + 3];
        }

        private static byte PaethPredictor(byte a, byte b, byte c)
        {
            int p = a + b - c;
            int pa = Mathf.Abs(p - a);
            int pb = Mathf.Abs(p - b);
            int pc = Mathf.Abs(p - c);
            if (pa <= pb && pa <= pc) return a;
            if (pb <= pc) return b;
            return c;
        }
    }
}
