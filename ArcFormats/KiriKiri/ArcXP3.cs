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
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using GameRes.Compression;
using GameRes.Formats.GUI;
using GameRes.Formats.Misc;
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

        //プルダウンメニューの最初に表示される共通暗号化方式の一覧
        public static Dictionary<string, ICrypt> CommonKeys = new Dictionary<string, ICrypt>{
            {arcStrings.ArcNoEncryption, NoCryptAlgorithm}, //暗号化無し
            {"[" + arcStrings.Common + "] AbeliaCrypt", new AbeliaCrypt()},
            {"[" + arcStrings.Common + "] AlteredPinkCrypt", new AlteredPinkCrypt()},
            {"[" + arcStrings.Common + "] AppliqueCrypt", new AppliqueCrypt()},
            {"[" + arcStrings.Common + "] ChuSinguraCrypt", new ChuSinguraCrypt()},
            {"[" + arcStrings.Common + "] DameganeCrypt", new DameganeCrypt()},
            {"[" + arcStrings.Common + "] DieselmineCrypt", new DieselmineCrypt()},
            {"[" + arcStrings.Common + "] DualTailCrypt", new DualTailCrypt()},
            {"[" + arcStrings.Common + "] ExaCrypt", new ExaCrypt()},
            {"[" + arcStrings.Common + "] FateCrypt", new FateCrypt()},
            {"[" + arcStrings.Common + "] FestivalCrypt", new FestivalCrypt()},
            {"[" + arcStrings.Common + "] FinishCrypt", new FinishCrypt()},
            {"[" + arcStrings.Common + "] FlyingShineCrypt", new FlyingShineCrypt()},
            {"[" + arcStrings.Common + "] HaikuoCrypt", new HaikuoCrypt()},
            {"[" + arcStrings.Common + "] HashCrypt", new HashCrypt()},
            {"[" + arcStrings.Common + "] HibikiCrypt", new HibikiCrypt()},
            {"[" + arcStrings.Common + "] HighRunningCrypt", new HighRunningCrypt()},
            {"[" + arcStrings.Common + "] HoneyCombCrypt", new HoneyCombCrypt()},
            {"[" + arcStrings.Common + "] HybridCrypt", new HybridCrypt()},
            {"[" + arcStrings.Common + "] Kano2Crypt", new Kano2Crypt()},
            {"[" + arcStrings.Common + "] MadoCrypt", new MadoCrypt(0)},
            {"[" + arcStrings.Common + "] MiburoCrypt", new MiburoCrypt()},
            {"[" + arcStrings.Common + "] MizukakeCrypt", new MizukakeCrypt()},
            {"[" + arcStrings.Common + "] NatsupochiCrypt", new NatsupochiCrypt()},
            {"[" + arcStrings.Common + "] NekoWorksCrypt", new NekoWorksCrypt(Enumerable.Repeat<byte>(0,31).ToArray())},
            {"[" + arcStrings.Common + "] NephriteCrypt", new NephriteCrypt()},
            {"[" + arcStrings.Common + "] OkibaCrypt", new OkibaCrypt()},
            {"[" + arcStrings.Common + "] PinPointCrypt", new PinPointCrypt()},
            {"[" + arcStrings.Common + "] PoringSoftCrypt", new PoringSoftCrypt()},
            {"[" + arcStrings.Common + "] PuCaCrypt", new PuCaCrypt()},
            {"[" + arcStrings.Common + "] RoseTiaraCrypt", new RoseTiaraCrypt()},
            {"[" + arcStrings.Common + "] SeitenCrypt", new SeitenCrypt()},
            {"[" + arcStrings.Common + "] SourireCrypt", new SourireCrypt()},
            {"[" + arcStrings.Common + "] SyangrilaSmartCrypt", new SyangrilaSmartCrypt()},
            {"[" + arcStrings.Common + "] TokidokiCrypt", new TokidokiCrypt()},
            {"[" + arcStrings.Common + "] YuzuCrypt", new YuzuCrypt()},
        };

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
                "おしかけ奥様と、キミの嘘",
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
#pragma warning disable CS0162
            if (false)
            {
                //return new HitorenCrypt();
                //return new InreCrypt();
                //return new FlapCrypt();
                //return new HogeCrypt();
                //return new AkabeiCrypt(0);
                //return new OkibaCrypt();
                //return new HighRunningCrypt();
                //return new AppliqueSisterCrypt();
                //return new CroixCrypt();
                //return new RoseTiaraCrypt();
                //return new AbeliaCrypt();
                //return new FinishCrypt();
                return new DualTailCrypt();

                return new CxEncryption(new CxScheme
                {
                    Mask = 0x00000213,
                    Offset = 0x00000473,
                    PrologOrder = new byte[] { 1, 0, 2 },
                    OddBranchOrder = new byte[] { 1, 4, 0, 3, 2, 5 },
                    EvenBranchOrder = new byte[] { 5, 4, 1, 0, 7, 6, 2, 3 },
                    ControlBlock = new uint[] { 0x9C91BADF, 0x8B8F868D, 0xDF919096, 0x8B91909C, 0xDF93908D, 0x9C90939D, 0xD2D2DF94, 0x9E8BACDF, 0x9E9C968B, 0xDF869393, 0x9BDF8D90, 0x929E9186, 0x939E9C96, 0xDFD38693, 0x9A8D969B, 0x86938B9C, 0xDF8D90DF, 0x969B9196, 0x8B9C9A8D, 0xDFD38693, 0x91968C8A, 0x978BDF98, 0x8FDF8C96, 0x8D98908D, 0x9EDF929E, 0x90D09B91, 0x939DDF8D, 0xDF949C90, 0x92908D99, 0x978B90DF, 0x8FDF8D9A, 0x8D98908D, 0xDF8C929E, 0x93939688, 0xDF9A9DDF, 0x9A939396, 0xDF939E98, 0x8BDF869D, 0x93DF9A97, 0x919A9C96, 0x9EDF9A8C, 0x9A9A8D98, 0x8B919A92, 0x4E7DDFD1, 0x897C337D, 0xB07C727C, 0x7F7C767C, 0x8A7C1D7D, 0x9D7C727C, 0x0F7DB17C, 0x3C6FBE7E, 0x3A7DB66C, 0x157D5F7D, 0xB66C516C, 0x5F7D3A7D, 0xBE7E157D, 0x256F436D, 0x3A7DB66C, 0x157D5F7D, 0x256F2B75, 0x3A7DB66C, 0x157D5F7D, 0x436EBE7E, 0x897C337D, 0xB07C727C, 0x7F7C767C, 0x187D567D, 0x5D7D8F68, 0x4E7D167D, 0x327D397D, 0xBC7C767C, 0x6C7CA57C, 0x367DA77C, 0x177D197D, 0x497D2974, 0x157D187D, 0x5D7D3B7D, 0x487D237D, 0x77F5BD7E, 0x7D1F6C3B, 0x7D6E7036, 0x7D3B7D5D, 0x7D3E7D5F, 0x7D887642, 0x7145763A, 0x7E4A7D2B, 0x7C897CBE, 0x7C9D7C76, 0x7C8B7C98, 0x7CA47EB7, 0x72367D7F, 0x68177D81, 0x7E3D7D58, 0x7D5370BD, 0x6E377D4C, 0x7178722D, 0x7C557D45, 0x7C9C7C83, 0x7D397D75, 0x7D167D5F, 0x7D507D41, 0x6F4C6933, 0x7D887693, 0x7DBE7E3A, 0x7D127D23, 0x6F367D17, 0x7D397693, 0x7D567332, 0x7D427D5F, 0x7D377D18, 0xF5BD7E5D, 0x337D4E7D, 0x3A7D8876, 0x177D8172, 0x1970167D, 0x327D8E74, 0x15779369, 0x417D936F, 0x187D507D, 0x527D4A7D, 0x0E68BE7E, 0x327D2B71, 0x46767372, 0x3D690F7D, 0x4A7D187D, 0x557D377D, 0x0469187D, 0x5D7DB370, 0x397D7376, 0x1F7D397D, 0x696E367D, 0x7174177D, 0x3B7D3E7D, 0x3E7D5D7D, 0xBD7E427D, 0x2B710E68, 0xB87C337D, 0xA87C6C7C, 0x46766C7C, 0x3E70557D, 0x3B7D597D, 0x527D5D7D, 0xBE7E397D, 0x876C5E72, 0x1B6F327D, 0x3D69337D, 0x456F547D, 0xB47C557D, 0xB47C6C7C, 0x397D6C7C, 0x367D556C, 0x5D7D4074, 0x527D3B7D, 0xBD7E167D, 0x7C907CF5, 0x7DB07C9D, 0x71187D56, 0x7DB06E69, 0x7D4D6D36, 0x773B7D28, 0x7D03720C, 0x7D3B7D4A, 0x6D427D54, 0x7D826F91, 0x7D07770F, 0x7D5C6D3E, 0x7D907017, 0x7DBE7E4A, 0x7D427D5F, 0x730F7D17, 0x7D0E7656, 0x6EBD7E48, 0x7D1D7D8A, 0x71377D56, 0x72337DAD, 0x7D177D7E, 0x7D3E6855, 0x6A5D7D4A, 0x7D397D68, 0x7D3E7D37, 0x7D7E6F3B, 0x7D8B6A54, 0x7D3B7D50, 0x7E427D54, 0x7D577DBD, 0x7D567D57, 0x6A337D42, 0x7D8F7504, 0x6C567333, 0x7D327D69, 0x7D5D7D3D, 0x7E557D42, 0x7D4A7DBE, 0x7D4A7D56, 0x7D237D5D, 0x6D407D5D, 0x7D826F91, 0x723F7139, 0x6D337D24, 0x7D9F7391, 0x7D557D39, 0x72227D56, 0x7D127D78, 0x7E5D7D37, 0x7D4A7DBD, 0x7D187D31, 0x7D1F6A52, 0x7D4E7D52, 0x7D367D39, 0x7D177D37, 0x7D5B7D44, 0x7DBE7E41, 0x7D427D39, 0x7D586E20, 0x7D3D7D0F, 0x7D3D7D54, 0x6EBE7E3D, 0x7178722D, 0x6C367D45, 0x7D3E7D03, 0xF5BD7E42, 0x78722D6E, 0x337D4571, 0x327D796D, 0x0E7D2F7D, 0x177D1D7D, 0x4A7D397D, 0x5D7D3B7D, 0xBE7E3B7D, 0x337D1B6F, 0x547D3D69, 0x1F7D456F, 0x966D1577, 0x3C6F397D, 0x367D567D, 0x4E7D486A, 0x167D597D, 0x3C77BD7E, 0x45715D7D, 0x367D1F6C, 0x557D2569, 0x157D377D, 0x527D3B7D, 0x397D167D, 0x486DBE7E, 0x8E712977, 0x1E71367D, 0x68705D7D, 0x557D546F, 0x83756772, 0x3B7D507D, 0x4F697074, 0x0F7D177D, 0x3B7D4A7D, 0x167D5D7D, 0x557D337D, 0x367D2569, 0x3E7D036C, 0xBD7E427D, 0x2B710E68, 0x2D6E0F7D, 0x3B7D3E7D, 0x427D5D7D, 0x417D337D, 0x5B7D147D, 0xBD7E567D, 0x2B710E68, 0x5D7D327D, 0x356D237D, 0x2076177D, 0x3B7D537D, 0x3E7D5D7D, 0x337D427D, 0x557D417D, 0xBD7E9C7E, 0x6E8876F5, 0x7D337DB0, 0x7D1A7D40, 0x7D397D3E, 0x6D427D4A, 0x702B716C, 0x69367D15, 0x730F7D25, 0x7D507D03, 0x7C397D16, 0x7DA77C90, 0x7D817155, 0x7D3E7D23, 0x7D5D7D3B, 0x7DBD7E42, 0x76337D4E, 0x7D567D88, 0x7C907C18, 0x70557DA7, 0x7D3B7D90, 0x7D167D5D, 0x6D327D39, 0x7D187D92, 0x7D567D37, 0x7D427D3E, 0x7DBE7E55, 0x7D4A7D1F, 0x7D4A7D56, 0x67187D42, 0x700F7DB7, 0x7D4A7D25, 0x7D4C7452, 0x7D3B7D59, 0x7D157D52, 0x7D567D16, 0x7D4A7D1F, 0x7D377D15, 0x7E4A7D5D, 0x7DA176BE, 0x7D386855, 0x7D157D50, 0x6C256931, 0x7D916DB6, 0x7D3A7D23, 0x7C907C33, 0x7D567DA7, 0x7D4A7D1F, 0x7D377D15, 0xF5BD7E5D, 0x157D447D, 0x4A7D367D, 0x1F7D3B7D, 0x4173BE7E, 0x3B7D3E7D, 0x5477327D, 0x557D5D7D, 0x727C847C, 0x4C7D527D, 0x907C5D7D, 0x417DA77C, 0x6776BD7E, 0x397D5D7D, 0x5B7D5D7D, 0x507D127D, 0x327D3A7D, 0x5D7D377D, 0xBE7E557D, 0xB16B486D, 0x127D9871, 0x3B7D157D, 0x427D547D, 0x417D0E7D, 0x5B7D147D, 0xBE7E377D, 0x5D7D397D, 0x4B755B7D, 0x0F7D497D, 0x507D0C71, 0xBD7E167D, 0xA26CA176, 0x367D1B6E, 0x936F327D, 0x3868337D, 0x447D4C7D, 0x377D5B7D, 0x936FA967, 0x3F72557D, 0x3B7D3E7D, 0x427D5D7D, 0x69F5BD7E, 0x7E557D93, 0x7D5F7D8A, 0x7EA47E33, 0x6F397D89, 0x7D0F7D45, 0x7D507D56, 0x7E397D16, 0x7D216BBE, 0x6CA17632, 0x7D1B6EA2, 0x6F187D56, 0x700F7D98, 0x70177D19, 0x7D4A7D90, 0x6D8A7E3B, 0x7D896ABD, 0x7D0E7D4C, 0x7E597D56, 0x7D897EB7, 0x7D936939, 0x6E436933, 0x7D0F7DB0, 0x7E5D7D5D, 0x7CB87CBE, 0x7CA87C6C, 0x7D0F7D6C, 0x7D507D56, 0xF5BD7E42, 0x327D9369, 0x5D7D3E74, 0x8A7E3B7D, 0xB77E597D, 0x0E7D377D, 0x93693A7D, 0x4369337D, 0x0F7DB06E, 0x3E7D926D, 0x167D3B7D, 0x3A7D0E7D, 0x567D487D, 0x897EB77E, 0x8A6F397D, 0x397D527D, 0x216BBE7E, 0x8A7E327D, 0xBB68036B, 0x1E7D407D, 0x327D0E7D, 0x5B7D387D, 0x427D487D, 0x897EB77E, 0x9369397D, 0x8A6F367D, 0x2B6A547D, 0xBD7E487D, 0x7D8A7EF5, 0x7EBE7E5F, 0x69397D89, 0x6E327D93, 0x7178722D, 0x77367D45, 0x6A547D07, 0x7E487D2B, 0x7D237DBD, 0x7D567D4C, 0x70337D5F, 0x7E546F68, 0x7184749C, 0x7D4C7D3F, 0xF5B77E0E, 0x78722D6E, 0x3A7D4571, 0x237D5D7D, 0xAD7C417D, 0x757CB17C, 0xB17CAD7C, 0x397D757C, 0x4F697074, 0x0F7D177D, 0x3B7D4A7D, 0x167D5D7D, 0x6870216B, 0xB06E337D, 0x5868367D, 0xBE7E407D, 0x0E7D0374, 0x8A7E3A7D, 0x3F718474, 0x0E7D4C7D, 0x897EB77E, 0x456F397D, 0x567D0F7D, 0x427D507D, 0x4C7DBD7E, 0x547D3E7D, 0xBC74327D, 0x567D3C7D, 0x567D377D, 0x427D3E7D, 0xBE7E557D, 0x4A7D427D, 0x367D567D, 0x3F718474, 0x0E7D4C7D, 0xBD7E417D, 0x6870216B, 0x9369327D, 0x4173337D, 0x367D8B68, 0x0F7D1875, 0x4F7D5F7D, 0x9369BE7E, 0x1875337D, 0x4A7D0F7D, 0x187D317D, 0x5673527D, 0x5D7D3B7D, 0x557D427D, 0x5D7DBE7E, 0x377D547D, 0xBC74177D, 0x5D7D3C7D, 0x197D427D, 0x367D5B7D, 0x5F7D8A7E, 0xBD6DBE7E, 0xB173896A, 0x897EB77E, 0x4173397D, 0x0F7D8B68, 0x4A7D526B, 0x447DBE7E, 0x3B7D4A7D, 0x3E7D367D, 0x177D4E7D, 0x3170397D, 0x427D3E7D, 0x47F5BD7E, 0xE723F99B, 0x4B1E3072, 0x04475BA7, 0x869CB6D8, 0xDDE9878E, 0xB4F33DFA, 0x6C68922A, 0xDFC13859, 0xAB56E2B3, 0xD83512DD, 0xA094E1E6, 0xE6A9B336, 0xF6F36534, 0x0D6AF626, 0xABF0336E, 0x3399B391, 0x80FD484D, 0x525AAB70, 0x6DD2CC31, 0x6BDBF9B5, 0xA4FAD65B, 0xD004CC0F, 0xEC0B34E7, 0xB2B8A62F, 0x9B2602BA, 0xA6F5ECC7, 0xE15A9AEC, 0xAEB2CE83, 0x9A43B0D7, 0x188059A7, 0xE60351CA, 0x32B7C3E8, 0x315AE0B1, 0x259A9C7A, 0xCCE27522, 0xF46FD0BA, 0xFBED52B7, 0x67C8254A, 0xF625830D, 0x35EBBBEB, 0x1FE07369, 0xE7F6B18C, 0x7E08304D, 0xFD391DA2, 0x43824A48, 0xEEE0B3D3, 0x1CF8DAFD, 0xBC131667, 0xE2E973C5, 0x6675DC2D, 0x6FEC5F91, 0x882A4F6B, 0xA98E0032, 0x59897497, 0x7BEED746, 0x73ABDAF2, 0x1F92C681, 0x007FCCF8, 0x06053D00, 0x80C0D9C5, 0x654006C7, 0x518041D6, 0xBAE6F34D, 0x205D8CD2, 0x79E64098, 0xF8261A35, 0x6436B356, 0x673D5BDB, 0x467BAEA9, 0xAB302680, 0xF62A99B7, 0xB66B8A37, 0xC0DACDC0, 0x53AB4B97, 0xE733E80B, 0x7059B2C2, 0x9849F3A4, 0xA52C82ED, 0x4F7298D3, 0xA556DEFE, 0xAED1988C, 0xFEF8134D, 0x5B97AB57, 0xC87BB73F, 0xE7B2FD5F, 0xBDB8B624, 0xBBE0C3DB, 0x581B4E6F, 0x39175738, 0x93A57D03, 0x63DAFAC9, 0x9835B07D, 0x952BE370, 0x8D0D6F14, 0x0B52D136, 0x37E29DCE, 0x15356C80, 0xA8F41350, 0xE0728E9D, 0x37C54AD9, 0xF302A0E3, 0x46A944DC, 0x62246C86, 0xD5FFFBDC, 0xE1B31687, 0xD1A42AF9, 0xB590CD0F, 0x63966698, 0x0F03B448, 0xAA1E6886, 0x1C8D67AD, 0xE2F23245, 0x1873BE71, 0xCB3286AC, 0x56CF77C1, 0x9D3E3A2F, 0xB1B705C4, 0x76AEFA95, 0x702E3FE4, 0xB76777E3, 0x9F3F0A8D, 0xF2848345, 0x3CBAF8D5, 0xCD7F7979, 0x8F10E9ED, 0x81D7188D, 0x81273011, 0x2E067E78, 0x1BE5CD04, 0x4B6EEA0B, 0x1944FC19, 0x3947A752, 0x08EFAE61, 0x4F01C7DA, 0x30422399, 0x9935E677, 0x695804BA, 0x58AE0241, 0x794E2535, 0x8F80AE91, 0xFA20932F, 0x62037C97, 0x49462849, 0xF8F3B8BB, 0x568B6A54, 0x97F17C29, 0xD8A6D68D, 0x41D199F7, 0x6E59BCFC, 0x669CA6C7, 0x7367101F, 0xE76BFCCD, 0xD75C659E, 0xB5CC269C, 0x6A831CC4, 0x267D5B75, 0x0A0FD1EE, 0x724AA19F, 0x1131577E, 0x7A88F321, 0x5F040753, 0x761338E2, 0xD25961A5, 0x436F6563, 0x9C31DF23, 0xDC38B439, 0xD5B1D24C, 0x891C8FA7, 0x8E38CED8, 0x1CC8397F, 0xDD9CB7EB, 0x995513C9, 0xF38CE429, 0xE328A330, 0x4BAD0597, 0x4068174D, 0xEEEA2350, 0x2802ABA2, 0xD565F2FA, 0xA7C22E19, 0x86560968, 0xF51E1E19, 0xD857AF99, 0x8B18036A, 0xF77AEF18, 0xF11AEAB6, 0xB1010E23, 0x168A7B40, 0xBCC51FF0, 0x299671F6, 0xE3CF0A8B, 0xD2940503, 0x7B2F016C, 0x8FE40194, 0x52E0B4E2, 0xAB7BAAAB, 0xB4B6B826, 0x823952F6, 0x491F8F3F, 0x1972CC89, 0x873F195F, 0x16DEB42F, 0x862749A2, 0x57C109E0, 0xFD869114, 0x44DDF5AF, 0x683FE1F3, 0x0EDF73E7, 0xF84EA1C5, 0x32B47874, 0xC59FDFC5, 0x081FCFCF, 0x4DFE9022, 0x99834A69, 0xF78FFF72, 0xDC4A0088, 0x06B62DAC, 0x3D8C6652, 0x8813B12F, 0x269830AA, 0x5B3F0C24, 0xD9276E55, 0x0A2F92F6, 0x5C1AF66F, 0xC154914C, 0x0FCBDBD0, 0xEFB8BB5C, 0x482B1E48, 0x876BC0E8, 0x8A84D6D0, 0x313B834E, 0x5063C3BC, 0x3A5D9B4D, 0x2AF05B5E, 0x881D2034, 0x6E830542, 0x9E5C6EBE, 0x1AEF76A3, 0x76A9E5CB, 0xB8E6F48B, 0xF9F18581, 0xFB24187F, 0xCE4F63AC, 0xD675BF9A, 0x5A5CBA0B, 0x37E70F14, 0xE45A7638, 0xFFDEFCF6, 0x88B1A5F4, 0x2ED1B09E, 0x6518AE27, 0xAAD23F6E, 0x1D1C3AA4, 0x7AACCC66, 0x2DB0B24B, 0xBDC54F81, 0xCA630C72, 0x59F92CC1, 0xF02C6DA0, 0xEFAB6657, 0x4BBE7DE8, 0xA566F544, 0x22B2C1E8, 0xB52FCDDD, 0x7BFF0BA0, 0xBE4A129D, 0x3DAC0391, 0x152990EF, 0xEF4DD025, 0xFB2B3C09, 0xBD6AF45E, 0x958B3E25, 0x27A60950, 0xCC0154D1, 0x04F3C49F, 0x4B218735, 0x8240AAC0, 0x677B04F9, 0x77C9DB27, 0x4BC5EB71, 0x96762CD1, 0xDB86CE77, 0x3DDC6918, 0x79A87EDB, 0x38CB629F, 0xC044D488, 0x749E32AB, 0xAC87D4D8, 0x69485A5B, 0x04DA5B28, 0x999CAF4E, 0x7639A7F8, 0xB59EF422, 0x9D5F7791, 0xC3B3DFFC, 0x3CE3AC8A, 0xB1940DA5, 0x49BC4167, 0x3650B876, 0x72C11E54, 0xC2451E15, 0x7DE05FEE, 0xB0908569, 0x7A5CF9A1, 0x37A93192, 0x57E9E0E5, 0x8E9A97F8, 0x5A14EBA2, 0xAAB749A0, 0x0CDB2DF6, 0x43A7747E, 0x145B7479, 0xB3552B5F, 0x594807BB, 0x873E0922, 0x9A77938F, 0xE2698895, 0x238B3AE8, 0xF2575822, 0x4E0A0E93, 0x07234F05, 0xA66CFE01, 0xC2357643, 0xB4158847, 0xCA79AB16, 0x065AE034, 0x8AEB8566, 0xEC0DB7E2, 0x1288D83C, 0x7F93CCCC, 0x9D1C0454, 0xAAD92490, 0xB034AF6F, 0xB65D6332, 0xC79910DC, 0x70E6C901, 0xE0360415, 0x642A72CB, 0xE3C204A8, 0x3C2BC24C, 0x3C113B47, 0x1ED521E8, 0xA20587FC, 0x312E8B49, 0x17DBF64D, 0xBEC8DE49, 0xB81E2452, 0x009CA559, 0x5183B333, 0x58B066C7, 0xCE8BE83E, 0x493F70FF, 0x0900C1A0, 0xA42F1A8C, 0x30A63B9E, 0xFEEAAFE2, 0x233C29C1, 0x765679BC, 0x6C88C7C6, 0xCCED9552, 0x069AA432, 0xD99E9ADA, 0x178AAE85, 0xF57F2B40, 0x3ED508FF, 0x3C6E5EB2, 0x46337812, 0xE807D6B1, 0xD71E8826, 0x091251E1, 0xF76821ED, 0x6473E2A1, 0x0AD86715, 0x84B8C7AE, 0x26AD8BDD, 0x799B3962, 0xC6D693CA, 0x21219D4C, 0x47C7CE65, 0x3307D976, 0x3B381B15, 0xA421BC51, 0x7E97AA1E, 0xD552F15A, 0x437D18C8, 0x760E4576, 0xA81D613B, 0xAAF71069, 0x4455F3A1, 0xB6D3C537, 0x60B93C79, 0xD0AE9C59, 0x60999404, 0x0ADA118E, 0xB8CAAD79, 0x774275D1, 0xF2FA7DC5, 0x26E2E8FD, 0x865BE67B, 0x6BAFC51D, 0x541DC2A4, 0x8DEEB385, 0xC11B3204, 0x8A7B1020, 0x7BDF7126, 0x20A183A5, 0x2832E4E1, 0xC093F27F, 0xD8540872, 0x17F2C607, 0x2A49B01B, 0x09E4D5B4, 0x07A8E26C, 0xD08CA2B2, 0xAA48B3D0, 0x65321771, 0x4F8AF8AA, 0x810A666B, 0x02C7DE3B, 0x8FFD3529, 0x64B39034, 0x78BD8CA2, 0xDD2B0A1D, 0xB08F91E4, 0x07C35F32, 0x8BB59875, 0x5908E41F, 0x872E8D43, 0xE2DFF80B, 0x8888050F, 0x4B213C55, 0xCFBBBC47, 0x0B72BA5C, 0xA40D4C15, 0xB8287F2B, 0xA2D02407, 0x7761425B, 0x6DBF3D2C, 0x3CDD89C4, 0xACF35E6F, 0xD3745CA5, 0x37EAA3A4, 0x25702EB0, 0x09237162, 0x89DD4134, 0x0FDFCFAC, 0x66FD29E1, 0xB18D28E3, 0x7F13C847, 0xDAA062AD, 0x83D3ABAA, 0x81A691DF, 0xB9448678, 0x049103BD, 0xD70DC88F, 0x611F4B12, 0xB1053D38, 0x40AD9320, 0x0EF44A53, 0x4A536584, 0xE3F1CD34, 0x7AACCC77, 0x8B82A5DD, 0xE938E11A, 0xB3EB739B, 0x95BDE46E, 0x819E1CA0, 0x6550F3D4, 0xD19CFD4E, 0x4EAFDA21, 0x3AC03F46, 0xF696EF02, 0x3AD314FE, 0x16273603, 0x911BEFB1, 0xA77070BD, 0x28773C1C, 0x594889EB, 0xD76423F0, 0x6020ABCF, 0x6CB85E9A, 0x99BCD036, 0x70EEAC15, 0x58B1CBDA, 0x77F5DA6C, 0x6F206003, 0x450C896F, 0xE8EAEF01, 0x95CDD196, 0x0E10698B, 0x7D3B2703, 0x78BDF4B1, 0x8DA0820B, 0x119DD313, 0x0BFB0282, 0x8F36DA05, 0x814676C3, 0x274BB170, 0x327BB1A1, 0x46EB4143, 0xF15F0BE1, 0xA94041E1, 0x35A64D23, 0xC1EE2FF4, 0xA7FF052E, 0xFB449702, 0xB2A958C0, 0xE9BCA912, 0x9A960C42, 0x623406CD, 0xF5ABE78A, 0x94ABE351, 0x69840F23, 0x21103547, 0xA2F9561D, 0xEA5E89B7, 0xD7E5920C, 0xD0E76935, 0x1C56769A, 0x4D32D5CE, 0x188903C8 },
                    TpmFileName = null
                });

            }

            var alg = GuessCryptAlgorithm(file);
            if (null != alg)
                return alg;
            var options = Query<Xp3Options>(arcStrings.XP3EncryptedNotice);

            m_title = new WidgetXP3().Scheme.SelectedValue as string;
            Console.WriteLine(new WidgetXP3().Scheme.SelectedValue.GetType());

            return options.Scheme;
        }

        public static ICrypt GetScheme(string scheme)
        {
            ICrypt algorithm;
            if (!CommonKeys.TryGetValue(scheme, out algorithm) && !KnownSchemes.TryGetValue(scheme, out algorithm))
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
