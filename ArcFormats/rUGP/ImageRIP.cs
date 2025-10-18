//! \file       ImageRIP.cs
//! \date       Mon Nov 07 17:01:32 2016
//! \brief      rUGP image format.
//
// Copyright (C) 2016 by morkt
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to
// deal in the Software without restriction, including without limitation the
// rights to use, copy, modify, merge, publish, distribute, sublicense, and/or
// sell copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
// IN THE SOFTWARE.
//

using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Windows.Media;
using GameRes.Utility;

namespace GameRes.Formats.Rugp
{
    internal class RioMetaData : ImageMetaData
    {
        public uint ObjectOffset;
        public CRip Rip;
    }

    [Export(typeof(ImageFormat))]
    public class RipFormat : ImageFormat
    {
        public override string         Tag { get { return "RIP"; } }
        public override string Description { get { return "rUGP compressed image format"; } }
        public override uint     Signature { get { return 0; } }

        public RipFormat ()
        {
            Extensions = new string[] { "rip", "sia" };
        }

        // signature set to 0 because all serialized rUGP objects have same signature.

        public override ImageMetaData ReadMetaData (IBinaryStream file)
        {
            if (file.Signature != CRioArchive.ObjectSignature)
                return null;
            var rio = new CRioArchive (file);
            uint signature;
            var class_ref = rio.LoadRioTypeCore (out signature);
            CRip img;
            if ("CRip007" == class_ref)
                img = new CRip007();
            else if ("CRip" == class_ref)
                img = new CRip();
            else
                return null;
            return img.ReadMetaData (rio);
        }

        public override ImageData Read (IBinaryStream file, ImageMetaData info)
        {
            var meta = (RioMetaData)info;
            file.Position = meta.ObjectOffset;
            var arc = new CRioArchive (file);
            var img = meta.Rip;
            img.Deserialize (arc);
            return ImageData.Create (info, img.Format, null, img.Pixels);
        }

        public override void Write (Stream file, ImageData image)
        {
            throw new System.NotImplementedException ("RipFormat.Write not implemented");
        }
    }

    internal class CRip : CObject
    {
        protected int   Version;
        protected int   m_width;
        protected int   m_height;
        protected int   m_x;
        protected int   m_y;
        protected int   m_w;
        protected int   m_h;
        protected int   m_flags;   // field_30
        protected byte[] m_pixels;

        public PixelFormat Format { get; protected set; }
        public byte[]      Pixels { get { return m_pixels; } }

        public override void Deserialize (CRioArchive arc)
        {
            Version = arc.ReadInt32();
            m_x = arc.ReadUInt16();
            m_y = arc.ReadUInt16();
            m_width = arc.ReadUInt16();
            m_height = arc.ReadUInt16();
            m_w = arc.ReadUInt16();
            m_h = arc.ReadUInt16();
            m_flags = arc.ReadInt32();
            int size = arc.ReadInt32();
            arc.ReadInt32(); // field_34
            var data = arc.ReadBytes (size);
            m_pixels = Uncompress (data);
        }

        public virtual ImageMetaData ReadMetaData (CRioArchive arc)
        {
            uint object_pos = (uint)arc.Input.Position;
            arc.ReadInt32();
            int x = arc.ReadUInt16();
            int y = arc.ReadUInt16();
            uint w1 = arc.ReadUInt16();
            uint h1 = arc.ReadUInt16();
            uint w2 = arc.ReadUInt16();
            uint h2 = arc.ReadUInt16();
            int flags = arc.ReadInt32() & 0xFF;
            if (flags < 1 || flags > 3)
                return null;
            uint width, height;
            if (3 == flags)
            {
                width = w2;
                height = h2;
            }
            else
            {
                width = w1;
                height = h1;
            }
            return new RioMetaData
            {
                OffsetX = x,
                OffsetY = y,
                Width = width,
                Height = height,
                BPP = 1 == flags ? 8 : 32,
                ObjectOffset = object_pos,
                Rip = this,
            };
        }

        byte[] Uncompress (byte[] data)
        {
            int flags = m_flags & 0xFF;
            if (1 == flags)
                return UncompressSia (data);
            byte[] pixels = null;
            if (2 == flags)
            {
                using (var mem = new MemoryStream (data))
                using (var input = new MsbBitStream (mem))
                {
                    Format = PixelFormats.Bgr32;
                    pixels = new byte[4 * m_width * m_height];
                    switch ((m_flags >> 16) & 0xFF)
                    {
                    case 1: UncompressRgb1 (input, pixels); break;
                    case 2: UncompressRgb2 (input, pixels); break;
                    case 3: UncompressRgb3 (input, pixels); break;
                    }
                }
            }
            else if (3 == flags)
            {
                if (2 == ((m_flags >> 16) & 0xFF))
                {
                    Format = PixelFormats.Bgra32;
                    pixels = new byte[4 * m_w * m_h];
                    UncompressRgba (data, pixels);
                }
            }
            if (null == pixels)
                throw new InvalidFormatException();
            return pixels;
        }

        byte[] UncompressSia (byte[] input)
        {
            var output = new byte[m_width * m_height];
            Format = PixelFormats.Gray8;
            int src = 0;
            int stride = m_width;
            for (int y = 0; y < m_height; ++y)
            {
                byte color = 0;
                int width = m_width;
                int dst = y * stride;
                while (width > 0)
                {
                    int count = input[src++];
                    if (count > 0)
                    {
                        width -= count;
                        for (int i = 0; i < count; ++i)
                            output[dst++] = color;
                    }
                    if (width > 0 && src < input.Length)
                    {
                        color = input[src++];
                    }
                }
            }
            return output;
        }

        void UncompressRgb1 (IBitStream input, byte[] output)
        {
            int stride = m_width * 4;
            int rgb = 0;
            for (int dst_row = output.Length - stride; dst_row >= 0; dst_row -= stride)
            {
                int dst = dst_row;
                for (int x = 0; x < m_width; ++x)
                {
                    if (input.GetNextBit() != 0)
                    {
                        int b = ReadLong (input, rgb);
                        int g = ReadLong (input, rgb >> 8);
                        int r = ReadLong (input, rgb >> 16);
                        rgb = r << 16 | g << 8 | b;
                    }
                    LittleEndian.Pack (rgb, output, dst);
                    dst += 4;
                }
            }
        }

        void UncompressRgb2 (IBitStream input, byte[] output)
        {
            throw new NotImplementedException ("CRip.UncompressRgb2 not implemented.");
        }

        void UncompressRgb3 (IBitStream input, byte[] output)
        {
            int stride = m_width * 4;
            int rgb = 0;
            for (int dst_row = output.Length - stride; dst_row >= 0; dst_row -= stride)
            {
                int dst = dst_row;
                for (int x = 0; x < m_width; ++x)
                {
                    if (input.GetNextBit() != 0)
                    {
                        int b = ReadLong  (input, rgb) + 3;
                        int g = ReadShort (input, rgb >> 8) + 1;
                        int r = ReadLong  (input, rgb >> 16) + 3;
                        rgb = r << 16 | g << 8 | b;
                    }
                    LittleEndian.Pack (rgb, output, dst);
                    dst += 4;
                }
            }
        }

        void UncompressRgba (byte[] input, byte[] output)
        {
            int src = input.ToInt32 (0);
            using (var mem = new MemoryStream (input, 4, src - 4))
            using (var bits = new MsbBitStream (mem))
            {
                int dst = 0;
                for (int y = 0; y < m_h; ++y)
                {
                    int rgb = 0;
                    int alpha = 0;
                    int x = 0;
                    while (x < m_w)
                    {
                        int len = input[src++];
                        if (alpha != 0)
                        {
                            for (int i = 0; i < len; ++i)
                            {
                                if (bits.GetNextBit() != 0)
                                {
                                    int b = ReadABits (bits, rgb)       + 3;
                                    int g = ReadABits (bits, rgb >> 8)  + 3;
                                    int r = ReadABits (bits, rgb >> 16) + 3;
                                    rgb = r << 16 | g << 8 | b;
                                }
                                LittleEndian.Pack (rgb | alpha << 24, output, dst);
                                dst += 4;
                            }
                        }
                        else
                        {
                            dst += 4 * len;
                        }
                        x += len;
                        if (x >= m_w)
                            break;
                        if (bits.GetNextBit() != 0)
                            alpha = (bits.GetBits (7) << 1) + 1;
                        else if (bits.GetNextBit() != 0)
                            alpha = 0xFF;
                        else
                            alpha = 0;
                    }
                }
            }
        }

        int ReadLong (IBitStream input, int prev)
        {
            prev &= 0xFC;
            if ((prev >> 2) == 0)
            {
                if (input.GetNextBit() == 0)
                    return prev;
                else if (input.GetNextBit() == 0)
                    return input.GetBits (6) << 2;
                else if (input.GetNextBit() == 0)
                    return 4;
                else if (input.GetNextBit() == 0)
                    return 8;
                else
                    return 12;
            }
            else if ((prev >> 2) == 1)
            {
                if (input.GetNextBit() == 0)
                    return input.GetNextBit() << 2;
                else if (input.GetNextBit() == 0)
                    return input.GetBits (6) << 2;
                else if (input.GetNextBit() == 0)
                    return 8;
                else if (input.GetNextBit() == 0)
                    return 12;
                else
                    return 16;
            }
            else if ((prev >> 2) == 0x3F)
            {
                if (input.GetNextBit() == 0)
                    return 0xFC;
                else if (input.GetNextBit() == 0)
                    return input.GetBits (6) << 2;
                else if (input.GetNextBit() != 0)
                    return 0xF4 + (-input.GetNextBit() & 0xFC);
                else
                    return 0xF8;
            }
            else
            {
                if (input.GetNextBit() == 0)
                {
                    if (input.GetNextBit() == 0)
                        return prev;
                    return input.GetBits (6) << 2;
                }
                if (input.GetNextBit() == 0)
                {
                    if (input.GetNextBit() != 0)
                        return prev - 4;
                    else
                        return prev + 4;
                }
                if (input.GetNextBit() == 0)
                {
                    if (input.GetNextBit() != 0)
                        return prev - 8;
                    else
                        return prev + 8;
                }
                switch (input.GetBits (2))
                {
                case 0:  return Math.Min (prev + 16, 0xFC);
                case 1:  return Math.Max (prev - 16, 0);
                case 2:  return Math.Min (prev + 24, 0xFC);
                default: return Math.Max (prev - 24, 0);
                }
            }
        }

        int ReadShort (IBitStream input, int prev)
        {
            prev &= 0xFE;
            if (input.GetNextBit() == 0)
            {
                if (input.GetNextBit() == 0)
                    return prev;
                else
                    return input.GetBits (6) << 2;
            }
            else if (input.GetNextBit() == 0)
            {
                if (input.GetNextBit() == 0)
                    return prev + 2;
                else
                    return prev - 2;
            }
            else if (input.GetNextBit() == 0)
            {
                if (input.GetNextBit() == 0)
                    return prev + 4;
                else
                    return prev - 4;
            }
            else
            {
                switch (input.GetBits (2))
                {
                case 0:  return Math.Min (prev + 8, 0xFE);
                case 1:  return Math.Max (prev - 8, 0);
                case 2:  return Math.Min (prev + 12, 0xFE);
                default: return Math.Max (prev - 12, 0);
                }
            }
        }

        int ReadABits (IBitStream input, int prev)
        {
            prev &= 0xFC;
            if (input.GetNextBit() == 0)
            {
                if (input.GetNextBit() == 0)
                    return prev;
                else
                    return input.GetBits (6) << 2;
            }
            else if (input.GetNextBit() == 0)
            {
                if (input.GetNextBit() == 0)
                    return prev + 4;
                else
                    return prev - 4;
            }
            else if (input.GetNextBit() == 0)
            {
                if (input.GetNextBit() == 0)
                    return prev + 8;
                else
                    return prev - 8;
            }
            else
            {
                switch (input.GetBits (2))
                {
                case 0:  return Math.Min (prev + 16, 0xFC);
                case 1:  return Math.Max (prev - 16, 0);
                case 2:  return Math.Min (prev + 24, 0xFC);
                default: return Math.Max (prev - 24, 0);
                }
            }
        }
    }

    internal class CRip007 : CRip
    {
        byte[]   CompressInfo;
        CObject  field_4C;

        public bool      HasAlpha { get { return ((m_flags & 0xFF) - 2) == 1; } }

        public override void Deserialize (CRioArchive arc)
        {
            Version = arc.ReadInt32();
            m_width = arc.ReadUInt16();
            m_height = arc.ReadUInt16();
            m_x = arc.ReadUInt16();
            m_y = arc.ReadUInt16();
            m_w = arc.ReadUInt16();
            m_h = arc.ReadUInt16();
            m_flags = arc.ReadInt32();
            CompressInfo = arc.ReadBytes (7);
            if (arc.GetObjectSchema() >= 2)
                field_4C = arc.ReadRioReference ("CSbm");
            int size = arc.ReadInt32();
            arc.ReadInt32(); // field_3C
            var data = arc.ReadBytes (size);
            m_pixels = Uncompress (data);
            Format = HasAlpha ? PixelFormats.Bgra32 : PixelFormats.Bgr32;
        }

        public override ImageMetaData ReadMetaData (CRioArchive arc)
        {
            uint object_pos = (uint)arc.Input.Position;
            arc.ReadInt32();
            uint w = arc.ReadUInt16();
            uint h = arc.ReadUInt16();
            return new RioMetaData
            {
                Width = w,
                Height = h,
                BPP = 32,
                ObjectOffset = object_pos,
                Rip = this,
            };
        }

        byte[] Uncompress (byte[] data)
        {
            using (var input = new MemoryStream (data))
            using (var bits = new MsbBitStream (input))
            {
                var pixels = new byte[4 * m_width * m_height];
                if (HasAlpha)
                    UncompressRgba (bits, pixels);
                else
                    UncompressRgb (bits, pixels);
                return pixels;
            }
        }

        void UncompressRgb (IBitStream input, byte[] output)
        {
            int stride = m_width * 4;
            int q = CompressInfo[0];
            int b_bits = CompressInfo[4];
            int g_bits = CompressInfo[5];
            int r_bits = CompressInfo[6];
            bool is_bgr676 = 6 == b_bits && 7 == g_bits && 6 == r_bits;
            int b_shift = 8 - b_bits;
            int g_shift = 16 - g_bits;
            int r_shift = 24 - r_bits;
            int baseline = 0xFF >> b_bits | (0xFF >> g_bits | (0xFF >> r_bits | 0xFF00) << 8) << 8;
            {
                for (int y = 0; y < m_height; ++y)
                {
                    int rgb = 0, rgb_ = 0;
                    int dst = y * stride;
                    int x = 0;
                    while (x < m_width)
                    {
                        int count = GetInt (input);
                        x += count;
                        do
                        {
                            if (input.GetNextBit() > 0)
                            {
                                rgb = LittleEndian.ToInt32 (output, dst - stride);
                                if (rgb != 0)
                                {
                                    rgb -= baseline;
                                }
                                rgb_ = rgb;
                            }
                            else
                            {
                                int r = 0, g = 0, b = 0;
                                if (input.GetNextBit() > 0)
                                {
                                    g = GetSigned (input);
                                }
                                int b_inc = 0;
                                if (input.GetNextBit() > 0)
                                {
                                    bool sign = input.GetNextBit() > 0;
                                    int v = GetInt (input);
                                    b_inc = tblQuantTransfer[q, v];
                                    if (sign)
                                        b_inc = -b_inc;
                                }
                                int r_inc = 0;
                                if (input.GetNextBit() > 0)
                                {
                                    bool sign = input.GetNextBit() > 0;
                                    int v = GetInt (input);
                                    r_inc = tblQuantTransfer[q, v];
                                    if (sign)
                                        r_inc = -r_inc;
                                }
                                int gg = g;
                                if (is_bgr676)
                                    gg >>= 1;
                                if (CompressInfo[3] != 0)
                                {
                                    int c1 = (0xFF >> b_shift) & (int)((uint)rgb_ >> b_shift);
                                    c1 = -c1;
                                    if (gg >= c1)
                                    {
                                        int c2 = (0xFF >> b_shift) + c1;
                                        c1 = c2;
                                        if (gg <= c2)
                                            c1 = gg;
                                    }
                                    b = c1 + b_inc;
                                    c1 = (0xFF0000 >> r_shift) & (int)((uint)rgb_ >> r_shift);
                                    c1 = -c1;
                                    if (gg >= c1)
                                    {
                                        int c2 = (0xFF0000 >> r_shift) + c1;
                                        c1 = c2;
                                        if (gg <= c2)
                                            c1 = gg;
                                    }
                                    r = c1 + r_inc;
                                }
                                else
                                {
                                    b = gg + b_inc;
                                    r = gg + r_inc;
                                }
                                rgb_ += (b << b_shift) + (r << r_shift) + (g << g_shift);
                                rgb = rgb_;
                            }
                            if (rgb != 0)
                                rgb += baseline;
                            LittleEndian.Pack (rgb, output, dst);
                            dst += 4;
                            --count;
                        }
                        while (count > 0);
                        if (x >= m_width)
                            break;

                        count = GetInt (input);
                        x += count;
                        while (count --> 0)
                        {
                            LittleEndian.Pack (rgb, output, dst);
                            dst += 4;
                        }
                    }
                }
            }
        }

        void UncompressRgba (IBitStream input, byte[] output)
        {
            int stride = 4 * m_width;
            int q = CompressInfo[0];
            int b_bits = CompressInfo[4];
            int g_bits = CompressInfo[5];
            int r_bits = CompressInfo[6];
            int b_shift = 8 - b_bits;
            int g_shift = 16 - g_bits;
            int r_shift = 24 - r_bits;
            int dst_pos = m_y * stride + m_x * 4;
            int baseline = 0xFF >> b_bits | (0xFF >> g_bits | (0xFF >> r_bits << 8)) << 8;
            var line_buf = new int[m_w];
            for (int y = 0; y < m_h; ++y)
            {
                int alpha = 0;
                int rgb = 0;
                int repeat_count = 0;
                bool repeat = true;
                int dst = dst_pos + y * stride;
                int x = 0;
                int chunk_size = 0;
                while (x < m_w)
                {
                    if (0 == chunk_size)
                    {
                        int alpha_inc = 0;
                        if (input.GetNextBit() > 0)
                        {
                            alpha_inc = GetSigned (input);
                        }
                        alpha += alpha_inc;
                        if (0 == alpha || 31 == alpha)
                        {
                            chunk_size = GetInt (input);
                        }
                    }
                    if (alpha != 0)
                    {
                        if (31 == alpha)
                            --chunk_size;
                        if (0 == repeat_count)
                        {
                            repeat_count = GetInt (input);
                            repeat = !repeat;
                        }
                        --repeat_count;
                        if (!repeat)
                        {
                            if (input.GetNextBit() > 0)
                            {
                                rgb = line_buf[x];
                            }
                            else
                            {
                                int g = 0;
                                if (input.GetNextBit() > 0)
                                {
                                    g = GetSigned (input);
                                }
                                int b_inc = 0;
                                if (input.GetNextBit() > 0)
                                {
                                    bool sign = input.GetNextBit() > 0;
                                    int v = GetInt (input);
                                    b_inc = tblQuantTransfer[q, v];
                                    if (sign)
                                        b_inc = -b_inc;
                                }
                                int r_inc = 0;
                                if (input.GetNextBit() > 0)
                                {
                                    bool sign = input.GetNextBit() > 0;
                                    int v = GetInt (input);
                                    r_inc = tblQuantTransfer[q, v];
                                    if (sign)
                                        r_inc = -r_inc;
                                }
                                int c1 = (0xFF >> b_shift) & (int)((uint)rgb >> b_shift);
                                c1 = -c1;
                                if (g >= c1)
                                {
                                    int c2 = (0xFF >> b_shift) + c1;
                                    c1 = g;
                                    if (g > c2)
                                        c1 = c2;
                                }
                                int b = c1 + b_inc;
                                c1 = (0xFF0000 >> r_shift) & (int)((uint)rgb >> r_shift);
                                c1 = -c1;
                                if (g >= c1)
                                {
                                    int c2 = (0xFF0000 >> r_shift) + c1;
                                    c1 = g;
                                    if (g > c2)
                                        c1 = c2;
                                }
                                int r = c1 + r_inc;
                                rgb += (b << b_shift) + (r << r_shift) + (g << g_shift);
                            }
                        }
                        uint pixel = (uint)(baseline + rgb);
                        if (31 == alpha)
                            pixel |= 0xFF000000u;
                        else
                            pixel |= (uint)(alpha << 27);
                        LittleEndian.Pack (pixel, output, dst);
                        dst += 4;
                        line_buf[x++] = rgb;
                    }
                    else
                    {
                        dst += 4 * chunk_size;
                        x += chunk_size;
                        chunk_size = 0;
                    }
                }
            }
        }

        static int GetInt (IBitStream input)
        {
            int n = 1;
            while (input.GetNextBit() > 0)
            {
                n <<= 1;
                n |= input.GetNextBit();
            }
            return n;
        }

        static int GetSigned (IBitStream input)
        {
            bool sign = input.GetNextBit() > 0;
            int n = GetInt (input);
            return sign ? -n : n;
        }

        static readonly byte[,] tblQuantTransfer = {
            {
            0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
            0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F,
            0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x2B, 0x2C, 0x2D, 0x2E, 0x2F,
            0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F,
            0x40, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A, 0x4B, 0x4C, 0x4D, 0x4E, 0x4F,
            0x50, 0x51, 0x52, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x5B, 0x5C, 0x5D, 0x5E, 0x5F,
            0x60, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x6B, 0x6C, 0x6D, 0x6E, 0x6F,
            0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x7B, 0x7C, 0x7D, 0x7E, 0x7F,
            0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
            0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F,
            0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x2B, 0x2C, 0x2D, 0x2E, 0x2F,
            0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F,
            0x40, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A, 0x4B, 0x4C, 0x4D, 0x4E, 0x4F,
            0x50, 0x51, 0x52, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x5B, 0x5C, 0x5D, 0x5E, 0x5F,
            0x60, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x6B, 0x6C, 0x6D, 0x6E, 0x6F,
            0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x7B, 0x7C, 0x7D, 0x7E, 0x7F,
            }, {
            0x00, 0x01, 0x02, 0x04, 0x06, 0x09, 0x0C, 0x0F, 0x13, 0x16, 0x19, 0x1C, 0x1F, 0x23, 0x27, 0x2B,
            0x30, 0x34, 0x38, 0x3C, 0x40, 0x44, 0x48, 0x4C, 0x50, 0x54, 0x58, 0x5C, 0x60, 0x64, 0x68, 0x6C,
            0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x7B, 0x7C, 0x7D, 0x7E, 0x7F,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x01, 0x02, 0x02, 0x03, 0x03, 0x04, 0x04, 0x04, 0x05, 0x05, 0x05, 0x06, 0x06, 0x06, 0x07,
            0x07, 0x07, 0x07, 0x08, 0x08, 0x08, 0x09, 0x09, 0x09, 0x0A, 0x0A, 0x0A, 0x0B, 0x0B, 0x0B, 0x0C,
            0x0C, 0x0C, 0x0C, 0x0D, 0x0D, 0x0D, 0x0D, 0x0E, 0x0E, 0x0E, 0x0E, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F,
            0x10, 0x10, 0x10, 0x10, 0x11, 0x11, 0x11, 0x11, 0x12, 0x12, 0x12, 0x12, 0x13, 0x13, 0x13, 0x13,
            0x14, 0x14, 0x14, 0x14, 0x15, 0x15, 0x15, 0x15, 0x16, 0x16, 0x16, 0x16, 0x17, 0x17, 0x17, 0x17,
            0x18, 0x18, 0x18, 0x18, 0x19, 0x19, 0x19, 0x19, 0x1A, 0x1A, 0x1A, 0x1A, 0x1B, 0x1B, 0x1B, 0x1B,
            0x1C, 0x1C, 0x1C, 0x1C, 0x1D, 0x1D, 0x1D, 0x1D, 0x1E, 0x1E, 0x1E, 0x1E, 0x1F, 0x1F, 0x1F, 0x1F,
            0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x2B, 0x2C, 0x2D, 0x2E, 0x2F,
            }, {
            0x00, 0x01, 0x02, 0x04, 0x08, 0x0C, 0x10, 0x14, 0x18, 0x1B, 0x1E, 0x22, 0x26, 0x2A, 0x2E, 0x32,
            0x36, 0x3A, 0x3E, 0x42, 0x46, 0x4B, 0x50, 0x55, 0x5A, 0x5F, 0x64, 0x69, 0x6E, 0x73, 0x78, 0x7D,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x01, 0x02, 0x02, 0x03, 0x03, 0x03, 0x03, 0x04, 0x04, 0x04, 0x04, 0x05, 0x05, 0x05, 0x05,
            0x06, 0x06, 0x06, 0x06, 0x07, 0x07, 0x07, 0x07, 0x08, 0x08, 0x08, 0x09, 0x09, 0x09, 0x0A, 0x0A,
            0x0A, 0x0A, 0x0B, 0x0B, 0x0B, 0x0B, 0x0C, 0x0C, 0x0C, 0x0C, 0x0D, 0x0D, 0x0D, 0x0D, 0x0E, 0x0E,
            0x0E, 0x0E, 0x0F, 0x0F, 0x0F, 0x0F, 0x10, 0x10, 0x10, 0x10, 0x11, 0x11, 0x11, 0x11, 0x12, 0x12,
            0x12, 0x12, 0x13, 0x13, 0x13, 0x13, 0x14, 0x14, 0x14, 0x14, 0x14, 0x15, 0x15, 0x15, 0x15, 0x15,
            0x16, 0x16, 0x16, 0x16, 0x16, 0x17, 0x17, 0x17, 0x17, 0x17, 0x18, 0x18, 0x18, 0x18, 0x18, 0x19,
            0x19, 0x19, 0x19, 0x19, 0x1A, 0x1A, 0x1A, 0x1A, 0x1A, 0x1B, 0x1B, 0x1B, 0x1B, 0x1B, 0x1C, 0x1C,
            0x1C, 0x1C, 0x1C, 0x1D, 0x1D, 0x1D, 0x1D, 0x1D, 0x1E, 0x1E, 0x1E, 0x1E, 0x1E, 0x1F, 0x1F, 0x1F,
            }, {
            0x00, 0x01, 0x03, 0x07, 0x0C, 0x10, 0x15, 0x1A, 0x20, 0x25, 0x2A, 0x30, 0x36, 0x3C, 0x42, 0x48,
            0x50, 0x54, 0x58, 0x5C, 0x60, 0x64, 0x68, 0x6C, 0x70, 0x74, 0x78, 0x7B, 0x7C, 0x7D, 0x7E, 0x7F,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x01, 0x01, 0x02, 0x02, 0x02, 0x02, 0x03, 0x03, 0x03, 0x03, 0x03, 0x04, 0x04, 0x04, 0x04,
            0x05, 0x05, 0x05, 0x05, 0x05, 0x06, 0x06, 0x06, 0x06, 0x06, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07,
            0x08, 0x08, 0x08, 0x08, 0x08, 0x09, 0x09, 0x09, 0x09, 0x09, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A,
            0x0B, 0x0B, 0x0B, 0x0B, 0x0B, 0x0B, 0x0C, 0x0C, 0x0C, 0x0C, 0x0C, 0x0C, 0x0D, 0x0D, 0x0D, 0x0D,
            0x0D, 0x0D, 0x0E, 0x0E, 0x0E, 0x0E, 0x0E, 0x0E, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F,
            0x10, 0x10, 0x10, 0x10, 0x11, 0x11, 0x11, 0x11, 0x12, 0x12, 0x12, 0x12, 0x13, 0x13, 0x13, 0x13,
            0x14, 0x14, 0x14, 0x14, 0x15, 0x15, 0x15, 0x15, 0x16, 0x16, 0x16, 0x16, 0x17, 0x17, 0x17, 0x17,
            0x18, 0x18, 0x18, 0x18, 0x19, 0x19, 0x19, 0x19, 0x1A, 0x1A, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F,
            }, {
            0x00, 0x01, 0x03, 0x07, 0x0D, 0x13, 0x1A, 0x21, 0x28, 0x2F, 0x36, 0x3E, 0x46, 0x4E, 0x56, 0x5E,
            0x68, 0x6A, 0x6C, 0x6E, 0x70, 0x72, 0x74, 0x76, 0x78, 0x79, 0x7A, 0x7B, 0x7C, 0x7D, 0x7E, 0x7F,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x01, 0x01, 0x02, 0x02, 0x02, 0x02, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x04, 0x04, 0x04,
            0x04, 0x04, 0x04, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06,
            0x06, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x09,
            0x09, 0x09, 0x09, 0x09, 0x09, 0x09, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0B, 0x0B,
            0x0B, 0x0B, 0x0B, 0x0B, 0x0B, 0x0B, 0x0C, 0x0C, 0x0C, 0x0C, 0x0C, 0x0C, 0x0C, 0x0C, 0x0D, 0x0D,
            0x0D, 0x0D, 0x0D, 0x0D, 0x0D, 0x0D, 0x0E, 0x0E, 0x0E, 0x0E, 0x0E, 0x0E, 0x0E, 0x0E, 0x0F, 0x0F,
            0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x10, 0x10, 0x11, 0x11, 0x12, 0x12, 0x13, 0x13,
            0x14, 0x14, 0x15, 0x15, 0x16, 0x16, 0x17, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F,
            }, {
            0x00, 0x01, 0x04, 0x0A, 0x11, 0x18, 0x20, 0x28, 0x32, 0x3C, 0x46, 0x50, 0x5A, 0x64, 0x6E, 0x78,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x01, 0x01, 0x01, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03,
            0x03, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05,
            0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07,
            0x07, 0x07, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x09, 0x09, 0x09, 0x09,
            0x09, 0x09, 0x09, 0x09, 0x09, 0x09, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A,
            0x0B, 0x0B, 0x0B, 0x0B, 0x0B, 0x0B, 0x0B, 0x0B, 0x0B, 0x0B, 0x0C, 0x0C, 0x0C, 0x0C, 0x0C, 0x0C,
            0x0C, 0x0C, 0x0C, 0x0C, 0x0D, 0x0D, 0x0D, 0x0D, 0x0D, 0x0D, 0x0D, 0x0D, 0x0D, 0x0D, 0x0E, 0x0E,
            0x0E, 0x0E, 0x0E, 0x0E, 0x0E, 0x0E, 0x0E, 0x0E, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F,
            }
        };
    }
}
