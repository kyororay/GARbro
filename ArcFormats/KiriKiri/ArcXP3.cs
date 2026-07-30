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
using GameRes.Formats.Misc;

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
            var decoder = base.OpenImage(arc, entry);
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
                "やりなおしクランクイン",
                "ダウニャーさんと飼い主くん",
                "飛べない蝶のバレンタイン",
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
            if (false)
            {
                //return new HitorenCrypt();
                //return new InreCrypt();
                //return new FlapCrypt();
                //return new HogeCrypt();
                //return new AkabeiCrypt(0);
                //return new OkibaCrypt();
                //return new HighRunningCrypt();
                return new MiburoCrypt();

                return new CxEncryption(new CxScheme
                {
                    Mask = 0x000002B2,
                    Offset = 0x00000488,
                    PrologOrder = new byte[] { 2, 1, 0 },
                    OddBranchOrder = new byte[] { 2, 0, 3, 5, 1, 4 },
                    EvenBranchOrder = new byte[] { 1, 2, 0, 6, 3, 5, 4, 7 },
                    ControlBlock = new uint[] { 0x9C91BADF, 0x8B8F868D, 0xDF919096, 0x8B91909C, 0xDF93908D, 0x9C90939D, 0xD2D2DF94, 0x9E8BACDF, 0x9E9C968B, 0xDF869393, 0x9BDF8D90, 0x929E9186, 0x939E9C96, 0xDFD38693, 0x9A8D969B, 0x86938B9C, 0xDF8D90DF, 0x969B9196, 0x8B9C9A8D, 0xDFD38693, 0x91968C8A, 0x978BDF98, 0x8FDF8C96, 0x8D98908D, 0x9EDF929E, 0x90D09B91, 0x939DDF8D, 0xDF949C90, 0x92908D99, 0x978B90DF, 0x8FDF8D9A, 0x8D98908D, 0xDF8C929E, 0x93939688, 0xDF9A9DDF, 0x9A939396, 0xDF939E98, 0x8BDF869D, 0x93DF9A97, 0x919A9C96, 0x9EDF9A8C, 0x9A9A8D98, 0x8B919A92, 0x4E7DDFD1, 0x897C337D, 0xB07C727C, 0x7F7C767C, 0x8A7C1D7D, 0x9D7C727C, 0x0F7DB17C, 0x3C6FBE7E, 0x3A7DB66C, 0x157D5F7D, 0xB66C516C, 0x5F7D3A7D, 0xBE7E157D, 0x256F436D, 0x3A7DB66C, 0x157D5F7D, 0x256F2B75, 0x3A7DB66C, 0x157D5F7D, 0x436EBE7E, 0x897C337D, 0xB07C727C, 0x7F7C767C, 0x187D567D, 0x5D7D8F68, 0x4E7D167D, 0x327D397D, 0xBC7C767C, 0x6C7CA57C, 0x367DA77C, 0x177D197D, 0x497D2974, 0x157D187D, 0x5D7D3B7D, 0x487D237D, 0xBCF5BD7E, 0x8D868F90, 0x8B979896, 0xDFD6BCD7, 0xC6CFCFCD, 0xA7D2AEDF, 0x9393BEDF, 0x9896ADDF, 0xDF8C8B97, 0x9A8C9AAD, 0x9B9A898D, 0x6EF5F5D1, 0x6A1D7275, 0x7D087149, 0x7D407D32, 0x7D3E7D1A, 0x7D317D39, 0x7C177D56, 0x7CA47EAE, 0x7DBB727F, 0x69BA7E54, 0x72856E30, 0x7D547DBB, 0x7D327D3A, 0x7D167D5F, 0x7D157D50, 0x7DBE7E38, 0x6A527D4D, 0x7D356D7E, 0x6B157733, 0x6DB66C35, 0x7E8E7195, 0x7D9C7E9C, 0x7D3D7D33, 0x7D177D1F, 0x7D3E7D41, 0xF5BD7E42, 0x748B70F5, 0x72227D87, 0x7D1C733A, 0x7E056C33, 0x7EAE7CBE, 0x7D7F7CA4, 0x7D796D33, 0x6FBD6D3A, 0x7D4B7553, 0x7CAD7C1D, 0x7C7A7C81, 0x7EAF7C95, 0x7CA97CA4, 0x7D6C7C78, 0x7D1D7D0F, 0x7D547D17, 0x7D3B7D3E, 0x7D237D4A, 0x7D4E7D5B, 0x75367D39, 0x75BF7414, 0x750F7D4B, 0x7D597D90, 0x71496A42, 0x7E327D08, 0x75A96FBE, 0x7D057588, 0x7D5D7D39, 0x6F19745B, 0x7D177D27, 0x756B6A3A, 0x710F7D77, 0x7D207D91, 0x7D5B7D19, 0x6F777339, 0x7D487DAC, 0xF5BD7E16, 0x5E691071, 0x53703A7D, 0x0F7D1F6F, 0x497D567D, 0x496A167D, 0x327D0871, 0x8373496A, 0x297D6B6A, 0x9372397D, 0x3B7D597D, 0x427D5D7D, 0x9C7E557D, 0x74739C7E, 0xBE7E3874, 0x207D7773, 0x337D427D, 0x1577327D, 0x1E776571, 0x337D5D7D, 0x6C887EDF, 0x6A83739B, 0x7E877E6B, 0x7D447DBD, 0x7E327D15, 0x6C496FBE, 0x6D107694, 0x7C337D48, 0x7CB47C70, 0x7D827C82, 0x7D916E3A, 0x7D157D18, 0x7EAE7C42, 0x737F7CA4, 0x6A79745B, 0x7D417D6B, 0x7E427D3E, 0x7DF5F5BD, 0x6A337D44, 0x7D45716B, 0x7D5F7D55, 0x7D397D16, 0x715B7D5D, 0x76177D19, 0x684A7D0C, 0x7D176DA3, 0x72737433, 0x7D3671A5, 0x7D8C7229, 0x7D3B7D3E, 0x7D167D22, 0x7E9C7E39, 0x7D377D9C, 0x7E397D0E, 0x716B6ABE, 0x70337D45, 0x7D567D4F, 0x6F487618, 0x6A557D0D, 0x70547D53, 0x7C487D90, 0x7C897C91, 0x7C6C7C95, 0xF5B67EB0, 0x567D4A7D, 0xBE7E1F7D, 0x736D6D6C, 0x796D337D, 0x187D567D, 0x337D7868, 0x68705270, 0x4473557D, 0xBE7E157D, 0xA47EAE7C, 0xBF747F7C, 0x187D567D, 0x2E7DB36F, 0xAF7C167D, 0x8A7CA47E, 0x0F7D747C, 0x0871496A, 0x4674337D, 0x256F367D, 0x4A7D4E6E, 0x4A7D3B7D, 0x5B7D237D, 0xF5F5B67E, 0x50712C77, 0x47710F7D, 0x427D3E7D, 0x0871496A, 0x2569557D, 0x207D9075, 0x397D167D, 0x9C7E9C7E, 0x4E7D447D, 0x8C6A327D, 0x9C748971, 0x997C377D, 0xA17CA87C, 0x707C747C, 0x747CA47E, 0xBD7E977C, 0x7D257DF5, 0x7D1D7D0E, 0x6B397D17, 0x735D7D6D, 0x6A557D71, 0x7D567D7D, 0x7D16692B, 0x714C7433, 0x73BE7E45, 0x7D157D44, 0x7D337D42, 0x7CB87C32, 0x7C997C73, 0x7CA47EBD, 0x7D397D79, 0x775B7D5D, 0x7D7D6905, 0x7D3E7D41, 0xF5BD7E42, 0x6870216B, 0x0577327D, 0x187D7D69, 0x527D4A7D, 0x0871496A, 0x686B367D, 0xBE7E177D, 0x936F8A7E, 0x8168337D, 0x997CA269, 0xA17CA47E, 0xA8700F7D, 0x3B7D207D, 0x4A7D8168, 0x897E5D7D, 0x1775397D, 0xBD7E5B7D, 0x5671F5F5, 0x337D556A, 0xBA755D6F, 0xBE74367D, 0x3B7D3E7D, 0x427D547D, 0x0871496A, 0x9873337D, 0xAE7C2E6E, 0x7F7CA47E, 0x327DBF74, 0x5D7DBE7E, 0x337D3D7D, 0x367D2B75, 0x356A567D, 0x367D576A, 0x5D6E0376, 0x157D4C7D, 0x5D7D3B7D, 0xBD7E427D, 0x7D447DF5, 0x7C827C33, 0x7D6C7CA9, 0x7E9C7E32, 0x7D936F9C, 0x69816833, 0x7C0F7DA2, 0x7CA47EAE, 0x7D44767F, 0x7D167D48, 0x7D5D7D39, 0x887EDF5B, 0x6C7C8F7C, 0xB67C977C, 0x877E767C, 0x7DF5BD7E, 0x6A337D4E, 0x7489718C, 0x7C377D9C, 0x7CA47EAE, 0x7DBF747F, 0x7CBE7E55, 0x7C727C8E, 0x7D6C7CBC, 0x7D407D42, 0x7D55750F, 0x7D617254, 0x7E9C7E22, 0x6E15779C, 0x7D387D33, 0x7C377D0E, 0x7C9D7C8D, 0x7CA17CB0, 0x7C987CBC, 0x6E0F7D74, 0x70177D91, 0x7D4A7D90, 0x7D5D7D3B, 0x7D337D52, 0xDF187D1D, 0xDEF5C0DE, 0x358CF2EB, 0xBB7CBDB1, 0x24B1C11B, 0xC19E467E, 0x4D75B206, 0x3545E17E, 0x7F0847CC, 0x7BD44E23, 0xA2D060CF, 0x997C4826, 0xC5C1ABF9, 0xDF43B9D9, 0x180D4451, 0x7221D75B, 0x4AF01AF4, 0xA4D39210, 0xD0FBDFA6, 0x7C5C25DA, 0x26BD3EDA, 0x52FAC81F, 0x553785A6, 0xB3628AA6, 0x6680D57A, 0xFDF167D9, 0x02052385, 0x39C61263, 0xCB8897BA, 0x825C5845, 0x6B56BD16, 0x074ECBEA, 0x6D162F46, 0x17834B5C, 0xE50A4C7A, 0xE44E381D, 0xE5C28340, 0x96FC2FA9, 0x5E0A32F1, 0x37285CAB, 0xE393A9BE, 0x2B0D9976, 0xD3AB6705, 0x1AE3ADBB, 0x8EF8F73F, 0x719A0B14, 0xCC5E17A0, 0xE92FE55C, 0x8F7FA0B5, 0xB3F924F2, 0x90AF6A8A, 0xE962F10B, 0x125FE6C6, 0x1F10EBCF, 0x5138797A, 0x9B4CCF59, 0x8C010C94, 0xE2720201, 0x107C88F0, 0xF66441A8, 0xA3AEB191, 0x9D66668C, 0x86EBE14F, 0x8B56AE7D, 0xA568D207, 0x4B0D2E56, 0x2EA0F305, 0xE4EC09AD, 0x72C38F85, 0x0E1E3F69, 0x6C8615AF, 0x34602F0E, 0x70069242, 0x560B158E, 0xC42C3606, 0x84ACCB8A, 0x2C6A38C7, 0x54394800, 0xB45247D3, 0x3868BEED, 0x8221EA27, 0x2C5B0CAF, 0xA8D5475C, 0xFE4E4A61, 0xE85CA0A6, 0x0FB2FDD9, 0xF9D99522, 0x2B49C367, 0x8801A1F3, 0xB3D424BF, 0xCDC29D19, 0xBB7CC20C, 0x6DFC254D, 0xB9B1F666, 0x040C896F, 0xC7C6D0C6, 0xCB49AE8D, 0x780D9EB0, 0x8CD3548F, 0xDD0B9D63, 0xE92A9192, 0xA6788695, 0x853B98F5, 0x0666A60F, 0x1374301D, 0xC806D25D, 0x04A58A3A, 0xB3EB70AB, 0x8AF11C89, 0xE9F8FF42, 0x2EA0A4F2, 0x1E942FC4, 0xA37EC3F3, 0x2D6E579F, 0xF6424740, 0x79867DC2, 0xF8CAB741, 0x509ADCB2, 0x2B317B88, 0xEFEC03D4, 0x295BF3BD, 0x899DBF3A, 0xE60D6E88, 0x9588FFF4, 0xF819D335, 0xD4F74659, 0x91A1564C, 0x4683E4C7, 0xFFFE22D1, 0x9C594810, 0xC158DE20, 0x2A72F289, 0x6AECB97C, 0xDD121154, 0x26122AFC, 0x9DED9192, 0x6B8531C7, 0xE3235D6D, 0x102F3376, 0x1706EAB4, 0xA9D12069, 0x7C2A3E00, 0x187B6B80, 0x58E807A8, 0xB0AB3D2F, 0x95E91418, 0xD7DC761F, 0xE701A795, 0x90FFBB0C, 0xFD6B77E1, 0xC345B401, 0x88CD2CE7, 0x0D524FD6, 0x15C3AF17, 0x9B139A72, 0x12BB29C7, 0xE29A690E, 0x6D2C172B, 0xEE91524F, 0xAA235497, 0x5D60DB36, 0x36C76B9C, 0x4765FE3E, 0xEB37D722, 0x9001B458, 0x8071207D, 0x5D403867, 0x68ED3DFF, 0x15604324, 0xF138C400, 0xF5D06E8E, 0x6EF687AB, 0xF681D38D, 0x5AA792E0, 0x4F1C0986, 0x24A914CF, 0xA74D53A0, 0x7F40D060, 0x2A0AA39D, 0x6825F9BF, 0xED90F9EC, 0x477DAC5B, 0x21CB1211, 0x8381EF26, 0x84DD92E7, 0xD5CB507D, 0x998A0E7D, 0xD25BACEB, 0xC925C61F, 0x828713E3, 0x17301D99, 0xCE3C201D, 0x84B28833, 0xC3AE4549, 0xDE31A26E, 0x7CEFCAF6, 0x7BDD314D, 0xF00ADE53, 0xB9AC301F, 0xC24C79BA, 0x7D98142A, 0xF3E915A1, 0x138BAAEC, 0x759E95A5, 0xDFC5B4DB, 0xE4458B88, 0xB3373C88, 0x419F246D, 0x45DC820B, 0xCEAB697D, 0x4D21DB21, 0x8A081026, 0x8E558057, 0x5BA408C8, 0x714920C0, 0x1D1FE7D7, 0x48F11D54, 0x788C1441, 0x8E28ECDE, 0x6132B815, 0x4B30B57C, 0x0F2E2BC4, 0x4D15A495, 0x53B3C73E, 0x11BAD7BF, 0x4162021E, 0x83C1BA36, 0xCB30B73B, 0x18F7DD72, 0x78960A2D, 0x372A0046, 0x9BEEC22A, 0xA6065320, 0xB4EF5EC2, 0x46F9D364, 0x9B166208, 0x6275EF7B, 0x350AB51E, 0xA083D40E, 0x6CFE04CB, 0x8A4CDB07, 0x1B1F4F34, 0x9D87EDCF, 0x090EB9B0, 0xB0621D95, 0x5638681A, 0xF4D5AE93, 0x163CB300, 0x2A853A45, 0x998DA6A9, 0x38A3BC1D, 0x4CF1B90C, 0xB76397CD, 0x95458BD8, 0x3B155716, 0x14803492, 0x23D175BF, 0xAE3CE291, 0xF62CB11D, 0x49431E24, 0xE645E75A, 0x97073333, 0xE5A29795, 0xE2CC8635, 0x7A3D7B4B, 0x95EFADDA, 0x9F6EA7FC, 0x3EF3BB6C, 0x6D5783CF, 0x2E6B1F48, 0x26B50E4D, 0x638DD21D, 0x62952ABB, 0xC753DE5C, 0xF924E610, 0x243011C3, 0x218F5FEA, 0x990D3CDC, 0xD676C810, 0x796F8267, 0x790ECA1C, 0x6841F68C, 0x46B1A070, 0xB5915F86, 0xC9D19828, 0xC085A3E2, 0x0E4853F2, 0xB187E71C, 0x58BA1128, 0x880FA448, 0x33F7F7A9, 0xD15ACCFB, 0xD3BDFCAE, 0x4BE5E53A, 0x5349E142, 0x7399C341, 0x99E51E73, 0x63BF9170, 0x1902F609, 0xCA4C7055, 0xEDE0FE50, 0x9B6E572C, 0x410526E9, 0x64123E4A, 0xBBDD7E3D, 0xEFBB25CC, 0xBE4C9588, 0x195349C6, 0x306F129E, 0x3E7B6200, 0xF5318E8D, 0xE74DF111, 0xEF032B84, 0xE3B1B00E, 0x1508377A, 0xA64747E8, 0xD0A119D2, 0x2E921EEA, 0x0881A591, 0xB7605FF3, 0xCE3C8972, 0xCEB373E9, 0x7959FA5F, 0x83BD41C3, 0x7C554A83, 0x12386206, 0x7B554FF2, 0x3DCD47B4, 0x202538DB, 0x1B0316FA, 0x43BA7E73, 0xFFB5CEDA, 0x6286DE75, 0x49EAEBF0, 0x80A7ED1B, 0xAEC3E405, 0x15385651, 0x7290B1C7, 0xCBE4389E, 0x275B7E9E, 0x9A5C47A6, 0x752636AF, 0x309C9EF0, 0x6D6C1E95, 0xDD5AAA5F, 0xB831C457, 0x520F0A74, 0xAF098E9A, 0x69FBE411, 0x0E12EE34, 0x235A6C16, 0xB41F07FE, 0x232EAA82, 0x7CFD8F1A, 0x448727C5, 0x3B81E9C4, 0x7F02806E, 0xE9F2536F, 0xEE2D4C5A, 0xAE1D6074, 0xFC10F74C, 0x0B74CBEC, 0x7B1BAB8E, 0x9BC52291, 0xB98FD1A1, 0x99B5E83A, 0x40522067, 0x2FE61319, 0xAC4DAC3E, 0xEC459125, 0x7E568B76, 0x8A417C31, 0x5F194967, 0xEDC5A90D, 0x64BE0490, 0xC863380B, 0xE0085556, 0x84C9900C, 0x3D9ABB62, 0xED0DE400, 0xB4F2D92C, 0x5D3D3D48, 0xA1A495EA, 0x47AA91A2, 0xF454534E, 0x9243B701, 0xD5386DE2, 0x1ABD4133, 0x7B3C4B17, 0x501B794B, 0x3BA6C833, 0x2400DD67, 0xADFF898A, 0x92B770CE, 0xD13EEC62, 0x8D93BCFF, 0x5F52547C, 0x58A45E31, 0x613B7BD3, 0x444119B9, 0xFF12139A, 0xBD12A13E, 0xB4F35E79, 0x816A94E5, 0xBA6C29D8, 0x427F960F, 0x704C1173, 0xB8C7DCF5, 0xC7D3797E, 0x76ECEFD4, 0x038F697D, 0x70CA35CF, 0xCE2EA668, 0x1905FC6A, 0xADFA1062, 0xCCF96777, 0x60F5329B, 0x8D5B9543, 0x2870EABC, 0xFA38CBC6, 0x8E775913, 0x133BE1DF, 0x5DA5AF20, 0x0B2A3C73, 0x21851CDB, 0x7E223015, 0x3F747EC3, 0xB0B4E8BB, 0xD49477EA, 0xD18D59F9, 0x3F19D067, 0xDA09C39A, 0xCBFD860B, 0x816BD254, 0xCC56BFCB, 0xCB3AE051, 0x2BADB23F, 0x54A98E3E, 0xFFCAD01D, 0xF92EA69C, 0xCE12766B, 0x8B23D285, 0x82261EE7, 0xF4743050, 0xD524AA0C, 0x4125D722, 0xF5AEC9D3, 0x64F10800, 0x846874E3, 0x6B9C6B07, 0x7D48ABD8, 0x7CA5F043, 0x552AD9F6, 0x11C49A02, 0xE362F2D9, 0x551568FD, 0x744ADE86, 0xCD59C937, 0x56593219, 0xF3BE765C, 0x85CCB694, 0xE747E839, 0xB922DA27, 0x3186E184, 0x10840228, 0x198981EF, 0x2450FBD3, 0xA4AA92FA, 0xEFF074E2, 0x3E19EB8F, 0xD3D39116, 0x992FC904, 0x458B2AC6, 0x73426663, 0x67947A59, 0x06D243B8, 0x18FBB1DC, 0x52408CB4, 0xB959E0A5, 0x6A29F455, 0x0041C707, 0xAB5BF6C2, 0xCE44CE3C, 0xFCCC3130, 0x6EFC796A, 0x99706259, 0x5C12CB93, 0xDDA3E36D, 0xC6754D51, 0x4C964BBB, 0x726F43B9, 0x95C7B2E5, 0xDF3E105E, 0x64D89918, 0x2E5BD79A, 0x874C3E2E, 0x099E9836, 0x9F5FBBD5, 0x19CA2135, 0xCC1F47F5, 0x550CBC06, 0xB1A49F27, 0x19E960E3, 0xB85FDFF5, 0xFAB460F2, 0x4AD19945, 0xF20E1CD8, 0x472C9DED, 0x52C79A21, 0xCAFBDCA9, 0x75CFE07B, 0x7E3497DB, 0x7058DB7F, 0xC96F63FB, 0x2D9AC26F, 0x6AC35C54, 0x7801E582, 0xA732653C, 0x459D0CF4, 0x113B78A8, 0x0399F881, 0x5AFCC316, 0xF33B20A9, 0xD365EB6D, 0xE242933D, 0x9735793A, 0xF95B9746, 0x9823ABCA, 0x3770147B, 0x75A58F5B, 0x092B7E5A, 0xFFFD4CAA, 0xAB979924, 0x5B2206FC, 0x2E935CD9, 0xE2860772, 0x29827998, 0x3E434AD2, 0xD14C109A, 0x44342954, 0x5430121E, 0xDA8FF1C3, 0x682769BE, 0x2DAB2178, 0xF3F35B42, 0xC52CF029, 0xA70274F7, 0x331F6FB3, 0x115F4DD4, 0x3A90E2EE, 0x5ED57132, 0x6254F662, 0xAC8FDB5E, 0x45E23FBE, 0xE757B83D, 0x9D2F708D, 0xFB1EBDAA, 0xE06518C7, 0xFB28D6B1, 0xA4BF43A2, 0xD690860C, 0x36D08A59, 0x54C62166, 0x7D3DBEFE, 0xAD67D79E, 0x57E87D95, 0xF06825E0, 0x575770C5, 0x216CD49C, 0xCE21D42F, 0xAF03F545, 0x09E49954, 0x3BDCC91B, 0x1BA1BC8B, 0x41376943, 0x05F2FD44, 0x26D57835, 0x1C6A5D70, 0xB9AFD359, 0xB428D616, 0x560EDF08, 0xD193E8C7, 0x2123E805, 0x2AA22C65, 0xADEAA6AB, 0x0AEC4DBA, 0x4A1646DF, 0x4F390D47, 0x17C7BD89, 0x5ABD8264, 0x42F85291, 0x6867B76A, 0xE6236A92, 0xC2160A93, 0xD502D740, 0x0343CED7, 0xDC91876C, 0x551BF568, 0x3D6DC64E, 0xD39A8671, 0x81E7BBA5, 0xA8BF2982, 0xCE68398E, 0xB66FD7A2, 0xA5601C5E, 0xBED569C6, 0x2B634EDE, 0x2580ABC2, 0x29D88FCD, 0xCCFED5F7, 0xA2594C9B, 0x8E921130, 0xB402C7B3, 0x68ADB5EC, 0x1FBC5F0F, 0xD81F67E4, 0x24D28631, 0x78547B4E, 0x2A101B63, 0x576A11B3, 0x36340E81, 0xE514C636, 0xCA80DB0B, 0xA3622409, 0x22A13E20, 0xB66DFF11, 0xED48E00C, 0xCE75981C, 0xF36D7454, 0x85F3CA9B, 0x74D890A8, 0x64E4F98C, 0x176A8E98, 0xEAA5F472, 0xDAB37B7D, 0xD94D9585, 0x2FE159FA, 0x40E468FA, 0x679506F8, 0x693C2A8A, 0x183FED52, 0x53BF18E4, 0x5F84CE44, 0x9B236D38, 0x2CB2B6EA, 0xFEE7F6B5, 0x678745BA, 0x2CFC26C6, 0xFE6DAAB3, 0xECBD5783, 0x684AF51E, 0x74394AE7, 0x452841F6, 0x4FBFF265, 0xA1F7AE56, 0x116357B3, 0x8CFF11FE, 0xF00DC597, 0x78697733, 0x1E4BEB31, 0xC28FAF1C, 0x0115D922, 0xC12F8DF4, 0x294BE72F, 0x9FEBAD9C, 0xC1C72555, 0xB118C716, 0xFF32BD34, 0xAA58DD99, 0xA0C1656D, 0x719F2F35, 0xF23A6601, 0x915171BA, 0xAF81E5DD, 0xB0C9465A },
                    TpmFileName = null
                });
            }

            var alg = GuessCryptAlgorithm(file);
            if (null != alg)
                return alg;
            var options = Query<Xp3Options>(arcStrings.XP3EncryptedNotice);
            m_title = new WidgetXP3().Scheme.SelectedValue as string;

            return options.Scheme;
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
