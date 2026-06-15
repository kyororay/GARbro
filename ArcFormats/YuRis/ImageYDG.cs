//! \file       ImageYDG.cs
//! \date       Sat Jun 13 17:38:10 2026
//! \brief      YU-RIS compressed image.
//
// Copyright (C) 2026 by morkt
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
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameRes.Formats.Google;

namespace GameRes.Formats.YuRis
{
    internal class YdgEntry
    {
        public long Offset { get; set; }
        public uint Size { get; set; }
        public int Height { get; set; }
    }

    internal class YdgMetaData : ImageMetaData
    {
        public List<YdgEntry> EntryList;
    }

    [Export(typeof(ImageFormat))]
    public class YdgFormat : ImageFormat
    {
        public override string         Tag { get { return "YDG"; } }
        public override string Description { get { return "YU-RIS compressed image format"; } }
        public override uint     Signature { get { return 0x474459; } } //"YDG"

        public YdgFormat ()
        {
            Extensions = new string[] { "ydg" };
        }

        public override ImageMetaData ReadMetaData (IBinaryStream stream)
        {
            var header = stream.ReadHeader (0x10);
            if (!header.AsciiEqual ("YDG"))
                return null;
            if (!header.AsciiEqual (4, "YU-RIS"))
                return null;

            var header_length = 0x10 + header.ToUInt16(0x0C);
            header = stream.ReadHeader(header_length);

            var entry_count = header.ToInt32(0x30);
            var entry_list = new List<YdgEntry>(entry_count);

            for (int i = 0; i < entry_count; ++i)
            {
                entry_list.Add(new YdgEntry
                {
                    Offset = header.ToUInt32(0x34 + 0x10 * i),
                    Size = header.ToUInt32(0x34 + 0x10 * i + 4),
                    Height = header.ToInt16(0x34 + 0x10 * i + 10),
                });
            }

            return new YdgMetaData
            {
                Width = header.ToUInt16(0x20),
                Height = header.ToUInt16(0x22),
                BPP = 32,
                EntryList = entry_list,
            };
        }

        public override ImageData Read (IBinaryStream stream, ImageMetaData info)
        {
            var meta = (YdgMetaData) info;
            var stride = meta.iWidth * meta.BPP / 8;
            var pixels = new byte[stride * meta.Height];

            var format = new WebPFormat();
            int offsetY = 0;

            foreach (var entry in meta.EntryList)
            {
                stream.Position = entry.Offset;
                format.Decode(stream).CopyPixels(
                    Int32Rect.Empty, 
                    pixels, 
                    stride, 
                    stride * offsetY
                    );
                offsetY += entry.Height;
            }

            return ImageData.Create(
                meta,
                PixelFormats.Bgra32,
                null,
                pixels,
                stride
                );
        }

        public override void Write (Stream file, ImageData image)
        {
            throw new NotImplementedException ("YdgFormat.Write not implemented");
        }
    }
}
