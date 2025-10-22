//! \file       ArcNexas.cs
//! \date       Sat Mar 14 18:03:04 2015
//! \brief      NeXAS enginge resource archives implementation.
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
// NeXASエンジンは戯画(2023-01-27に解散)が独自に開発したスクリプトエンジン


using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics.Eventing.Reader;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Xml.Linq;
using GameRes.Compression;
using GameRes.Formats.Artemis;
using GameRes.Formats.Macromedia;
using GameRes.Formats.Musica;
using GameRes.Formats.Strings;
using GameRes.Utility;
using ZstdNet;
using static System.Net.Mime.MediaTypeNames;
using static System.Net.WebRequestMethods;
using static GameRes.Formats.Emote.PsbReader;

namespace GameRes.Formats.NeXAS
{
    public enum Compression
    {
        None,
        Lzss,
        Huffman,
        Deflate,
        DeflateOrNone,
        Reserved1,
        Reserved2,
        ZstdOrNone //「アイキス2」, 「ガラス姫と鏡の従者」でこのタイプを発見。画像、音声データは非圧縮、テキストデータはZstandard(zstd)で圧縮されている
    }

    public class PacArchive : ArcFile
    {
        public readonly Compression PackType;

        public PacArchive(ArcView arc, ArchiveFormat impl, ICollection<Entry> dir, Compression type) : base(arc, impl, dir)
        {
            PackType = type;
        }
    }

    public class PacMetaData : ImageMetaData
    {
        public string Base;
    }

    [Export(typeof(ArchiveFormat))]
    public class PacOpener : ArchiveFormat
    {
        public override string Tag { get { return "PAC"; } }
        public override string Description { get { return "NeXAS engine resource archive"; } }
        public override uint Signature { get { return 0x00434150; } } // 'PAC\000'
        public override bool IsHierarchic { get { return false; } }
        public override bool CanWrite { get { return false; } }

        public PacOpener()
        {
            Signatures = new uint[] { 0x00434150, 0 };
        }

        public override ArcFile TryOpen(ArcView file)
        {
            if (!file.View.AsciiEqual(0, "PAC") || 'K' == file.View.ReadByte(3))
                return null;
            var reader = new IndexReader(file);
            var dir = reader.Read();
            if (null == dir)
                return null;

            //メタデータの初期化
            m_get_flag = false;
            m_metadata_dict.Clear();

            return new PacArchive(file, this, dir, reader.PackType);
        }

        internal sealed class IndexReader
        {
            ArcView m_file;
            int m_count;
            int m_pack_type;

            const int MaxNameLength = 0x40;

            public Compression PackType { get { return (Compression)m_pack_type; } }

            public IndexReader(ArcView file)
            {
                m_file = file;
                m_count = file.View.ReadInt32(4);
                m_pack_type = file.View.ReadInt32(8);
            }

            List<Entry> m_dir;

            public List<Entry> Read()
            {
                if (!IsSaneCount(m_count))
                    return null;
                m_dir = new List<Entry>(m_count);
                bool success = false;
                try
                {
                    success = ReadOld();
                }
                catch { /* ignore parse errors */ }
                if (!success && !ReadNew())
                    return null;
                return m_dir;
            }

            bool ReadNew()
            {
                uint index_size = m_file.View.ReadUInt32(m_file.MaxOffset - 4);
                int unpacked_size = m_count * 0x4C;
                if (index_size >= m_file.MaxOffset || index_size > unpacked_size * 2)
                    return false;

                var index_packed = m_file.View.ReadBytes(m_file.MaxOffset - 4 - index_size, index_size);
                for (int i = 0; i < index_packed.Length; ++i)
                    index_packed[i] = (byte)~index_packed[i];

                var index = HuffmanDecode(index_packed, unpacked_size);
                using (var input = new BinMemoryStream(index))
                    return ReadFromStream(input, 0x40);
            }

            bool ReadOld()
            {
                using (var input = m_file.CreateStream())
                {
                    input.Position = 0xC;
                    if (ReadFromStream(input, 0x20))
                        return true;
                    input.Position = 0xC;
                    return ReadFromStream(input, 0x40);
                }
            }

            bool ReadFromStream(IBinaryStream index, int name_length)
            {
                m_dir.Clear();
                for (int i = 0; i < m_count; ++i)
                {
                    var name = index.ReadCString(name_length);
                    if (string.IsNullOrWhiteSpace(name))
                        return false;
                    var entry = FormatCatalog.Instance.Create<PackedEntry>(name);
                    entry.Offset = index.ReadUInt32();
                    entry.UnpackedSize = index.ReadUInt32();
                    entry.Size = index.ReadUInt32();
                    if (!entry.CheckPlacement(m_file.MaxOffset))
                        return false;
                    entry.IsPacked = m_pack_type != 0 && (m_pack_type != 4 || entry.Size != entry.UnpackedSize);
                    m_dir.Add(entry);
                }
                return true;
            }
        }

        public override Stream OpenEntry(ArcFile arc, Entry entry)
        {
            var input = arc.File.CreateStream(entry.Offset, entry.Size, entry.Name);
            var pac = arc as PacArchive;
            var pent = entry as PackedEntry;
            if (null == pac || null == pent || !pent.IsPacked)
                return input;
            switch (pac.PackType)
            {
                case Compression.None:
                    return input;
                case Compression.Lzss:
                    return new LzssStream(input);
                case Compression.Huffman:
                    using (input)
                    {
                        var packed = new byte[entry.Size];
                        input.Read(packed, 0, packed.Length);
                        var unpacked = HuffmanDecode(packed, (int)pent.UnpackedSize);
                        return new BinMemoryStream(unpacked, 0, (int)pent.UnpackedSize, entry.Name);
                    }
                case Compression.ZstdOrNone:
                    if (entry.Type == "image" || entry.Type == "audio") //画像、音楽はそのまま
                        return input;
                    else
                        using (var dec = new ZstdNet.Decompressor()) //テキストはzstdで解凍
                        {
                            var unpacked = dec.Unwrap(arc.File.View.ReadBytes(entry.Offset, entry.Size));
                            return new BinMemoryStream(unpacked, entry.Name);
                        }
                case Compression.Deflate:
                default:
                    return new ZLibStream(input, CompressionMode.Decompress);
            }
        }

        public override IImageDecoder OpenImage(ArcFile arc, Entry entry)
        {
            using (var decoder = base.OpenImage(arc, entry))
            {
                var image = decoder.Image;
                
                if (System.IO.Path.GetExtension(entry.Name) == ".bmp")
                {
                    image.MakeTransparent( //黒色部分の透明化
                        System.Drawing.Color.FromArgb(0, 0, 0),
                        System.Drawing.Imaging.PixelFormat.Format32bppPArgb
                        );
                }

                var source = image.Bitmap;

                try
                {
                    SetParams(arc, entry);
                    if (m_metadata_dict.ContainsKey(entry.Name))
                    {
                        var info = m_metadata_dict[entry.Name];
                        if (info.Width == 0xFFFFFFFF || info.Height == 0xFFFFFFFF) //キスベルの個別対応
                        {
                            info.Width = 1280;
                            info.Height = 720;
                        }

                        int byte_depth = info.BPP / 8;
                        int stride = (int)info.Width * byte_depth;
                        var base_pixels = new byte[stride * info.Height];

                        //ベース画像
                        Entry base_entry = null;
                        if (!string.IsNullOrEmpty(info.Base))
                        {
                            var base_name = System.IO.Path.GetDirectoryName(entry.Name) + info.Base;
                            base_entry = arc.Dir.FirstOrDefault(e => e.Name.ToLower() == base_name.ToLower());
                            if (base_entry != null)
                            {
                                using (var input = arc.OpenImage(base_entry))
                                {
                                    input.Image.Bitmap.CopyPixels(Int32Rect.Empty, base_pixels, stride, 0);
                                }
                            }
                        }

                        //オフセット付加
                        int offset = info.OffsetY * stride + info.OffsetX * byte_depth;
                        var source_pixels = new byte[stride * info.Height];
                        source.CopyPixels(Int32Rect.Empty, source_pixels, stride, offset);

                        //重ね合わせ
                        if (base_entry == null)
                            base_pixels = source_pixels;
                        else
                        {
                            for (int i = 3; i < base_pixels.Length; i += 4)
                            {
                                if (source_pixels[i] != 0x00) //透明でない
                                {
                                    base_pixels[i - 1] = source_pixels[i - 1]; //R
                                    base_pixels[i - 2] = source_pixels[i - 2]; //G
                                    base_pixels[i - 3] = source_pixels[i - 3]; //B
                                }
                            }
                        }

                        source = BitmapImage.Create(
                            (int)info.Width,
                            (int)info.Height,
                            ImageData.DefaultDpiX,
                            ImageData.DefaultDpiY,
                            PixelFormats.Bgra32,
                            null,
                            base_pixels,
                            stride
                            );
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(System.IO.Path.GetFileName(entry.Name));
                    Console.WriteLine(e.Message);
                    Console.WriteLine(e.StackTrace);
                }

                return new BitmapSourceDecoder(source);
            }
        }

        internal static Dictionary<string, PacMetaData> m_metadata_dict = new Dictionary<string, PacMetaData>(StringComparer.OrdinalIgnoreCase);
        internal static bool m_get_flag = false;

        private void SetParams(ArcFile arc, Entry entry)
        {
            var name = System.IO.Path.GetFileName(entry.Name);
            var arc_name = System.IO.Path.GetFileName(arc.File.Name).ToLower();

            if (!m_get_flag)
            {
                ArcFile pac_arc;
                Entry dat_entry;


                if (arc_name == "visual.pac" || arc_name == "append.pac")
                {
                    var pac_dir = System.IO.Path.GetDirectoryName(arc.File.Name);
                    VFS.FullPath = new string[] { pac_dir };
                    pac_arc = ArcFile.TryOpen(pac_dir + "\\Config.pac");
                    dat_entry = pac_arc.Dir.FirstOrDefault(e => e.Name.ToLower() == "visual.dat");
                }
                else if (arc_name == "update.pac") //アペンドアーカイブ
                {
                    pac_arc = arc;
                    dat_entry = pac_arc.Dir.FirstOrDefault(e => e.Name == "visual.dat");
                    if (dat_entry == null)
                        return;
                }
                else
                    return;

                byte[] data;
                using (var input = pac_arc.OpenEntry(dat_entry))
                using (var ms = new MemoryStream())
                {
                    input.CopyTo(ms);
                    data = ms.ToArray();
                }

                if (arc_name == "Visual.pac")
                    pac_arc.Dispose(); //visual.datのデータ取得したらConfig.pacはメモリ開放

                byte dat_type = data.First();
                var span = data.AsSpan();

                //先頭インデックスのリスト作成
                var index_list = new List<int>();
                for (int i = 0; i <= data.Length - 12; ++i)
                {
                    if (BitConverter.ToString(span.Slice(i, 12).ToArray()).Replace("-", "") == "FF000000FF000000FF000000") //フレームの先頭を検索
                    {
                        index_list.Add(i);
                        i += 50; //1フレームは最低51バイト
                    }
                }
                index_list.Add(data.Length);

                //データ取得
                string base_name = "";
                string tag;
                if (dat_type == 0x0E)
                {
                    for (int i = 0; i < index_list.Count - 1; ++i)
                    {
                        int start = index_list[i];
                        int end = index_list[i + 1];

                        var name_bytes = span.Slice(start + 32, end - start - 48 - 1).ToArray();
                        int deli_index = Array.IndexOf(name_bytes, (byte)0x00);

                        if (deli_index == name_bytes.Length - 1) //デリミタ0x00が最後尾ならベース画像
                            continue;

                        base_name = new UTF8Encoding().GetString(name_bytes.AsSpan(0, deli_index).ToArray());
                        tag = new UTF8Encoding().GetString(name_bytes.AsSpan(deli_index + 1, name_bytes.Length - deli_index - 1).ToArray());

                        m_metadata_dict[tag] = new PacMetaData
                        {
                            Width = BitConverter.ToUInt32(span.Slice(end - 8, 4).ToArray(), 0),
                            Height = BitConverter.ToUInt32(span.Slice(end - 4, 4).ToArray(), 0),
                            OffsetX = BitConverter.ToInt32(span.Slice(end - 16, 4).ToArray(), 0),
                            OffsetY = BitConverter.ToInt32(span.Slice(end - 12, 4).ToArray(), 0),
                            BPP = 32,
                            Base = base_name
                        };
                    }
                    m_get_flag = true;
                }
                else if (dat_type == 0x0C)
                {
                    for (int i = 0; i < index_list.Count - 1; ++i)
                    {
                        int start = index_list[i];
                        int end = index_list[i + 1];

                        var name_bytes = span.Slice(start + 32, end - start - 40 - 1).ToArray();
                        int deli_index = Array.IndexOf(name_bytes, (byte)0x00);

                        if (deli_index == name_bytes.Length - 1) //デリミタ0x00が最後尾ならベース画像
                            continue;

                        base_name = new UTF8Encoding().GetString(name_bytes.AsSpan(0, deli_index).ToArray());
                        tag = new UTF8Encoding().GetString(name_bytes.AsSpan(deli_index + 1, name_bytes.Length - deli_index - 1).ToArray());

                        m_metadata_dict[tag] = new PacMetaData
                        {
                            Width = 1280,
                            Height = 720,
                            OffsetX = BitConverter.ToInt32(span.Slice(end - 8, 4).ToArray(), 0),
                            OffsetY = BitConverter.ToInt32(span.Slice(end - 4, 4).ToArray(), 0),
                            BPP = 32,
                            Base = base_name
                        };
                    }
                    m_get_flag = true;
                }
            }
            //元のアーカイブに戻す処理が必要
            VFS.FullPath = new string[] { arc.File.Name, "" };
        }

        static private byte[] HuffmanDecode(byte[] packed, int unpacked_size)
        {
            var dst = new byte[unpacked_size];
            var decoder = new HuffmanDecoder(packed, dst);
            return decoder.Unpack();
        }
    }
}
