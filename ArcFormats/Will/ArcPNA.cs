//! \file       ArcPNA.cs
//! \date       Wed Feb 17 23:02:21 2016
//! \brief      Pulltop multi-frame image.
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
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameRes.Formats.Entis;

namespace GameRes.Formats.Will
{
    internal class PnaEntry : ImageEntry
    {
        public ImageMetaData Info;
    }

    [Export(typeof(ArchiveFormat))]
    public class PnaOpener : ArchiveFormat
    {
        public override string Tag { get { return "PNA"; } }
        public override string Description { get { return "Pulltop multi-frame image format"; } }
        public override uint Signature { get { return 0x50414E50; } } // 'PNAP'
        public override bool IsHierarchic { get { return false; } }
        public override bool CanWrite { get { return false; } }

        public override ArcFile TryOpen(ArcView file)
        {
            int count = file.View.ReadInt32(0x10);
            if (!IsSaneCount(count))
                return null;

            var base_name = Path.GetFileNameWithoutExtension(file.Name);
            uint base_width = file.View.ReadUInt32(0x08);
            uint base_height = file.View.ReadUInt32(0x0C);

            uint index_offset = 0x14;
            uint current_offset = index_offset + (uint)count * 0x28;
            var dir = new List<Entry>(count);
            for (int i = 0; i < count; ++i)
            {
                uint size = file.View.ReadUInt32(index_offset + 0x24);
                if (size > 0)
                {
                    var imginfo = new ImageMetaData
                    {
                        BaseWidth = base_width,
                        BaseHeight = base_height,
                        OffsetX = file.View.ReadInt32(index_offset + 0x08),
                        OffsetY = file.View.ReadInt32(index_offset + 0x0C),
                        Width = file.View.ReadUInt32(index_offset + 0x10),
                        Height = file.View.ReadUInt32(index_offset + 0x14),
                        BPP = 32,
                    };
                    var entry = new PnaEntry
                    {
                        Name = string.Format("{0}#{1:D3}", base_name, file.View.ReadInt32(index_offset + 0x04)),
                        Size = size,
                        Offset = current_offset,
                        Info = imginfo,
                    };
                    if (!entry.CheckPlacement(file.MaxOffset))
                        return null;
                    dir.Add(entry);
                    current_offset += entry.Size;
                }
                index_offset += 0x28;
            }
            return new ArcFile(file, this, dir);
        }

        public override IImageDecoder OpenImage(ArcFile arc, Entry entry)
        {
            var pent = (PnaEntry)entry;
            using (var input = arc.File.CreateStream(entry.Offset, entry.Size, entry.Name))
            {
                var decoder = new PnaDecoder(input, pent.Info);
                var source = decoder.Image.Bitmap;

                try
                {
                    if (
                        pent.Info.OffsetX != 0 ||
                        pent.Info.OffsetY != 0 ||
                        pent.Info.Width != pent.Info.BaseWidth ||
                        pent.Info.Height != pent.Info.BaseHeight
                        ) //ç∑ï™âÊëú
                    {
                        int byte_depth = pent.Info.BPP / 8;
                        int stride = (int)pent.Info.BaseWidth * byte_depth;
                        var pixels = new byte[stride * pent.Info.BaseHeight];

                        (Int32Rect rect, int offset) = SetPosition(pent.Info, stride, byte_depth);

                        source.CopyPixels(rect, pixels, stride, offset);
                        source = BitmapImage.Create(
                            (int)pent.Info.BaseWidth,
                            (int)pent.Info.BaseHeight,
                            ImageData.DefaultDpiX,
                            ImageData.DefaultDpiY,
                            PixelFormats.Bgra32,
                            null,
                            pixels,
                            stride
                            );
                        //decoder.Image.Bitmap = source;
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(entry.Name);
                    Console.WriteLine(e.Message);
                    Console.WriteLine(e.StackTrace);
                }

                return new BitmapSourceDecoder(source);
            }
        }

        private (Int32Rect rect, int offset) SetPosition(ImageMetaData info, int stride, int byte_depth)
        {
            int rect_offsetx;
            int rect_offsety;
            int rect_width;
            int rect_height;
            int copy_offsetx;
            int copy_offsety;

            if (info.OffsetX < 0)
            {
                rect_offsetx = -info.OffsetX;
                copy_offsetx = 0;
            }
            else
            {
                rect_offsetx = 0;
                copy_offsetx = info.OffsetX;
            }
            if (info.OffsetY < 0)
            {
                rect_offsety = -info.OffsetY;
                copy_offsety = 0;
            }
            else
            {
                rect_offsety = 0;
                copy_offsety = info.OffsetY;
            }
            if (info.BaseWidth + info.OffsetX < info.Width)
                rect_width = (int)info.BaseWidth;
            else if (info.BaseWidth - info.OffsetX <= info.Width && info.Width <= info.BaseWidth + info.OffsetX)
                rect_width = (int)info.BaseWidth - info.OffsetX;
            else
                rect_width = (int)info.Width;

            if (info.BaseHeight + info.OffsetY < info.Height)
                rect_height = (int)info.BaseHeight;
            else if (info.BaseHeight - info.OffsetY <= info.Height && info.Height <= info.BaseHeight + info.OffsetY)
                rect_height = (int)info.BaseHeight - info.OffsetY;
            else
                rect_height = (int)info.Height;

            return (
                new Int32Rect(rect_offsetx, rect_offsety, rect_width, rect_height), 
                copy_offsety * stride + copy_offsetx * byte_depth
                );
        }
    }

    internal sealed class PnaDecoder : BinaryImageDecoder
    {
        public PnaDecoder(IBinaryStream input, ImageMetaData info) : base(input, info)
        {
        }

        protected override ImageData GetImageData()
        {
            var pixels = ReadPixels();
            return ImageData.Create(Info, PixelFormats.Bgra32, null, pixels);
        }

        byte[] ReadPixels()
        {
            var image = ImageFormat.Read(m_input);
            if (null == image)
                throw new InvalidFormatException();
            var bitmap = image.Bitmap;
            if (bitmap.Format.BitsPerPixel != 32)
            {
                bitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            }
            int stride = bitmap.PixelWidth * 4;
            var pixels = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, stride, 0);

            // restore colors premultiplied by alpha
            for (int i = 0; i < pixels.Length; i += 4)
            {
                int alpha = pixels[i + 3];
                if (alpha != 0 && alpha != 0xFF)
                {
                    pixels[i] = (byte)(pixels[i] * 0xFF / alpha);
                    pixels[i + 1] = (byte)(pixels[i + 1] * 0xFF / alpha);
                    pixels[i + 2] = (byte)(pixels[i + 2] * 0xFF / alpha);
                }
            }
            return pixels;
        }
    }
}
