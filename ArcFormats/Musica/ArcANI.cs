//! \file       ArcANI.cs
//! \date       2019 Jan 28
//! \brief      Musica animation resource.
//
// Copyright (C) 2019 by morkt
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
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameRes.Compression;
using GameRes.Formats.ExHibit;

namespace GameRes.Formats.Musica
{
    [Export(typeof(ArchiveFormat))]
    public class AniOpener : ArchiveFormat
    {
        public override string         Tag { get { return "ANI/PAZ"; } }
        public override string Description { get { return "Musica engine animation resource"; } }
        public override uint     Signature { get { return 0x040100; } }
        public override bool  IsHierarchic { get { return false; } }
        public override bool      CanWrite { get { return false; } }

        public AniOpener ()
        {
            Signatures = new uint[] { 0x040100, 0x020100, 0 };
        }

        public override ArcFile TryOpen (ArcView file)
        {
            if (file.View.ReadUInt16 (0) != 0x100)
                return null;
            int count = file.View.ReadInt16 (2);
            if (!IsSaneCount (count))
                return null;
            if (file.View.ReadUInt32 (4) != 0)
                return null;
            using (var input = file.CreateStream())
            {
                var base_name = Path.GetFileNameWithoutExtension (file.Name);
                input.Position = 8;
                var dir = new List<Entry> (count);
                for (int i = 0; i < count; ++i)
                {
                    var name = input.ReadCString();
                    if (string.IsNullOrWhiteSpace (name))
                        return null;
                    var entry = new Entry {
                        Name = string.Format ("{0}#{1}", base_name, name),
                        Type = "image",
                        Offset = input.Position,
                    };
                    uint width  = input.ReadUInt16();
                    uint height = input.ReadUInt16();
                    ushort bpp  = input.ReadUInt16();
                    entry.Size = width * height * bpp / 8 + 10;
                    if (!entry.CheckPlacement (file.MaxOffset))
                        return null;
                    dir.Add (entry);
                    input.Position = entry.Offset + entry.Size;
                }
                return new ArcFile (file, this, dir);
            }
        }

        public override IImageDecoder OpenImage (ArcFile arc, Entry entry)
        {
            using (var input = arc.File.CreateStream(entry.Offset, entry.Size))
            {
                var info = new ImageMetaData
                {
                    Width = input.ReadUInt16(),
                    Height = input.ReadUInt16(),
                    BPP = input.ReadUInt16(),
                    OffsetX = input.ReadInt16(),
                    OffsetY = input.ReadInt16(),
                };

                var decoder = new RawImageDecoder(input, info);
                var source = decoder.Image.Bitmap;

                (int base_width, int base_height, bool flag) = ReadBaseMetaData(Path.GetFileNameWithoutExtension(arc.File.Name), info);

                if (flag)
                {
                    int byte_depth = info.BPP / 8; //ビット深度（バイト換算）
                    int stride = base_width * byte_depth; //1行当たりのバイト数
                    int offset = info.OffsetY * stride + info.OffsetX * byte_depth;
                    var pixels = new byte[stride * base_height];

                    source.CopyPixels(Int32Rect.Empty, pixels, stride, offset);
                    source = BitmapImage.Create(
                        base_width,
                        base_height,
                        ImageData.DefaultDpiX,
                        ImageData.DefaultDpiY,
                        PixelFormats.Bgra32,
                        null,
                        pixels,
                        stride
                        );
                    //decoder.Image.Bitmap = source;
                }

                return new BitmapSourceDecoder(source);
            }
        }

        //ベース画像のWidth, Heightを取得
        private (int, int, bool) ReadBaseMetaData(string name, ImageMetaData info)
        {
            int base_width = info.OffsetX + (int)info.Width;
            int base_height = info.OffsetY + (int)info.Height;
            bool flag = false;

            try
            {
                if (VFS.CurrentArchive == null) //アーカイブ外 ⇒ ANIファイル直開き
                {
                    if (File.Exists(name + ".png"))
                        using (var input = File.OpenRead(name + ".png"))
                        {
                            input.Position = 0x10;
                            var bytes = new byte[8];
                            input.Read(bytes, 0, 8);
                            Array.Reverse(bytes); //pngはビッグエンディアンなので、反転
                            base_width = bytes.ToInt32(4);
                            base_height = bytes.ToInt32(0);
                        }
                }
                else //アーカイブ内から参照
                {
                    VFS.FullPath = new string[] { PazArc.m_arc.File.Name, "" };
                    using (var input = PazArc.m_arc.OpenEntry(VFS.FindFile(name + ".png")))
                    {
                        var bytes = new byte[0x18];
                        input.Read(bytes, 0, 0x18);
                        Array.Reverse(bytes); //pngはビッグエンディアンなので、反転
                        base_width = bytes.ToInt32(4);
                        base_height = bytes.ToInt32(0);
                    }
                    //元のアーカイブ、ディレクトリに戻す処理が必要
                    VFS.ChDir(VFS.FindFile(name + ".ani"));
                }
                flag = true;
            }
            catch (Exception ex) 
            {
                Console.WriteLine(ex.Message);
                Console.WriteLine(ex.StackTrace);
            }

            return (base_width, base_height, flag);
        }
    }

    internal class RawImageDecoder : BinaryImageDecoder
    {
        public RawImageDecoder (IBinaryStream input, ImageMetaData info) : base (input, info)
        {
        }

        protected override ImageData GetImageData ()
        {
            var format = GetFormatFromBpp (Info.BPP);
            m_input.Position = 10;
            int stride = Info.iWidth * Info.BPP / 8;
            var pixels = m_input.ReadBytes (stride * Info.iHeight);
            return ImageData.Create (Info, format, null, pixels, stride);
        }

        static PixelFormat GetFormatFromBpp (int bpp)
        {
            switch (bpp)
            {
            case 32:    return PixelFormats.Bgra32;
            case 24:    return PixelFormats.Bgr24;
            case 16:    return PixelFormats.Bgr565;
            case 8:     return PixelFormats.Gray8;
            default:    throw new InvalidFormatException();
            }
        }
    }
}
