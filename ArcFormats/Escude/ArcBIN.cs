//! \file       ArcBIN.cs
//! \date       Sat Dec 19 06:16:35 2015
//! \brief      Escu:de resource archives.
//
// Copyright (C) 2015 by morkt
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
using System.IO;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text;
using GameRes.Formats.Entis;
using GameRes.Formats.Kaguya;
using GameRes.Utility;
using GameRes.Formats.Macromedia;
using Microsoft.SqlServer.Server;
using System.Windows.Media.Imaging;
using System.Windows;
using System.Windows.Media;

namespace GameRes.Formats.Escude
{
    [Export(typeof(ArchiveFormat))]
    public class BinOpener : FVP.BinOpener
    {
        public override string         Tag { get { return "BIN/ESC-ARC"; } }
        public override string Description { get { return "Escu:de resource archive"; } }
        public override uint     Signature { get { return 0x2D435345; } } // 'ESC-'
        public override bool  IsHierarchic { get { return true; } }
        public override bool      CanWrite { get { return false; } }

        public BinOpener ()
        {
            Signatures = new uint[] { 0x2D435345 };
        }

        internal static Dictionary<string, ImageMetaData> m_metadata_dict = new Dictionary<string, ImageMetaData>(StringComparer.OrdinalIgnoreCase);

        public override ArcFile TryOpen (ArcView file)
        {
            if (!file.View.AsciiEqual (4, "ARC"))
                return null;
            int version = file.View.ReadByte (7) - '0';
            var reader = new IndexReader (file);
            List<Entry> dir = null;
            if (1 == version)
                dir = reader.ReadIndexV1();
            else if (2 == version)
                dir = reader.ReadIndexV2();
            if (null == dir)
                return null;
            m_metadata_dict.Clear(); //メタデータの初期化
            return new ArcFile (file, this, dir);
        }

        public override IImageDecoder OpenImage(ArcFile arc, Entry entry)
        {
            using (var decoder = base.OpenImage(arc, entry))
            {
                var source = decoder.Image.Bitmap;
                var raw_name = Path.GetFileNameWithoutExtension(entry.Name);

                SetParams(arc, entry.Name);

                if (m_metadata_dict.ContainsKey(raw_name))
                {
                    var info = m_metadata_dict[raw_name];
                    if (info.Width != source.PixelWidth || info.Height != source.PixelHeight)
                    {
                        int byte_depth = info.BPP / 8;
                        int stride = info.iWidth * byte_depth;
                        int offset = info.OffsetY * stride + info.OffsetX * byte_depth;
                        var pixels = new byte[stride * info.Height];

                        source.CopyPixels(Int32Rect.Empty, pixels, stride, offset);
                        source = BitmapImage.Create(
                            info.iWidth,
                            info.iHeight,
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

                return new BitmapSourceDecoder(source);
            }
        }

        private void SetParams(ArcFile arc, string name)
        {
            List<Entry> dir = arc.Dir.ToList();
            string lsf_name = name.Split('_')[0] + '_' + name.Split('_')[1] + ".lsf";

            if (!dir.Exists(e => e.Name == lsf_name))
                return;

            var entry = dir.Find(e => e.Name == lsf_name);

            using (var input = base.OpenEntry(arc, entry))
            {
                if (input.ReadStringUntil(0x00, Encoding.ASCII) != "LSF") //Signature確認
                    return;
                input.Position = 12;
                uint width = ReadUInt32(input); //ベース画像幅
                uint height = ReadUInt32(input); //ベース画像高さ

                for (int i = 28; i < input.Length; i += 164) //ヘッダー：28バイト、メタデータ：164バイト/1フレーム
                {
                    input.Position = i;
                    var tag = input.ReadStringUntil(0x00, Encoding.ASCII);
                    input.Position = i + 128;
                    m_metadata_dict[tag] = new ImageMetaData
                    {
                        OffsetX = ReadInt32(input), //差分画像X座標
                        OffsetY = ReadInt32(input), //差分画像Y座標
                        Width = width,
                        Height = height,
                        BPP = 32
                    };
                }
            }
        }

        private uint ReadUInt32(Stream stream)
        {
            byte[] buff = new byte[4];
            stream.Read(buff, 0, 4);
            return BitConverter.ToUInt32(buff, 0);
        }

        private int ReadInt32(Stream stream)
        {
            byte[] buff = new byte[4];
            stream.Read(buff, 0, 4);
            return BitConverter.ToInt32(buff, 0);
        }
    }

    internal sealed class IndexReader
    {
        ArcView   m_file;
        uint      m_seed;
        uint      m_count;

        public IndexReader (ArcView file)
        {
            m_file = file;
            m_seed = m_file.View.ReadUInt32 (8);
            m_count = file.View.ReadUInt32 (0xC) ^ NextKey();
        }

        public List<Entry> ReadIndexV1 ()
        {
            if (!ArchiveFormat.IsSaneCount ((int)m_count))
                return null;
            uint index_size = m_count * 0x88;
            var index = m_file.View.ReadBytes (0x10, index_size);
            if (index.Length != index_size)
                return null;
            Decrypt (index);
            int index_offset = 0;
            var dir = new List<Entry> ((int)m_count);
            for (uint i = 0; i < m_count; ++i)
            {
                var name = Binary.GetCString (index, index_offset, 0x80);
                var entry = FormatCatalog.Instance.Create<Entry> (name);
                entry.Offset = LittleEndian.ToUInt32 (index, index_offset+0x80);
                entry.Size   = LittleEndian.ToUInt32 (index, index_offset+0x84);
                if (!entry.CheckPlacement (m_file.MaxOffset))
                    return null;
                index_offset += 0x88;
                dir.Add (entry);
            }
            return dir;
        }

        public List<Entry> ReadIndexV2 ()
        {
            if (!ArchiveFormat.IsSaneCount ((int)m_count))
                return null;

            uint names_size = m_file.View.ReadUInt32 (0x10) ^ NextKey();
            uint index_size = m_count * 12;
            var index = m_file.View.ReadBytes (0x14, index_size);
            if (index.Length != index_size)
                return null;
            uint filenames_base = 0x14 + index_size;
            var names = m_file.View.ReadBytes (filenames_base, names_size);
            if (names.Length != names_size)
                return null;
            Decrypt (index);
            int index_offset = 0;
            var dir = new List<Entry> ((int)m_count);
            for (uint i = 0; i < m_count; ++i)
            {
                int filename_offset = LittleEndian.ToInt32 (index, index_offset);
                if (filename_offset < 0 || filename_offset >= names.Length)
                    return null;
                var name = Binary.GetCString (names, filename_offset, names.Length-filename_offset);
                if (0 == name.Length)
                    return null;
                var entry = FormatCatalog.Instance.Create<Entry> (name);
                entry.Offset = LittleEndian.ToUInt32 (index, index_offset+4);
                entry.Size   = LittleEndian.ToUInt32 (index, index_offset+8);
                if (!entry.CheckPlacement (m_file.MaxOffset))
                    return null;
                index_offset += 12;
                dir.Add (entry);
            }
            return dir;
        }

        unsafe void Decrypt (byte[] data)
        {
            fixed (byte* raw = data)
            {
                uint* data32 = (uint*)raw;
                for (int i = data.Length/4; i > 0; --i)
                {
                    *data32++ ^= NextKey();
                }
            }
        }

        uint NextKey ()
        {
            m_seed ^= 0x65AC9365;
            m_seed ^= (((m_seed >> 1) ^ m_seed) >> 3)
                    ^ (((m_seed << 1) ^ m_seed) << 3);
            return m_seed;
        }
    }
}
