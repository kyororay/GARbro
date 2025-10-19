//! \file       ArcXP3.cs
//! \date       Wed Jul 16 13:58:17 2014
//! \brief      KiriKiri engine archive implementation.
//
// Copyright (C) 2014-2017 by morkt
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
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;
using GameRes.Compression;
using GameRes.Formats.GUI;
using GameRes.Formats.Strings;
using GameRes.Utility;
using GARbro.GUI;

namespace GameRes.Formats.KiriKiri
{
    public struct Xp3Segment
    {
        public bool IsCompressed;
        public long Offset;
        public uint Size;
        public uint PackedSize;
    }

    public class Xp3Entry : PackedEntry
    {
        List<Xp3Segment> m_segments = new List<Xp3Segment>();

        public bool IsEncrypted { get; set; }
        public ICrypt Cipher { get; set; }
        public List<Xp3Segment> Segments { get { return m_segments; } }
        public uint Hash { get; set; }
        public object Extra { get; set; }
    }

    public class Xp3Options : ResourceOptions
    {
        public int Version { get; set; }
        public ICrypt Scheme { get; set; }
        public bool CompressIndex { get; set; }
        public bool CompressContents { get; set; }
        public bool RetainDirs { get; set; }
    }

    [Serializable]
    public class Xp3Scheme : ResourceScheme
    {
        public IDictionary<string, ICrypt> KnownSchemes;
        public ISet<string> NoCryptTitles;
    }

    // Archive version 1: encrypt file first, then calculate checksum
    //         version 2: calculate checksum, then encrypt

    [Export(typeof(ArchiveFormat))]
    public class Xp3Opener : ArchiveFormat
    {
        public override string Tag { get { return "XP3"; } }
        public override string Description { get { return arcStrings.XP3Description; } }
        public override uint Signature { get { return 0x0d335058; } }
        public override bool IsHierarchic { get { return true; } }
        public override bool CanWrite { get { return true; } }

        public Xp3Opener()
        {
            Signatures = new uint[] { 0x0d335058, 0x00905A4D, 0 };
            Extensions = new[] { "xp3", "exe" };
            ContainedFormats = new[] { "TLG", "BMP", "PNG", "JPEG", "OGG", "WAV", "TXT" };
        }

        static readonly byte[] s_xp3_header = {
            (byte)'X', (byte)'P', (byte)'3', 0x0d, 0x0a, 0x20, 0x0a, 0x1a, 0x8b, 0x67, 0x01
        };

        public bool ForceEncryptionQuery = true;

        internal static readonly ICrypt NoCryptAlgorithm = new NoCrypt();

        public override ArcFile TryOpen(ArcView file)
        {
            long base_offset = 0;
            if (0x5a4d == file.View.ReadUInt16(0)) // 'MZ'
                base_offset = SkipExeHeader(file, s_xp3_header);
            if (!file.View.BytesEqual(base_offset, s_xp3_header))
                return null;
            long dir_offset = base_offset + file.View.ReadInt64(base_offset + 0x0b);
            if (dir_offset < 0x13 || dir_offset >= file.MaxOffset)
                return null;
            if (0x80 == file.View.ReadUInt32(dir_offset))
            {
                dir_offset = base_offset + file.View.ReadInt64(dir_offset + 9);
                if (dir_offset < 0x13 || dir_offset >= file.MaxOffset)
                    return null;
            }
            int header_type = file.View.ReadByte(dir_offset);
            if (0 != header_type && 1 != header_type)
                return null;

            Stream header_stream;
            if (0 == header_type) // read unpacked header
            {
                long header_size = file.View.ReadInt64(dir_offset + 1);
                if (header_size > uint.MaxValue)
                    return null;
                header_stream = file.CreateStream(dir_offset + 9, (uint)header_size);
            }
            else // read packed header
            {
                long packed_size = file.View.ReadInt64(dir_offset + 1);
                if (packed_size > uint.MaxValue)
                    return null;
                long header_size = file.View.ReadInt64(dir_offset + 9);
                using (var input = file.CreateStream(dir_offset + 17, (uint)packed_size))
                    header_stream = ZLibCompressor.DeCompress(input);
            }

            var crypt_algorithm = new Lazy<ICrypt>(() => QueryCryptAlgorithm(file), false);

            var dir = new List<Entry>();
            dir_offset = 0;
            using (var header = new BinaryReader(header_stream, Encoding.Unicode))
            using (var filename_map = new FilenameMap())
            {
                Dictionary<string, HxEntry> hx_entry_info = null;
                while (-1 != header.PeekChar())
                {
                    uint entry_signature = header.ReadUInt32();
                    long entry_size = header.ReadInt64();
                    if (entry_size < 0)
                        return null;
                    dir_offset += 12 + entry_size;
                    if (0x656C6946 == entry_signature) // "File"
                    {
                        var entry = new Xp3Entry();
                        while (entry_size > 0)
                        {
                            uint section = header.ReadUInt32();
                            long section_size = header.ReadInt64();
                            entry_size -= 12;
                            if (section_size > entry_size)
                            {
                                // allow "info" sections with wrong size
                                if (section != 0x6f666e69)
                                    break;
                                section_size = entry_size;
                            }
                            entry_size -= section_size;
                            long next_section_pos = header.BaseStream.Position + section_size;
                            switch (section)
                            {
                                case 0x6f666e69: // "info"
                                    if (entry.Size != 0 || !string.IsNullOrEmpty(entry.Name))
                                    {
                                        goto NextEntry; // ambiguous entry, ignore
                                    }
                                    entry.IsEncrypted = 0 != header.ReadUInt32();
                                    long file_size = header.ReadInt64();
                                    long packed_size = header.ReadInt64();
                                    if (file_size >= uint.MaxValue || packed_size > uint.MaxValue || packed_size > file.MaxOffset)
                                    {
                                        goto NextEntry;
                                    }
                                    entry.IsPacked = file_size != packed_size;
                                    entry.Size = (uint)packed_size;
                                    entry.UnpackedSize = (uint)file_size;

                                    if (entry.IsEncrypted || ForceEncryptionQuery)
                                        entry.Cipher = crypt_algorithm.Value;
                                    else
                                        entry.Cipher = NoCryptAlgorithm;

                                    var name = entry.Cipher.ReadName(header);
                                    if (null == name)
                                    {
                                        goto NextEntry;
                                    }
                                    if (entry.Cipher.ObfuscatedIndex && ObfuscatedPathRe.IsMatch(name))
                                    {
                                        goto NextEntry;
                                    }
                                    if (filename_map.Count > 0)
                                        name = filename_map.Get(entry.Hash, name);
                                    if (name.Length > 0x100)
                                    {
                                        goto NextEntry;
                                    }
                                    entry.Name = name;
                                    entry.IsEncrypted = !(entry.Cipher is NoCrypt)
                                        && !(entry.Cipher.StartupTjsNotEncrypted && "startup.tjs" == name);
                                    break;
                                case 0x6d676573: // "segm"
                                    int segment_count = (int)(section_size / 0x1c);
                                    if (segment_count > 0)
                                    {
                                        for (int i = 0; i < segment_count; ++i)
                                        {
                                            bool compressed = 0 != header.ReadInt32();
                                            long segment_offset = base_offset + header.ReadInt64();
                                            long segment_size = header.ReadInt64();
                                            long segment_packed_size = header.ReadInt64();
                                            if (segment_offset > file.MaxOffset || segment_packed_size > file.MaxOffset)
                                            {
                                                goto NextEntry;
                                            }
                                            var segment = new Xp3Segment
                                            {
                                                IsCompressed = compressed,
                                                Offset = segment_offset,
                                                Size = (uint)segment_size,
                                                PackedSize = (uint)segment_packed_size
                                            };
                                            entry.Segments.Add(segment);
                                        }
                                        entry.Offset = entry.Segments.First().Offset;
                                    }
                                    break;
                                case 0x726c6461: // "adlr"
                                    if (4 == section_size)
                                        entry.Hash = header.ReadUInt32();
                                    break;

                                default: // unknown section
                                    break;
                            }
                            header.BaseStream.Position = next_section_pos;
                        }
                        if (!string.IsNullOrEmpty(entry.Name) && entry.Segments.Any())
                        {
                            if (entry.Cipher.ObfuscatedIndex)
                            {
                                DeobfuscateEntry(entry);
                            }
                            if (null != hx_entry_info)
                            {
                                if (hx_entry_info.TryGetValue(entry.Name, out HxEntry info))
                                {
                                    entry.Extra = info;

                                    var sb = new StringBuilder();
                                    if (!string.IsNullOrEmpty(info.Path))
                                    {
                                        sb.Append(info.Path);
                                        if (!info.Path.EndsWith("/") && !info.Path.EndsWith("\\"))
                                            sb.Append('/');
                                    }
                                    if (!string.IsNullOrEmpty(info.Name))
                                    {
                                        sb.Append(info.Name);
                                        if (sb.Length > 0)
                                            entry.Name = sb.ToString();
                                    }
                                    else
                                    {
                                        sb.Append(entry.Name);
                                        if (sb.Length > 0)
                                            entry.Name = sb.ToString();
                                    }
                                }
                            }
                            entry.Type = FormatCatalog.Instance.GetTypeFromName(entry.Name, ContainedFormats);
                            dir.Add(entry);
                        }
                    }
                    else if (0x3A == (entry_signature >> 24)) // "yuz:" || "sen:" || "dls:"
                    {
                        if (entry_size >= 0x10 && crypt_algorithm.Value is SenrenCxCrypt)
                        {
                            long offset = header.ReadInt64() + base_offset;
                            header.ReadUInt32(); // unpacked size
                            uint size = header.ReadUInt32();
                            if (offset > 0 && offset + size <= file.MaxOffset)
                            {
                                var yuz = file.View.ReadBytes(offset, size);
                                var crypt = crypt_algorithm.Value as SenrenCxCrypt;
                                crypt.ReadYuzNames(yuz, filename_map);
                            }
                        }
                    }
                    else if (0x34767848 == entry_signature) // "Hxv4"
                    {
                        if (crypt_algorithm.Value is HxCrypt)
                        {
                            try
                            {
                                var offset = header.ReadInt64() + base_offset;
                                var size = header.ReadUInt32();
                                var flags = header.ReadUInt16();
                                var hx = file.View.ReadBytes(offset, size);
                                var crypt = crypt_algorithm.Value as HxCrypt;
                                hx_entry_info = crypt.ReadIndex(Path.GetFileName(file.Name), hx);
                            }
                            catch (Exception) { /* ignore parse error */ }
                        }
                    }
                    else if (entry_size > 7)
                    {
                        // 0x6E666E68 == entry_signature    // "hnfn"
                        // 0x6C696D73 == entry_signature    // "smil"
                        // 0x46696C65 == entry_signature    // "eliF"
                        // 0x757A7559 == entry_signature    // "Yuzu"
                        uint hash = header.ReadUInt32();
                        int name_size = header.ReadInt16();
                        if (name_size > 0)
                        {
                            entry_size -= 6;
                            if (name_size * 2 <= entry_size)
                            {
                                var filename = new string(header.ReadChars(name_size));
                                filename_map.Add(hash, filename);
                            }
                        }
                    }
                NextEntry:
                    header.BaseStream.Position = dir_offset;
                }
            }
            if (0 == dir.Count)
                return null;

            m_metadata_dict.Clear();

            //foreach(var e in dir)
            //    Console.WriteLine("{0} : {1:X}", e.Name, e.Offset);

            var arc = new ArcFile(file, this, dir);
            try
            {
                if (crypt_algorithm.IsValueCreated)
                    crypt_algorithm.Value.Init(arc);
                return arc;
            }
            catch
            {
                arc.Dispose();
                throw;
            }
        }

        internal static string m_title;
        internal static Dictionary<string, TlgMetaData> m_metadata_dict = new Dictionary<string, TlgMetaData>(StringComparer.OrdinalIgnoreCase);

        public override IImageDecoder OpenImage(ArcFile arc, Entry entry)
        {
            using (var decoder = base.OpenImage(arc, entry))
            {
                var source = decoder.Image.Bitmap;

                try
                {
                    var file_name = Path.GetFileName(entry.Name);
                    if (!m_metadata_dict.ContainsKey(file_name))
                    {
                        ReadMetaData(arc, entry.Name.ToLower());
                        if (!m_metadata_dict.ContainsKey(file_name))
                            return decoder;
                    }

                    int byte_depth;
                    int stride;
                    byte[] pixels;
                    int offset;

                    var meta = m_metadata_dict[file_name];
                    if (meta.BaseName == null)
                    {
                        if (
                            (meta.BaseWidth != 0 && meta.BaseHeight != 0) &&
                            (meta.Width != meta.BaseWidth || meta.Height != meta.BaseHeight)
                            )
                        {
                            byte_depth = meta.BPP / 8;
                            stride = meta.iBaseWidth * byte_depth;
                            pixels = new byte[stride * meta.BaseHeight];
                        }
                        else
                            return decoder;
                    }
                    else
                    {
                        var base_name = Path.GetDirectoryName(entry.Name) + meta.BaseName;
                        var base_entry = arc.Dir.FirstOrDefault(e => e.Name.ToLower() == base_name);
                        if (base_entry == null)
                            return decoder;

                        using (var input = arc.OpenImage(base_entry))
                        {
                            meta.iBaseWidth = input.Image.Bitmap.PixelWidth;
                            meta.iBaseHeight = input.Image.Bitmap.PixelHeight;

                            byte_depth = meta.BPP / 8;
                            stride = meta.iBaseWidth * byte_depth;
                            pixels = new byte[stride * meta.BaseHeight];

                            input.Image.Bitmap.CopyPixels(Int32Rect.Empty, pixels, stride, 0);
                        }
                    }

                    Int32Rect rect = new Int32Rect(0, 0, meta.iWidth, meta.iHeight);
                    var offsset_x = meta.OffsetX;
                    var offsset_y = meta.OffsetY;
                    if (offsset_x < 0)
                    {
                        rect.X = -meta.OffsetX;
                        if (meta.Width + meta.OffsetX > meta.BaseWidth)
                            rect.Width = meta.iBaseWidth;
                        else
                            rect.Width = meta.iWidth;
                        offsset_x = 0;
                    }
                    if (offsset_y < 0)
                    {
                        rect.Y = -meta.OffsetY;
                        if (meta.Height + meta.OffsetY > meta.BaseHeight)
                            rect.Height = meta.iBaseHeight;
                        else
                            rect.Height = meta.iHeight;
                        offsset_y = 0;
                    }
                    offset = offsset_y * stride + offsset_x * byte_depth;

                    m_metadata_dict[file_name] = meta;

                    source.CopyPixels(rect, pixels, stride, offset);
                    source = BitmapImage.Create(
                        meta.iBaseWidth,
                        meta.iBaseHeight,
                        ImageData.DefaultDpiX,
                        ImageData.DefaultDpiY,
                        source.Format,
                        source.Palette,
                        pixels,
                        stride
                        );
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

        private void ReadMetaData(ArcFile arc, string name)
        {
            //オフセット付加のみ
            var v1_title = new List<string>()
            {
                //ハイクオソフト
                "面影レイルバック",
                "幼なじみとの暮らし方出張版　プロポーズ作戦",
                //Lose
                "ものべの -monobeno-",
                "ものべの -happy end-",
            };
            //オフセット付加+ベース画像重ね合わせ
            var v2_title = new List<string>()
            {
                //HULOTTE
                "妹のおかげでモテすぎてヤバい。",
                "叶とメグリとのその後がイチャらぶすぎてヤバい。",
                "嫁探しが捗りすぎてヤバい。",
                "神頼みしすぎて俺の未来がヤバい。",
                "出会って5分は俺のもの! 時間停止と不可避な運命",
                "俺の姿が、透明に!? 不可視の薬と数奇な運命",
                "俺の恋天使がポンコツすぎてコワ～い。",
                "俺の瞳で丸裸! 不可知な未来と視透かす運命",
                //HULOTTE Roi
                "ココロのカタチとイロとオト",
                //あざらしそふと+1
                "夢幻のティル・ナ・ノーグ", 
                //Mint CUBE
                "あま恋シロップス",
                "人気声優のつくりかた",
                "勇者と魔王と、魔女のカフェ",
                //CUBE
                "your diary+H",
                "恋する彼女の不器用な舞台",
                "間宮くんちの五つ子事情",
                "ゆらめく心に満ちた世界で、君の夢と欲望は叶うか",
                "神様のような君へ",
                "海と雪のシアンブルー",
                "神様のような君へ EXTENDED EDITION",
                "ネコと女子寮せよ！",
                "夏ノ終熄",
                "サメと生きる七日間",
                "恋し彩る正義爛漫",
                //Clover GAME
                "すれ違う兄妹の壊れる倫理観",
                "メイドちゃんは迷途ちゅう",
                "こあくまちゃんの誘惑っ!",
                //Sonora
                "僕の未来は、恋と課金と。 ～Charge To The Future～",
                "同じクラスのアイドルさん。Around me is full by a celebrity.",
                "響野さん家はエロゲ屋さん!",
                "ウチはもう、延期できない。",
            };

            if (v1_title.Contains(Xp3Opener.m_title))
                ReadMetaV1(arc, name);
            else if (v2_title.Contains(Xp3Opener.m_title))
                ReadMetaV2(arc, name);
        }

        //ハイクオソフト, Lose
        private void ReadMetaV1(ArcFile arc, string name)
        {
            var name_parts = name.Split('_');
            string meta_name = name_parts[0];
            for (int i = 1; i < name_parts.Length - 1; ++i)
                meta_name += '_' + name_parts[i];
            meta_name += ".txt";

            var entry = arc.Dir.FirstOrDefault(e => e.Name.ToLower() == meta_name);
            if (entry == null)
                return;

            using (var input = arc.OpenEntry(entry))
            {
                Encoding encoding;
                if (input.ReadByte() == 0x23 && input.ReadByte() == 0x00) //'#'
                    encoding = Encoding.Unicode;
                else
                    encoding = Encoding.GetEncoding(932);
                var reader = new StreamReader(input, encoding, false, 0x10000, false);
                var key_list = reader.ReadLine().Split('\t');

                var base_dict = new Dictionary<string, string>();
                var line = reader.ReadLine().Split('\t');
                foreach (var item in key_list.Zip(line, (key, value) => new { key, value }))
                    base_dict.Add(item.key, item.value);

                while (!reader.EndOfStream)
                {
                    Dictionary<string, string> meta_dict = new Dictionary<string, string>();
                    line = reader.ReadLine().Split('\t');
                    foreach (var item in key_list.Zip(line, (key, value) => new { key, value }))
                        meta_dict.Add(item.key, item.value);

                    try
                    {
                        m_metadata_dict.Add(
                            Path.GetFileNameWithoutExtension(meta_name) + '_' + meta_dict["layer_id"] + Path.GetExtension(name),
                            new TlgMetaData
                            {
                                BaseName = null,
                                OffsetX = int.Parse(meta_dict["left"]),
                                OffsetY = int.Parse(meta_dict["top"]),
                                Width = uint.Parse(meta_dict["width"]),
                                Height = uint.Parse(meta_dict["height"]),
                                BaseWidth = uint.Parse(base_dict["width"]),
                                BaseHeight = uint.Parse(base_dict["height"]),
                                BPP = 32
                            });
                    }
                    catch { }
                }
            }
        }

        //HULOTTE, あざらしそふと+1, CUBE, Mint CUBE
        private void ReadMetaV2(ArcFile arc, string name)
        {
            string meta_name;
            if (Path.GetFileName(name).Substring(0, 3) == "ev_")
                meta_name = name.Substring(0, 6);
            else
                meta_name = name.Substring(0, 4);

            if (name.Contains('l' + Path.GetExtension(name)))
                meta_name += "l.csv";
            else
                meta_name += ".csv";

            var meta_entry = arc.Dir.FirstOrDefault(e => e.Name.ToLower() == meta_name.ToLower());
            if (meta_entry == null)
                return;

            using (var input = arc.OpenEntry(meta_entry))
            using (var reader = new StreamReader(input, Encoding.GetEncoding(932), false, 0x10000, false))
            {
                var key_list = reader.ReadLine().Split(',');

                while (!reader.EndOfStream)
                {
                    var meta_dict = new Dictionary<string, string>();
                    var line = reader.ReadLine().Split(',');
                    foreach (var item in key_list.Zip(line, (key, value) => new { key, value }))
                        meta_dict.Add(item.key, item.value);
                    if (meta_dict["tag"] == meta_dict["base"])
                        continue;

                    try
                    {
                        m_metadata_dict.Add(
                            meta_dict["tag"].ToLower() + Path.GetExtension(name),
                            new TlgMetaData
                            {
                                BaseName = meta_dict["base"].ToLower() + Path.GetExtension(name),
                                OffsetX = int.Parse(meta_dict["x"]),
                                OffsetY = int.Parse(meta_dict["y"]),
                                Width = uint.Parse(meta_dict["w"]),
                                Height = uint.Parse(meta_dict["h"]),
                                BPP = 32
                            });
                    }
                    catch { }
                }
            }
        }

        static readonly Regex ObfuscatedPathRe = new Regex(@"[^\\/]+[\\/]\.\.[\\/]");

        private static void DeobfuscateEntry(Xp3Entry entry)
        {
            if (entry.Segments.Count > 1)
                entry.Segments.RemoveRange(1, entry.Segments.Count - 1);
            entry.IsPacked = entry.Segments[0].IsCompressed;
            entry.Size = entry.Segments[0].PackedSize;
            entry.UnpackedSize = entry.Segments[0].Size;
        }

        internal static long SkipExeHeader(ArcView file, byte[] signature)
        {
            var exe = new ExeFile(file);
            if (exe.ContainsSection(".rsrc"))
            {
                var offset = exe.FindString(exe.Sections[".rsrc"], signature);
                if (offset != -1 && 0 != file.View.ReadUInt32(offset + signature.Length))
                    return offset;
            }
            var section = exe.Overlay;
            while (section.Offset < file.MaxOffset)
            {
                var offset = exe.FindString(section, signature, 0x10);
                if (-1 == offset)
                    break;
                if (0 != file.View.ReadUInt32(offset + signature.Length))
                    return offset;
                section.Offset = offset + 0x10;
                section.Size = (uint)(file.MaxOffset - section.Offset);
            }
            return 0;
        }

        public override Stream OpenEntry(ArcFile arc, Entry entry)
        {
            var xp3_entry = entry as Xp3Entry;
            if (null == xp3_entry)
                return arc.File.CreateStream(entry.Offset, entry.Size);

            //xp3_entry.SetOrder();
            Stream input;
            if (1 == xp3_entry.Segments.Count && !xp3_entry.IsEncrypted)
            {
                var segment = xp3_entry.Segments.First();
                if (segment.IsCompressed)
                    input = new ZLibStream(arc.File.CreateStream(segment.Offset, segment.PackedSize),
                                            CompressionMode.Decompress);
                else
                    input = arc.File.CreateStream(segment.Offset, segment.Size);
            }
            else
                input = new Xp3Stream(arc.File, xp3_entry);

            return xp3_entry.Cipher.EntryReadFilter(xp3_entry, input);
        }

        public override ResourceOptions GetDefaultOptions()
        {
            return new Xp3Options
            {
                Version = Properties.Settings.Default.XP3Version,
                Scheme = GetScheme(Properties.Settings.Default.XP3Scheme),
                CompressIndex = Properties.Settings.Default.XP3CompressHeader,
                CompressContents = Properties.Settings.Default.XP3CompressContents,
                RetainDirs = Properties.Settings.Default.XP3RetainStructure,
            };
        }

        public override object GetCreationWidget()
        {
            return new GUI.CreateXP3Widget();
        }

        public override object GetAccessWidget()
        {
            return new GUI.WidgetXP3();
        }

        ICrypt QueryCryptAlgorithm(ArcView file)
        {

            var alg = GuessCryptAlgorithm(file);
            if (null != alg)
                return alg;
            var options = Query<Xp3Options>(arcStrings.XP3EncryptedNotice);
            m_title = new WidgetXP3().Scheme.SelectedValue as string;

            return options.Scheme;

            /*return new CxEncryption(new CxScheme
            {
                Mask = 0x0000017C,
                Offset = 0x00000682,
                PrologOrder = new byte[] { 1, 0, 2 },
                OddBranchOrder = new byte[] { 2, 1, 4, 5, 0, 3 },
                EvenBranchOrder = new byte[] { 4, 6, 1, 5, 2, 7, 0, 3 },
                ControlBlock = new uint[] { 0x9C91BADF, 0x8B8F868D, 0xDF919096, 0x8B91909C, 0xDF93908D, 0x9C90939D, 0xD2D2DF94, 0x9E8BACDF, 0x9E9C968B, 0xDF869393, 0x9BDF8D90, 0x929E9186, 0x939E9C96, 0xDFD38693, 0x9A8D969B, 0x86938B9C, 0xDF8D90DF, 0x969B9196, 0x8B9C9A8D, 0xDFD38693, 0x91968C8A, 0x978BDF98, 0x8FDF8C96, 0x8D98908D, 0x9EDF929E, 0x90D09B91, 0x939DDF8D, 0xDF949C90, 0x92908D99, 0x978B90DF, 0x8FDF8D9A, 0x8D98908D, 0xDF8C929E, 0x93939688, 0xDF9A9DDF, 0x9A939396, 0xDF939E98, 0x8BDF869D, 0x93DF9A97, 0x919A9C96, 0x9EDF9A8C, 0x9A9A8D98, 0x8B919A92, 0x4E7DDFD1, 0x897C337D, 0xB07C727C, 0x7F7C767C, 0x8A7C1D7D, 0x9D7C727C, 0x0F7DB17C, 0x3C6FBE7E, 0x3A7DB66C, 0x157D5F7D, 0xB66C516C, 0x5F7D3A7D, 0xBE7E157D, 0x256F436D, 0x3A7DB66C, 0x157D5F7D, 0x256F2B75, 0x3A7DB66C, 0x157D5F7D, 0x436EBE7E, 0x897C337D, 0xB07C727C, 0x7F7C767C, 0x187D567D, 0x5D7D8F68, 0x4E7D167D, 0x327D397D, 0xBC7C767C, 0x6C7CA57C, 0x367DA77C, 0x177D197D, 0x497D2974, 0x157D187D, 0x5D7D3B7D, 0x487D237D, 0xD7F5BD7E, 0xBEB4D6BC, 0x93BEDFB6, 0x96ADDF93, 0x8C8B9798, 0x8C9AADDF, 0x9A898D9A, 0xF5F5D19B, 0x1372B06E, 0x6B7C8A7E, 0x747CBF7C, 0x747C897C, 0xA77CB27C, 0x3A7D897E, 0x547DB174, 0x8B7C427D, 0xA47EB77C, 0xBC7C887C, 0xBA7C727C, 0x367DA67C, 0x3D7D237D, 0x167D127D, 0x70736971, 0x7EF5BE7E, 0x7EAB7C8A, 0x7CB17CA4, 0x7CB67C74, 0x7C977C8A, 0x7CA47EBB, 0x7E897E7F, 0x7D4E7DBD, 0x73157733, 0x7D567D70, 0xB16BCB18, 0xBD7E1C73, 0x755D6FDF, 0x7D367DBA, 0x7D237D32, 0x7DA16F41, 0x67426A33, 0x69327D9E, 0x7D157DB4, 0x7D5D7D3B, 0x7D567D37, 0x7E427D3E, 0x2677F5BD, 0x337D9F73, 0x9871A96C, 0xAB7CBE7E, 0xB37CA47E, 0x767C7A7C, 0xA67CA47E, 0x9C71337D, 0x557D826C, 0x916D9A75, 0x327D367D, 0x4E7D2E7D, 0xBE7E177D, 0x687D69F5, 0x710F7D32, 0x7D3E7D62, 0x6F687042, 0x7D427D54, 0x7C887E40, 0x7CBC7C7E, 0x7CA77CB4, 0x7CA47EB6, 0x7EA47EA0, 0x7D327D87, 0x6E337D44, 0x7D5D6C83, 0x6F4E6A36, 0x7D4A7D12, 0x7D5D7D3B, 0xF5BD7E42, 0x337D447D, 0x3268886C, 0x9D72337D, 0x1F7D1C6B, 0x3E7D5F7D, 0x567D3B7D, 0x6071BE7E, 0x367D196E, 0xA47EAB7C, 0x7A7CB37C, 0xA47E767C, 0x337DA67C, 0x1F7D6B6F, 0x704773F5, 0x6D337D52, 0x7D4A7D64, 0x7D56730F, 0x7D3B7D46, 0x7E427D5D, 0x7D417DBD, 0x73BE7E55, 0x686971AD, 0x6A367D32, 0x7DB86CBC, 0x68167D48, 0x710F7D32, 0x71407D62, 0x7D207D91, 0x887EF542, 0xBC7C7E7C, 0xA77CB47C, 0xA47EB67C, 0xA47EA07C, 0x337D877E, 0x4E6E496E, 0x337D447D, 0x337D1F7D, 0xA1740F7D, 0x74711D69, 0x167D487D, 0x557D456F, 0x557D1C70, 0x9171177D, 0x167D207D, 0x7DF5BD7E, 0x7D157D44, 0x7D21690F, 0x7D417D1F, 0x6D927239, 0x7D487D17, 0x7E2D7116, 0x5671F5BD, 0x337D187D, 0x2272496E, 0x246B0F7D, 0x4C7D176D, 0x5274157D, 0x367D9B72, 0x526B426B, 0x167D487D, 0xBD7E2D71, 0x7C7E7CF5, 0x7CB47CBC, 0x7EB67CA7, 0x7EA07CA4, 0x7DBD6DA4, 0x6E60711F, 0x6C367D19, 0x7D976B0E, 0x7D556A36, 0x7D427D56, 0x7D3B7D15, 0x7E527D1B, 0x417DF5BD, 0xBE7E557D, 0x6F685372, 0x4C7D337D, 0x567D377D, 0x5F7D367D, 0x3B7D3E7D, 0xBE7E1F7D, 0x237D5D7D, 0xAB7C417D, 0xB37CA47E, 0x767C7A7C, 0xA67CA47E, 0x327DBD6D, 0xBA755D6F, 0x45740F7D, 0x4A7D567D, 0x5D7D3B7D, 0x4E7D167D, 0x367D397D, 0x127D306A, 0x327D177D, 0x5D7D377D, 0x7CF5BD7E, 0x7CA47EAB, 0x7C7A7CB3, 0x7CA47E76, 0x6F327DA6, 0x7DA77E93, 0x69206B33, 0x741D7D3D, 0x7D366D15, 0x7D567433, 0x720F7D2E, 0x7E217DBB, 0x936FF5BD, 0x0F7DA77E, 0xBA71AC71, 0xBE7E4A7D, 0x547D7C74, 0x2B7D5674, 0x327D456F, 0x337D4E7D, 0x377D1C70, 0x036B5D7D, 0x377D0F71, 0x417D337D, 0x69F5BD7E, 0x6A1F7D25, 0x7D5D7D5D, 0x7D527D42, 0x77167D37, 0x74BF6A5D, 0x7D547556, 0x72916D33, 0x6FB87569, 0x7DBE7E82, 0x70337D44, 0x7D0C741C, 0x6D616C36, 0x7D4A7DBD, 0x70527442, 0x768172AF, 0x7C076D45, 0x7C747CB6, 0x7C727C98, 0x7D567DA7, 0x7E7CF518, 0xB47CBC7C, 0xB67CA77C, 0xA07CA47E, 0x337DA47E, 0x6B6A1577, 0xBE7E1F6E, 0x3370356D, 0xBC7C767C, 0x957C987C, 0xB07C6C7C, 0x6C7C767C, 0xA47EAB7C, 0x327DA67C, 0x377D4C7D, 0x187D557D, 0x177D8172, 0x517D726D, 0x337D7B6E, 0x5B7D197D, 0x8274367D, 0x45768172, 0x427D4A7D, 0x6FF5BD7E, 0x7E2A74A1, 0x715A68BE, 0x7CBE7E8E, 0x7CA47E75, 0x6FBE7E96, 0x7DBE7E7A, 0x7D177D22, 0x73337D5F, 0x7D936F23, 0x7D447D32, 0x7D437D15, 0x71367D15, 0x7D5D7D89, 0x7D166B0F, 0x7D377D20, 0x7E187D55, 0x732677BE, 0x6C337D9F, 0x7D9871A9, 0x7D407D42, 0x6E756E39, 0x7D487D31, 0xF5BD7E16, 0x7D3B72F5, 0x6EBE7E2E, 0x7D5D7D1A, 0x72167D37, 0x7D5D7D2F, 0x755D6F55, 0x6A0F7DBA, 0x72227D10, 0x7D1F7D61, 0x7D397D5B, 0x7E167D48, 0x7EA37EA3, 0xB438F5BD, 0xC7555EFE, 0x8EF2D7EF, 0xFF75BC02, 0x0F1F87C3, 0xA19B39C9, 0xA54BE2A0, 0x79DE934F, 0x4189507A, 0xB9DF7F4C, 0x9A1F578D, 0x929F2066, 0xEB653962, 0xB1011E6F, 0xDECB7892, 0xAE075E4F, 0x10A5D144, 0xF4C1932B, 0x0E69263F, 0xA7054BA0, 0x3C4576BF, 0x94B2A04D, 0xF7AC2162, 0xBA8B3C2C, 0xCF633CBC, 0xE2670DA8, 0x36FA3FC7, 0x161BD7FE, 0x48E34F8B, 0xC8B8172F, 0xE315007F, 0xCB9B0CA0, 0x2F41F592, 0xAA5AE1BD, 0xC93C59E5, 0x9E4FCA37, 0xB27EFEE0, 0x7899DF0C, 0x6D9D6096, 0x7705A963, 0x377F1F5D, 0x0E8E7E1F, 0x6AC2EA6F, 0x3E00A4F1, 0x18C0F999, 0x0F984BD8, 0x60D57E3D, 0x11816DD8, 0x8BD336F7, 0xFBF4BDF1, 0x794C426B, 0x9F7164FC, 0xD5D1F579, 0xDE254C34, 0xC20CBDB7, 0x689598CC, 0x8B66E2A1, 0x6D697500, 0x53D7DA5C, 0x051BA325, 0xB01480F0, 0xA908BD2A, 0x053D72C4, 0x2A451C4C, 0x97764AC9, 0x8A35AB97, 0xFE5A2E3D, 0x72979766, 0x8E1885EB, 0x9839B933, 0x868C99D1, 0x1BECD34F, 0xB380FD5A, 0x931CBAAB, 0xAFBB9E39, 0x63C77022, 0x78C9AA52, 0x166B0272, 0x0F8E6CB2, 0x3F9546F5, 0x18C0896A, 0x0750BE9E, 0xC5783F16, 0x822F5CFB, 0x8EBC54BB, 0x06CBCB88, 0xD63D627D, 0x150467E0, 0x2488A8FA, 0x111D8DBE, 0xCE7F07C3, 0xA30375CE, 0x14451024, 0xAB766DC0, 0xFD979C30, 0x982EA5D9, 0x666A6DCB, 0xF63B2A04, 0xFEB5722F, 0xC8B301E4, 0x6FB45ACA, 0x5D700452, 0x900029A8, 0x82AC6E98, 0x95352986, 0x385F03E7, 0x0A4124C3, 0x6D5B3060, 0xEB445988, 0xB73D9363, 0x59CB422A, 0x235B97BF, 0x654FEB00, 0xD6F9B389, 0xE3F39B95, 0xCE72B055, 0x941A6ECD, 0x4C4C09C0, 0x20768E3B, 0x2CBF259D, 0x49906ABF, 0xFA774076, 0x95C59355, 0xBEEBAB8F, 0x71DD3C1D, 0x5DA816B8, 0x86ABDE99, 0xC60A87FD, 0x1CC6CCDE, 0xCA31DEF9, 0x85F186B0, 0x3D87E797, 0x65983BF5, 0x25C540C1, 0x96CFBFC0, 0x5CDAD8D6, 0x2701E2F7, 0x291B2949, 0x99DA9592, 0x0C7193E2, 0xBDC811EC, 0x9E98BBBF, 0xDD767ECA, 0x3D38200A, 0x2079B733, 0x9516BCF7, 0xBE42D4BE, 0xD1F1A040, 0x95156545, 0x5BFF07D2, 0x8F5C3FE5, 0xAC2A62EA, 0x264317A2, 0xE8FFAECE, 0xE4127B35, 0xA36A246F, 0xF581C2C2, 0xCDC42387, 0xDEFD0405, 0xD1219EEE, 0xC7A0A635, 0x779E02EC, 0xD17504F7, 0x822C27E6, 0x3DBBB550, 0x5FAE2794, 0x697E0BD2, 0x467BF00E, 0x4A8D63C3, 0xD6A8E6C6, 0xE70C239B, 0xAC5058DC, 0x4DFDF5C6, 0x3B70C72E, 0xA7311D36, 0x8C125DEE, 0xF6348D66, 0x02CD75AC, 0xF8EB68E7, 0x8B7327C0, 0xC0C19439, 0x722832B4, 0x10D6F382, 0xE32CAC1B, 0xEA699D1D, 0x672AF5EE, 0x8D3B0D9D, 0xA284EDF1, 0x9D971228, 0xD8709624, 0x54135BE8, 0x872949E1, 0xB3BDB718, 0x59481784, 0xB1E54EF8, 0xB4241F26, 0x558068F3, 0x68510B22, 0x93132601, 0x5E57E277, 0x5B6EBC01, 0x6BAB8BD5, 0x958ECA65, 0x7DAF2D05, 0x4977905D, 0xB9BAD6D6, 0xD5E5CE26, 0xFC018EBE, 0x95644A30, 0xDD44FD02, 0x1B755667, 0x67FC06ED, 0x43B21B3E, 0xA01CA7DC, 0x11887699, 0xA02A8098, 0x44C55995, 0x076E8069, 0xCC96292F, 0x884F84AE, 0xF8958866, 0xED525A53, 0xA775C4CB, 0xB5D6E68D, 0xF2633F1E, 0x7F845956, 0x322AFEEC, 0xA008ACBE, 0x0DE0E140, 0x4FA139B2, 0x669573A5, 0xD2F26F95, 0xA8B88B47, 0xD94A366C, 0x8F843AC6, 0x6B726E93, 0x10FC7066, 0x897E06CB, 0x56672EC0, 0xBC88E093, 0xAD000DA7, 0xD2A0713D, 0xC6669A93, 0x6554B94D, 0x4A91D69C, 0x230484C0, 0x80A9AA1F, 0x2F0CEB95, 0xE120BBEE, 0xD4525119, 0x8F891BC4, 0x22B2767A, 0x85731290, 0xEB91DE6C, 0x92D95A01, 0xAAF104A8, 0x7F27FB0C, 0x77AAC887, 0xA367F646, 0xE33D2C94, 0xB01FDF32, 0xEF29ED07, 0x6D880D12, 0x9095C280, 0x4094DF5B, 0xFBABB44F, 0x76258EC4, 0x26FF49BC, 0x6E53B457, 0x5780F535, 0x980B6B29, 0xB93FFFC1, 0x9BCF6361, 0x9F0870A2, 0xE8C3B441, 0xD2FF7F16, 0x05F33486, 0x27B5E8D5, 0xCB715A4A, 0xE0377FFB, 0xBDBEB020, 0xDE9A540F, 0xC46AE280, 0x4044A3EE, 0x70946F3E, 0x5ED120B5, 0x66627EE2, 0xB3AE9F67, 0xD7B1AA97, 0xCE7E9F0E, 0x9105D2BE, 0xAE2ACD7C, 0x4F059E2C, 0xBBDCA87A, 0x4743A320, 0x4842BB3D, 0x70E6F471, 0xB07543D6, 0x55777654, 0xCB3EFFB6, 0x41C0EAE6, 0xE2487289, 0x3A3A0418, 0xF18BAD60, 0x2BC6D9C2, 0x8471980A, 0x6E92A148, 0x2E4655FD, 0x22EDA4C4, 0x6CF4F092, 0x6103068F, 0xB111B59D, 0xEAE51460, 0x324F27F4, 0x28C0CB9A, 0x2F8819DC, 0xB721DC04, 0xD5398F23, 0xE0C77C44, 0x9F43C681, 0x8B968906, 0xE157DF62, 0x4BA87048, 0x9DB3CFDE, 0xB5D65D13, 0x0D61A51F, 0x142DE3A3, 0x15C2B852, 0x15D50951, 0xE67E31C7, 0x4AB1707D, 0xF6613C37, 0xB90CCF0A, 0xE99A3A6E, 0xC798F682, 0xE5D3E443, 0xBECB1044, 0xAC630787, 0xC7DADF7C, 0xE4DDFAB8, 0xCEA782EC, 0x7F17BB9F, 0x2F962A75, 0x8E7F1E63, 0xE55D58A5, 0x5E65686F, 0x8A5EB638, 0xBC16D4DD, 0x6A7C18FA, 0x5EF096FB, 0xBC10A17C, 0x3C77D62C, 0xE79DD6A0, 0xA28BC477, 0x5EB7ED1D, 0xFF405EA0, 0xF20ADB60, 0xC4ACB44E, 0xDF9CEB73, 0x15181655, 0x8F46AA10, 0x4B8B4D18, 0xF677AB95, 0xD9B35D74, 0x33FBF1F7, 0x98FF9A79, 0xDF0DBF9E, 0x7F2D6583, 0x8053E34B, 0x5653DAF2, 0xE020ED36, 0x74F96BCD, 0xB7B66E93, 0x6E44E60D, 0xA3614584, 0x77694801, 0x5A6AE923, 0x6FD28513, 0x29AD6FB0, 0xB685E3F6, 0x079E1135, 0x871AB695, 0xCC414E61, 0xD8802E1D, 0x7B8A4F99, 0xEC60CE76, 0x99562A30, 0xA1B5D997, 0xD3055478, 0x4F45A31B, 0xF89568B3, 0x795D334D, 0x2B61F844, 0x4DF1D43F, 0x648042A2, 0x5F671156, 0x7CD3EF26, 0x5EB9B1D0, 0x70ED5E92, 0x709DDD73, 0x9F5772EC, 0x4366B111, 0x286B47C9, 0xDC6268D3, 0x8B2F4501, 0xF9549086, 0x7B89D52E, 0xB8B330AF, 0x78522453, 0x759CFC39, 0xF282F426, 0xEC58A4AC, 0xB97EA44B, 0xDE5F5D37, 0xCBE150F9, 0x0F43AC0C, 0x4374B337, 0x4D5CB872, 0xBAEE4239, 0x207A1EA9, 0xE7BE88C0, 0x8CA32F5D, 0x6C9E8A1D, 0xDDA73C6F, 0x46898104, 0xCBD813B1, 0xB252E599, 0x376417B0, 0x73DAD7C9, 0x68D11D1A, 0x688F8182, 0x23BBF9AD, 0xCA3A1ABF, 0x9DC4A827, 0x0CC1ED0D, 0x891CEC50, 0x7939A5EA, 0x4B88D67F, 0x06C358FB, 0xA145E453, 0xEE5C781A, 0x7DBABC75, 0xD3E44554, 0x2AA7F517, 0x007F3FD2, 0xF0E4F6D7, 0x767672B9, 0x9275AEAD, 0xBC813F43, 0xD4C48312, 0x7E2315AD, 0xAD750ABE, 0xFA7A33AF, 0x56EAD7A9, 0xD8BDEE54, 0xDE79F2C1, 0xC1660165, 0xA166AA64, 0x81A44281, 0x4F909C69, 0x46D7B629, 0x260CFCEC, 0xB459C527, 0x986C7903, 0xAFF3FA27, 0x2311DD8E, 0xFBA75CD6, 0xF9F006F9, 0xAF6AEB94, 0x233AA6ED, 0x81484CF3, 0x7F83F3C1, 0xE84E225B, 0x472063ED, 0x09A6AD5D, 0xBF89E60B, 0xEB492161, 0x4C4E02E9, 0x1F8616A7, 0x7E5F1236, 0xF8C8B1EF, 0x8FEEEB4C, 0x345CBF42, 0xE50A7CA9, 0x9D8195AB, 0x1F8F5858, 0xDDF8F2B0, 0x2D9D82D9, 0xE7DE37FE, 0x3FBF67D0, 0x5C1A7630, 0xD5D0CD37, 0xD48F2B0C, 0x44F01D6C, 0xF5205874, 0x4E9758DD, 0xFCF91F1D, 0x9674F00A, 0xE32312FD, 0x7A61C705, 0x5AD80696, 0x0F3F7F48, 0xDABCFE88, 0x2055DAE1, 0xE5ED9390, 0xFD575C49, 0xE4B18C98, 0x7A82F1A0, 0x7641888C, 0x8045EF34, 0x8196D89A, 0x21B536DF, 0x728EC784, 0xDA33EF96, 0x68BD20EE, 0x15215152, 0x1B17EA99, 0xC49B80FB, 0x8625B305, 0x6595483E, 0xC997E064, 0xD10B0768, 0x61EBB02D, 0xB75A6A4F, 0x699843A5, 0x32C3BC10, 0x35BBB8A2, 0xB1D2D7A1, 0x5E311606, 0x1F12FDE0, 0xB13C8D55, 0x0CB729A9, 0x28A9E686, 0x9EDB4F84, 0x0054CEC2, 0x841FA767, 0xF480256D, 0x5F53299C, 0x21DAB1AA, 0x27A8A65B, 0xD10EE26B, 0x1AEEA80F, 0x0C5E97E7, 0x482CF7DC, 0xA819F247, 0x25D5FE66, 0xEBAFD569, 0x5A1EE81F, 0x05B572F0, 0x52821F6D, 0x24AB602B, 0xA5ABA474, 0xB1260AAD, 0xC721D725, 0xB11DEEEB, 0x23F6A169, 0x63198C6F, 0x1C8BA800, 0x9390E455, 0x70A2A4A4, 0x34100078, 0x7F93A368, 0x53F84B36, 0x1A276C9C, 0x47F3652A, 0x0EE63398, 0xDF4989A9, 0x9548698B, 0x12CD48E9, 0xFB09B63F, 0xAE2818CA, 0x9513E4C1, 0x77948C93, 0xA8EFAE40, 0x7A8220E5, 0x428D3564, 0x297E0B6D, 0x4F5E8332, 0x4855BD25, 0xE51A1F65, 0x263DEA16, 0xA6EC70C1, 0xC1DF2877, 0x9C617393, 0x7FA15670, 0x4DC3A8FB, 0x57E7DE72, 0x07D9CEBA, 0xF2A7A61B, 0x710EFF4F, 0xFF44DA33, 0x781D6AFE, 0xCCDDB889, 0xF56AB0E7, 0xB8DCCB75, 0xA4E68D62, 0x66FCA537, 0x8C064AFA, 0x683E2CAF, 0x54B61DB3, 0xED19EBB8, 0xE0012145, 0xF96E0F1E, 0x12C639EA, 0xF94B7469, 0x376EEE3C, 0x1EB8416A, 0xDC8D7DAC, 0xC2E6023D, 0x64907E22, 0x501873DC, 0x5219530A, 0x0EA78FEF, 0xB07CF44E, 0x30C7A7BF, 0x6701E3A1, 0x13B04FC3, 0x534AB5AF, 0x72ED9D18, 0x11E8A0F7, 0xF3091DA2, 0xDED35F25, 0xE5BE16F4, 0x47A96A03, 0x0390DE9E, 0x2D206222, 0xCDEE89E8, 0x773C4C13, 0x32D880F4, 0x5611311F, 0x861B370E, 0x8E4F0233, 0xB45CE953, 0x9A0089D6, 0xBF95754B, 0x6C59A1AA, 0x54295865, 0xD791D5C8, 0xB388D94A, 0x65E0CC6C, 0x826496CD, 0xF242ADC8, 0x8F9CD981, 0xF0306375, 0x8A39A1F4, 0xCE29662A, 0xFAEA3DA7, 0xA38D3352, 0x3737333A, 0xB7DB4E9A, 0xEFE659B3, 0xAE8AD6D8, 0x34847F39, 0xD31D66A7, 0x655518BC, 0x5EAD9FCA, 0x070C4FAF, 0x65AA8671, 0x813505E6, 0x13AE25B5, 0x5AB19876, 0xFACC86FF, 0xA905A638, 0x11481573, 0x137AB3ED, 0x398DD999, 0xD0729889, 0xF163AB19, 0xB792786D, 0x94FEE3BD, 0xF776B60F, 0x2BCEB158 },
                TpmFileName = null
            });*/

        }

        public static ICrypt GetScheme(string scheme)
        {
            ICrypt algorithm;
            if (string.IsNullOrEmpty(scheme) || !KnownSchemes.TryGetValue(scheme, out algorithm))
                algorithm = NoCryptAlgorithm;
            return algorithm;
        }

        static uint GetFileCheckSum(Stream src)
        {
            // compute file checksum via adler32.
            // src's file pointer should be reset to zero.
            var sum = new Adler32();
            byte[] buf = new byte[64 * 1024];
            for (; ; )
            {
                int read = src.Read(buf, 0, buf.Length);
                if (0 == read) break;
                sum.Update(buf, 0, read);
            }
            return sum.Value;
        }

        public override void Create(Stream output, IEnumerable<Entry> list, ResourceOptions options,
                                     EntryCallback callback)
        {
            var xp3_options = GetOptions<Xp3Options>(options);

            ICrypt scheme = xp3_options.Scheme;
            bool compress_index = xp3_options.CompressIndex;
            bool compress_contents = xp3_options.CompressContents;
            bool retain_dirs = xp3_options.RetainDirs;

            bool use_encryption = !(scheme is NoCrypt);

            using (var writer = new BinaryWriter(output, Encoding.ASCII, true))
            {
                writer.Write(s_xp3_header);
                if (2 == xp3_options.Version || 3 == xp3_options.Version)
                {
                    writer.Write((long)0x17);
                    writer.Write((int)1);
                    writer.Write((byte)0x80);
                    writer.Write((long)0);
                }
                long index_pos_offset = writer.BaseStream.Position;
                writer.BaseStream.Seek(8, SeekOrigin.Current);

                int callback_count = 0;
                var used_names = new HashSet<string>();
                var dir = new List<Xp3Entry>();
                long current_offset = writer.BaseStream.Position;
                foreach (var entry in list)
                {
                    if (null != callback)
                        callback(callback_count++, entry, arcStrings.MsgAddingFile);

                    string name = entry.Name;
                    if (!retain_dirs)
                        name = Path.GetFileName(name);
                    else
                        name = name.Replace(@"\", "/");
                    if (!used_names.Add(name))
                    {
                        Trace.WriteLine("duplicate name", entry.Name);
                        continue;
                    }

                    var xp3entry = new Xp3Entry
                    {
                        Name = name,
                        Cipher = scheme,
                        IsEncrypted = use_encryption
                                       && !(scheme.StartupTjsNotEncrypted && VFS.IsPathEqualsToFileName(name, "startup.tjs"))
                    };
                    bool compress = compress_contents && ShouldCompressFile(entry);
                    using (var file = File.Open(name, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (!xp3entry.IsEncrypted || 0 == file.Length)
                            RawFileCopy(file, xp3entry, output, compress);
                        else
                            EncryptedFileCopy(file, xp3entry, output, compress);
                    }

                    dir.Add(xp3entry);
                }

                long index_pos = writer.BaseStream.Position;
                writer.BaseStream.Position = index_pos_offset;
                writer.Write(index_pos);
                writer.BaseStream.Position = index_pos;

                using (var header = new BinaryWriter(new MemoryStream(dir.Count * 0x58), Encoding.Unicode))
                {
                    if (null != callback)
                        callback(callback_count++, null, arcStrings.MsgWritingIndex);

                    long dir_pos = 0;
                    if (3 == xp3_options.Version)
                    {
                        foreach (var entry in dir)
                        {
                            header.Write((uint)0x6e666e68); // "hnfn"
                            header.Write((long)(4 + 2 + entry.Name.Length * 2));
                            header.Write((uint)entry.Hash);
                            header.Write((short)entry.Name.Length);
                            foreach (char c in entry.Name)
                                header.Write(c);
                        }
                        dir_pos = header.BaseStream.Position;
                    }
                    foreach (var entry in dir)
                    {
                        var entry_name = entry.Name;
                        if (3 == xp3_options.Version)
                        {
                            using (var md5 = MD5.Create())
                            {
                                var text_bytes = Encoding.Unicode.GetBytes(entry.Name.ToLowerInvariant());
                                var hash = md5.ComputeHash(text_bytes);
                                var sb = new StringBuilder(32);
                                for (int i = 0; i < hash.Length; ++i)
                                    sb.AppendFormat("{0:x2}", hash[i]);
                                entry_name = sb.ToString();
                            }
                        }
                        header.BaseStream.Position = dir_pos;
                        header.Write((uint)0x656c6946); // "File"
                        long header_size_pos = header.BaseStream.Position;
                        header.Write((long)0);
                        header.Write((uint)0x6f666e69); // "info"
                        header.Write((long)(4 + 8 + 8 + 2 + entry_name.Length * 2));
                        header.Write((uint)(use_encryption ? 0x80000000 : 0));
                        header.Write((long)entry.UnpackedSize);
                        header.Write((long)entry.Size);

                        header.Write((short)entry_name.Length);
                        foreach (char c in entry_name)
                            header.Write(c);

                        header.Write((uint)0x6d676573); // "segm"
                        header.Write((long)0x1c);
                        var segment = entry.Segments.First();
                        header.Write((int)(segment.IsCompressed ? 1 : 0));
                        header.Write((long)segment.Offset);
                        header.Write((long)segment.Size);
                        header.Write((long)segment.PackedSize);

                        header.Write((uint)0x726c6461); // "adlr"
                        header.Write((long)4);
                        header.Write((uint)entry.Hash);

                        dir_pos = header.BaseStream.Position;
                        long header_size = dir_pos - header_size_pos - 8;
                        header.BaseStream.Position = header_size_pos;
                        header.Write(header_size);
                    }

                    header.BaseStream.Position = 0;
                    writer.Write(compress_index);
                    long unpacked_dir_size = header.BaseStream.Length;
                    if (compress_index)
                    {
                        if (null != callback)
                            callback(callback_count++, null, arcStrings.MsgCompressingIndex);

                        long packed_dir_size_pos = writer.BaseStream.Position;
                        writer.Write((long)0);
                        writer.Write(unpacked_dir_size);

                        long dir_start = writer.BaseStream.Position;
                        using (var zstream = new ZLibStream(writer.BaseStream, CompressionMode.Compress,
                                                             CompressionLevel.Level9, true))
                            header.BaseStream.CopyTo(zstream);

                        long packed_dir_size = writer.BaseStream.Position - dir_start;
                        writer.BaseStream.Position = packed_dir_size_pos;
                        writer.Write(packed_dir_size);
                    }
                    else
                    {
                        writer.Write(unpacked_dir_size);
                        header.BaseStream.CopyTo(writer.BaseStream);
                    }
                }
            }
            output.Seek(0, SeekOrigin.End);
        }

        void RawFileCopy(FileStream file, Xp3Entry xp3entry, Stream output, bool compress)
        {
            if (file.Length > uint.MaxValue)
                throw new FileSizeException();

            uint unpacked_size = (uint)file.Length;
            xp3entry.UnpackedSize = (uint)unpacked_size;
            xp3entry.Size = (uint)unpacked_size;
            compress = compress && unpacked_size > 0;
            var segment = new Xp3Segment
            {
                IsCompressed = compress,
                Offset = output.Position,
                Size = unpacked_size,
                PackedSize = unpacked_size
            };
            if (compress)
            {
                var start = output.Position;
                using (var zstream = new ZLibStream(output, CompressionMode.Compress, CompressionLevel.Level9, true))
                {
                    xp3entry.Hash = CheckedCopy(file, zstream);
                }
                segment.PackedSize = (uint)(output.Position - start);
                xp3entry.Size = segment.PackedSize;
            }
            else
            {
                xp3entry.Hash = CheckedCopy(file, output);
            }
            xp3entry.Segments.Add(segment);
        }

        void EncryptedFileCopy(FileStream file, Xp3Entry xp3entry, Stream output, bool compress)
        {
            if (file.Length > int.MaxValue)
                throw new FileSizeException();

            using (var map = MemoryMappedFile.CreateFromFile(file, null, 0,
                    MemoryMappedFileAccess.Read, null, HandleInheritability.None, true))
            {
                uint unpacked_size = (uint)file.Length;
                xp3entry.UnpackedSize = (uint)unpacked_size;
                xp3entry.Size = (uint)unpacked_size;
                using (var view = map.CreateViewAccessor(0, unpacked_size, MemoryMappedFileAccess.Read))
                {
                    var segment = new Xp3Segment
                    {
                        IsCompressed = compress,
                        Offset = output.Position,
                        Size = unpacked_size,
                        PackedSize = unpacked_size,
                    };
                    if (compress)
                    {
                        output = new ZLibStream(output, CompressionMode.Compress, CompressionLevel.Level9, true);
                    }
                    unsafe
                    {
                        byte[] read_buffer = new byte[81920];
                        byte* ptr = view.GetPointer(0);
                        try
                        {
                            var checksum = new Adler32();
                            bool hash_after_crypt = xp3entry.Cipher.HashAfterCrypt;
                            if (!hash_after_crypt)
                                xp3entry.Hash = checksum.Update(ptr, (int)unpacked_size);
                            int offset = 0;
                            int remaining = (int)unpacked_size;
                            while (remaining > 0)
                            {
                                int amount = Math.Min(remaining, read_buffer.Length);
                                remaining -= amount;
                                Marshal.Copy((IntPtr)(ptr + offset), read_buffer, 0, amount);
                                xp3entry.Cipher.Encrypt(xp3entry, offset, read_buffer, 0, amount);
                                if (hash_after_crypt)
                                    checksum.Update(read_buffer, 0, amount);
                                output.Write(read_buffer, 0, amount);
                                offset += amount;
                            }
                            if (hash_after_crypt)
                                xp3entry.Hash = checksum.Value;
                        }
                        finally
                        {
                            view.SafeMemoryMappedViewHandle.ReleasePointer();
                            if (compress)
                            {
                                var dest = (output as ZLibStream).BaseStream;
                                output.Dispose();
                                segment.PackedSize = (uint)(dest.Position - segment.Offset);
                                xp3entry.Size = segment.PackedSize;
                            }
                            xp3entry.Segments.Add(segment);
                        }
                    }
                }
            }
        }

        uint CheckedCopy(Stream src, Stream dst)
        {
            var checksum = new Adler32();
            var read_buffer = new byte[81920];
            for (; ; )
            {
                int read = src.Read(read_buffer, 0, read_buffer.Length);
                if (0 == read)
                    break;
                checksum.Update(read_buffer, 0, read);
                dst.Write(read_buffer, 0, read);
            }
            return checksum.Value;
        }

        bool ShouldCompressFile(Entry entry)
        {
            if ("image" == entry.Type || "archive" == entry.Type)
                return false;
            if (entry.Name.HasExtension(".ogg"))
                return false;
            return true;
        }

        ICrypt GuessCryptAlgorithm(ArcView file)
        {
            var title = FormatCatalog.Instance.LookupGame(file.Name);
            if (string.IsNullOrEmpty(title))
                title = FormatCatalog.Instance.LookupGame(file.Name, @"..\*.exe");
            if (string.IsNullOrEmpty(title))
                return null;
            ICrypt algorithm;
            if (!KnownSchemes.TryGetValue(title, out algorithm))
            {
                if (NoCryptTitles.Contains(title))
                    algorithm = NoCryptAlgorithm;
                else
                    algorithm = null; //ダイアログ表示
            }
            m_title = title;
            return algorithm;
        }

        static Xp3Scheme KiriKiriScheme = new Xp3Scheme
        {
            KnownSchemes = new Dictionary<string, ICrypt>(),
            NoCryptTitles = new HashSet<string>()
        };

        public static IDictionary<string, ICrypt> KnownSchemes
        {
            get { return KiriKiriScheme.KnownSchemes; }
        }

        public static ISet<string> NoCryptTitles
        {
            get { return KiriKiriScheme.NoCryptTitles; }
        }

        public override ResourceScheme Scheme
        {
            get { return KiriKiriScheme; }
            set { KiriKiriScheme = (Xp3Scheme)value; }
        }
    }

    internal class Xp3Stream : Stream
    {
        ArcView m_file;
        Xp3Entry m_entry;
        IEnumerator<Xp3Segment> m_segment;
        Stream m_stream;
        long m_offset = 0;
        bool m_eof = false;

        public override bool CanRead { get { return !disposed; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { return m_entry.UnpackedSize; } }
        public override long Position
        {
            get { return m_offset; }
            set { throw new NotSupportedException("Xp3Stream.Position not supported."); }
        }

        public Xp3Stream(ArcView file, Xp3Entry entry)
        {
            m_file = file;
            m_entry = entry;
            m_segment = entry.Segments.GetEnumerator();
            NextSegment();
        }

        private void NextSegment()
        {
            if (!m_segment.MoveNext())
            {
                m_eof = true;
                return;
            }
            if (null != m_stream)
                m_stream.Dispose();
            var segment = m_segment.Current;
            var segment_size = segment.IsCompressed ? segment.PackedSize : segment.Size;
            m_stream = m_file.CreateStream(segment.Offset, segment_size);
            if (segment.IsCompressed)
                m_stream = new ZLibStream(m_stream, CompressionMode.Decompress);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (!m_eof && count > 0)
            {
                int read = m_stream.Read(buffer, offset, count);
                if (0 != read)
                {
                    if (m_entry.IsEncrypted)
                        m_entry.Cipher.Decrypt(m_entry, m_offset, buffer, offset, read);                    
                    m_offset += read;
                    total += read;
                    offset += read;
                    count -= read;
                }
                if (0 != count)
                    NextSegment();
            }
            return total;
        }

        public override int ReadByte()
        {
            int b = -1;
            while (!m_eof)
            {
                b = m_stream.ReadByte();
                if (-1 != b)
                {
                    if (m_entry.IsEncrypted)
                        b = m_entry.Cipher.Decrypt(m_entry, m_offset++, (byte)b);
                    break;
                }
                NextSegment();
            }
            return b;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException("Xp3Stream.Seek method is not supported");
        }

        public override void SetLength(long length)
        {
            throw new NotSupportedException("Xp3Stream.SetLength method is not supported");
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException("Xp3Stream.Write method is not supported");
        }

        public override void WriteByte(byte value)
        {
            throw new NotSupportedException("Xp3Stream.WriteByte method is not supported");
        }

        #region IDisposable Members
        bool disposed = false;
        protected override void Dispose(bool disposing)
        {
            if (!disposed)
            {
                if (disposing)
                {
                    if (null != m_stream)
                        m_stream.Dispose();
                    m_segment.Dispose();
                }
                disposed = true;
                base.Dispose(disposing);
            }
        }
        #endregion
    }

    /// <summary>
    /// Class that maps file hashes to filenames.
    /// </summary>
    internal sealed class FilenameMap : IDisposable
    {
        Dictionary<uint, string> m_hash_map = new Dictionary<uint, string>();
        Dictionary<string, string> m_md5_map = new Dictionary<string, string>();
        MD5 m_md5 = MD5.Create();
        StringBuilder m_md5_str = new StringBuilder();

        public int Count { get { return m_md5_map.Count; } }

        public void Add(uint hash, string filename)
        {
            if (!m_hash_map.ContainsKey(hash))
                m_hash_map[hash] = filename;

            m_md5_map[GetMd5Hash(filename)] = filename;
        }

        public void AddShortcut(string shortcut, string filename)
        {
            m_md5_map[shortcut] = filename;
        }

        public string Get(uint hash, string md5)
        {
            string filename;
            if (m_md5_map.TryGetValue(md5, out filename))
                return filename;
            if (m_hash_map.TryGetValue(hash, out filename))
                return filename;
            return md5;
        }

        string GetMd5Hash(string text)
        {
            var text_bytes = Encoding.Unicode.GetBytes(text.ToLowerInvariant());
            var md5 = m_md5.ComputeHash(text_bytes);
            m_md5_str.Clear();
            for (int i = 0; i < md5.Length; ++i)
                m_md5_str.AppendFormat("{0:x2}", md5[i]);
            return m_md5_str.ToString();
        }

        bool _disposed = false;
        public void Dispose()
        {
            if (!_disposed)
            {
                m_md5.Dispose();
                _disposed = true;
            }
        }
    }

    [Export(typeof(ResourceAlias))]
    [ExportMetadata("Extension", "ANM")]
    [ExportMetadata("Target", "TXT")]
    public class AnmFormat : ResourceAlias { }

    [Export(typeof(ResourceAlias))]
    [ExportMetadata("Extension", "ASD")]
    [ExportMetadata("Target", "TXT")]
    public class AsdFormat : ResourceAlias { }
}
