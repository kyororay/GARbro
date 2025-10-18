//! \file       ArcAN21.cs
//! \date       Sun Apr 30 21:04:25 2017
//! \brief      KaGuYa script engine animation resource.
//
// Copyright (C) 2017 by morkt
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
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using GARbro.GUI;

namespace GameRes.Formats.Kaguya
{
    internal class An21Entry : PackedEntry
    {
        public int FrameIndex;
        public int RleStep;
    }

    internal static class An21Frame
    {
        internal static string Name;
        internal static byte[][] Pixels;
    }

    [Export(typeof(ArchiveFormat))]
    public class An21Opener : ArchiveFormat
    {
        public override string Tag { get { return "AN21/KAGUYA"; } }
        public override string Description { get { return "KaGuYa script engine animation resource"; } }
        public override uint Signature { get { return 0x31324E41; } } // 'AN21'
        public override bool IsHierarchic { get { return false; } }
        public override bool CanWrite { get { return false; } }

        public An21Opener()
        {
            Extensions = new string[] { "anm" };
        }

        public override ArcFile TryOpen(ArcView file)
        {
            int table_count = file.View.ReadUInt16(4);
            uint current_offset = 8;
            for (int i = 0; i < table_count; ++i)
            {
                switch (file.View.ReadByte(current_offset++))

                {
                    case 0: break;
                    case 1: current_offset += 8; break;
                    case 2:
                    case 3:
                    case 4:
                    case 5: current_offset += 4; break;
                    default: return null;
                }
            }
            current_offset += 2 + file.View.ReadUInt16(current_offset) * 8u;
            if (!file.View.AsciiEqual(current_offset, "[PIC]10"))
                return null;
            current_offset += 7;
            int frame_count = file.View.ReadInt16(current_offset);
            if (!IsSaneCount(frame_count))
                return null;

            if (VFS.CurrentArchive == null && !KaguyaParams.m_flag) //アーカイブ内ではない AND 未取得
            {
                using (var input = LinkOpener.OpenParamDat(VFS.ChangeFileName(file.Name, "params.dat")))
                {
                    Version param_version;
                    if (input != null)
                    {
                        param_version = LinkOpener.GetParamVersion(input);
                        if (param_version != null)
                            LinkOpener.GetKaguyaParams(input, param_version);
                    }
                }
            }

            current_offset += 2;
            string base_name = Path.GetFileNameWithoutExtension(file.Name);
            var dir = new List<Entry>(frame_count);
            var info = new ImageMetaData
            {
                OffsetX = file.View.ReadInt32(current_offset),
                OffsetY = file.View.ReadInt32(current_offset + 4),
                Width = file.View.ReadUInt32(current_offset + 8),
                Height = file.View.ReadUInt32(current_offset + 12),
            };
            int channels = file.View.ReadInt32(current_offset + 0x20);
            info.BPP = channels * 8;
            current_offset += 0x24;
            var entry = new An21Entry
            {
                FrameIndex = 0,
                Name = string.Format("{0}#{1:D2}", base_name, 0),
                Type = "image",
                Offset = current_offset,
                Size = (uint)channels * info.Width * info.Height,
                IsPacked = false,
            };
            dir.Add(entry);
            current_offset += entry.Size;
            for (int i = 1; i < frame_count; ++i)
            {
                int step = file.View.ReadByte(current_offset++);
                if (0 == step)
                    return null;
                uint packed_size = file.View.ReadUInt32(current_offset);
                uint unpacked_size = (uint)channels * info.Width * info.Height;
                current_offset += 4;
                entry = new An21Entry
                {
                    FrameIndex = i,
                    Name = string.Format("{0}#{1:D2}", base_name, i),
                    Type = "image",
                    Offset = current_offset,
                    Size = packed_size,
                    UnpackedSize = unpacked_size,
                    IsPacked = true,
                    RleStep = step,
                };
                dir.Add(entry);
                current_offset += packed_size;
            }
            return new AnmArchive(file, this, dir, info);
        }

        public override Stream OpenEntry(ArcFile arc, Entry entry)
        {
            var anent = entry as An21Entry;
            var input = arc.File.CreateStream(entry.Offset, entry.Size, entry.Name);
            if (null == anent || !anent.IsPacked)
                return input;
            using (input)
            {
                var data = DecompressRLE(input, anent.UnpackedSize, anent.RleStep);
                return new BinMemoryStream(data);
            }
        }

        public override IImageDecoder OpenImage(ArcFile arc, Entry entry)
        {
            var anarc = (AnmArchive)arc;
            var anent = (An21Entry)entry;
            var info = anarc.ImageInfo;
            byte[] pixels;
            IImageDecoder decoder;

            info = Ap2Format.ParamsAdjust(info);

            int byte_depth = info.BPP / 8; //ビット深度（バイト換算）
            int stride = info.iBaseWidth * byte_depth; //1行当たりのバイト数
            var format = BitmapDecoder.GetFormat(info.BPP);

            if (info.Width == info.BaseWidth && info.Height == info.BaseHeight) //ベース画像とサイズが同じ（=リサイズ不要）
            {
                decoder = new BitmapSourceDecoder(ImageData.CreateFlipped(
                        new ImageMetaData
                        {
                            OffsetX = info.OffsetX,
                            OffsetY = info.OffsetY,
                            Width = info.BaseWidth,
                            Height = info.BaseHeight,
                            BPP = info.BPP
                        },
                        format,
                        null,
                        GetPixels(arc, anent),
                        stride
                        ).Bitmap);
            }
            else
            {
                int offset = info.OffsetY * stride + info.OffsetX * byte_depth;
                pixels = new byte[stride * info.BaseHeight];
                var source = ImageData.CreateFlipped(
                    anarc.ImageInfo,
                    format,
                    null,
                    GetPixels(arc, anent),
                    byte_depth * info.iWidth
                    ).Bitmap;

                source.CopyPixels(Int32Rect.Empty, pixels, stride, offset);

                decoder = new BitmapSourceDecoder(BitmapImage.Create(
                    info.iBaseWidth,
                    info.iBaseHeight,
                    ImageData.DefaultDpiX,
                    ImageData.DefaultDpiY,
                    format,
                    null,
                    pixels,
                    stride
                    ));
            }

            return decoder;
        }

        //RAM使用量：小、処理速度：遅
        private byte[] GetPixels(ArcFile arc, An21Entry entry)
        {
            byte[] pixels;
            using (var stream = arc.OpenEntry(entry))
            {
                pixels = new byte[stream.Length];
                stream.Read(pixels, 0, pixels.Length);
            }
            for (int i = 0; i < entry.FrameIndex; ++i)
            {
                byte[] prev_pixels;
                using (var stream = arc.OpenEntry(arc.Dir.ElementAt(i)))
                {
                    prev_pixels = new byte[stream.Length];
                    stream.Read(prev_pixels, 0, prev_pixels.Length);
                }
                for (int j = 0; j < pixels.Length; ++j)
                    pixels[j] += prev_pixels[j];
            }

            return pixels;
        }

        //RAM使用量：大、処理速度：速?
        private byte[] GetPixels(ArcFile arc, int index)
        {
            if (An21Frame.Name != arc.File.Name)
                An21Frame.Pixels = new byte[arc.Dir.Count][];
            if (null != An21Frame.Pixels[index])
                return An21Frame.Pixels[index];

            var entry = arc.Dir.ElementAt(index);
            byte[] pixels;
            using (var stream = arc.OpenEntry(entry))
            {
                pixels = new byte[stream.Length];
                stream.Read(pixels, 0, pixels.Length);
            }
            if (index > 0)
            {
                var prev_frame = GetPixels(arc, index - 1);
                for (int i = 0; i < pixels.Length; ++i)
                    pixels[i] += prev_frame[i];
            }
            An21Frame.Name = arc.File.Name;
            An21Frame.Pixels[index] = pixels;
            return pixels;
        }

        internal static byte[] DecompressRLE(IBinaryStream input, uint unpacked_size, int rle_step)
        {
            var output = new byte[unpacked_size];
            for (int i = 0; i < rle_step; ++i)
            {
                byte v1 = input.ReadUInt8();
                output[i] = v1;
                int dst = i + rle_step;
                while (dst < output.Length)
                {
                    byte v2 = input.ReadUInt8();
                    output[dst] = v2;
                    dst += rle_step;
                    if (v2 == v1)
                    {
                        int count = input.ReadUInt8();
                        if (0 != (count & 0x80))
                            count = input.ReadUInt8() + ((count & 0x7F) << 8) + 128;
                        while (count-- > 0 && dst < output.Length)
                        {
                            output[dst] = v2;
                            dst += rle_step;
                        }
                        if (dst < output.Length)
                        {
                            v2 = input.ReadUInt8();
                            output[dst] = v2;
                            dst += rle_step;
                        }
                    }
                    v1 = v2;
                }
            }
            return output;
        }
    }

    class An21Archive : AnmArchive
    {
        byte[][] Frames;

        public An21Archive(ArcView arc, ArchiveFormat impl, ICollection<Entry> dir, ImageMetaData base_info)
            : base(arc, impl, dir, base_info)
        {
            Frames = new byte[dir.Count][];
        }

        //ArcFileの中にピクセルデータを保持しているので、複数アーカイブのアンパック時にメモリ消費が大きい
        public byte[] GetFrame(int index)
        {
            if (index >= Frames.Length)
                throw new ArgumentException("index");
            if (null != Frames[index])
                return Frames[index];

            var entry = Dir.ElementAt(index);
            byte[] pixels;
            using (var stream = OpenEntry(entry))
            {
                pixels = new byte[stream.Length];
                stream.Read(pixels, 0, pixels.Length);
            }
            if (index > 0)
            {
                var prev_frame = GetFrame(index - 1);
                for (int i = 0; i < pixels.Length; ++i)
                    pixels[i] += prev_frame[i];
            }
            Frames[index] = pixels;
            return pixels;
        }
    }

    public class BitmapDecoder : IImageDecoder
    {
        public Stream Source { get { return null; } }
        public ImageFormat SourceFormat { get { return null; } }
        public ImageMetaData Info { get; private set; }
        public ImageData Image { get; private set; }

        public BitmapDecoder(byte[] pixels, ImageMetaData info)
        {
            Info = info;
            int stride = info.iWidth * info.BPP / 8;
            Image = ImageData.CreateFlipped(info, GetFormat(info.BPP), null, pixels, stride);
        }

        internal static PixelFormat GetFormat(int bpp)
        {
            switch (bpp)
            {
                case 8: return PixelFormats.Gray8;
                case 24: return PixelFormats.Bgr24;
                case 32: return PixelFormats.Bgra32;
                default: throw new InvalidFormatException();
            }
        }

        public void Dispose()
        {
        }
    }
}
