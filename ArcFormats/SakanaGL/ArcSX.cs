//! \file       ArcSX.cs
//! \date       2022 Apr 29
//! \brief      SakanaGL resource archive implementation.
//
// Copyright (C) 2022 by morkt
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

using GameRes.Utility;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ZstdNet;

namespace GameRes.Formats.Sakana
{
    internal class SxEntry : PackedEntry
    {
        public ushort   Flags;
        public ushort   ArcIndex;

        public bool IsEncrypted  { get { return 0 == (Flags & 0x10); } }
    }

    [Export(typeof(ArchiveFormat))]
    public class SxOpener : ArchiveFormat
    {
        public override string         Tag { get { return "SXSTORAGE"; } }
        public override string Description { get { return "SakanaGL engine resource archive"; } }
        public override uint     Signature { get { return 0; } }
        public override bool  IsHierarchic { get { return true; } }
        public override bool      CanWrite { get { return false; } }

        const uint DefaultKey = 0x2E76034B;
        static readonly Regex ArchiveNameRe = new Regex (@"^(.*)-([^-]+)$");
        
        public override ArcFile TryOpen (ArcView file)
        {
            /*var base_name = Path.GetFileNameWithoutExtension (file.Name);
            var sx_name = base_name.Substring (0, 4) + "(00).sx";
            sx_name = VFS.ChangeFileName (file.Name, sx_name);
            if (!VFS.FileExists (sx_name))
            {
                var match = ArchiveNameRe.Match (base_name);
                if (!match.Success)
                    return null;
                sx_name = VFS.ChangeFileName (file.Name, match.Groups[1] + "(00).sx");
                if (!VFS.FileExists(sx_name))
                    return null;
            }
            if (file.Name.Equals(sx_name, StringComparison.OrdinalIgnoreCase))
                return null;*/
            if (Path.GetExtension(file.Name) != ".sx")
                return null;

            byte[] index_data;
            using (var sx = VFS.OpenView (file.Name))
            {
                if (sx.MaxOffset <= 0x10)
                    return null;
                if (!sx.View.AsciiEqual(0, "SSXXDEFL"))
                    return null;
                int key = Binary.BigEndian (sx.View.ReadInt32 (8));
                int length = (int)(sx.MaxOffset - 0x10);
                var index_packed = sx.View.ReadBytes (0x10, (uint)length);

                long lkey = (long)key + length;
                lkey = key ^ (961 * lkey - 124789) ^ DefaultKey;
                uint key_lo = (uint)lkey;
                uint key_hi = (uint)(lkey >> 32) ^ 0x2E6;
                DecryptData (index_packed, key_lo, key_hi);

                index_data = UnpackZstd (index_packed);
            }
            using (var index = new BinMemoryStream (index_data))
            {
                var reader = new SxIndexDeserializer (index, file.MaxOffset);
                var dir = reader.Deserialize();
                if (null == dir || dir.Count == 0)
                    return null;
                return new ArcFile (file, this, dir);
            }
        }

        public override Stream OpenEntry (ArcFile arc, Entry entry)
        {
            //引数で渡されたarcはsxファイルを指しているので、同階層のsxstorageファイルを検索
            string pkg_path = Path.GetDirectoryName(arc.File.Name);
            string[] names = Directory.GetFiles(pkg_path, "*.sxstorage");
            string[] key_words;

            if (entry.Type == "image")
                key_words = new string[2] { "img.sxstorage", "0.sxstorage" };
            else if (entry.Type == "audio")
                key_words = new string[2] { "snd.sxstorage", "0.sxstorage" };
            else
                key_words = new string[1] { "0.sxstorage" };

            using (var sxstorage = FindArc(names, key_words, entry))
            {
                var input = sxstorage.View.ReadBytes(entry.Offset, entry.Size);
                var sx_entry = entry as SxEntry;
                if (sx_entry.IsEncrypted)
                {
                    uint key_lo = (uint)(entry.Offset >> 4) ^ (entry.Size << 16) ^ DefaultKey;
                    uint key_hi = (entry.Size >> 16) ^ 0x2E6;
                    DecryptData(input, key_lo, key_hi);
                }
                if (sx_entry.IsPacked)
                {
                    input = UnpackZstd(input);
                    if (sx_entry.UnpackedSize == 0)
                        sx_entry.UnpackedSize = (uint)input.Length;
                }
                return new BinMemoryStream(input, entry.Name);
            }
        }

        private ArcView FindArc(string[] names, string[] key_words, Entry entry)
        {
            if (names.Length == 1) //sxstorageが1個だけの場合
                return new ArcView(names.First());
            foreach (string key_word in key_words) //sxstorageが複数ある場合 ⇒ 検索開始
            {
                foreach (string name in names)
                {
                    if (!Path.GetFileName(name).Contains(key_word))
                        continue;
                    var arc_view = new ArcView(name);
                    if (arc_view.MaxOffset < entry.Offset + entry.Size)
                        continue;
                    if (key_words.Length == 1) //画像・音楽以外
                        return arc_view;
                    string ext = Path.GetExtension(entry.Name);
                    if ( //検索が必要なファイル拡張子があれば、条件を適宜追加すること
                        ((ext == ".webp") && (arc_view.View.ReadString(entry.Offset + 8, 4) == "WEBP"))
                        || ((ext == ".png") && (arc_view.View.ReadString(entry.Offset + 1, 3) == "PNG"))
                        || (new string[2] { ".jpg", ".jpeg" }.Contains(ext) && (arc_view.View.ReadUInt16(entry.Offset) == 0xD8FF))
                        || ((ext == ".bmp") && (arc_view.View.ReadString(entry.Offset, 2) == "BM"))
                        || (new string[2] { ".tif", ".tiff" }.Contains(ext) && new string[2] { "II", "MM" }.Contains(arc_view.View.ReadString(entry.Offset, 2)))
                        || ((ext == ".wav") && (arc_view.View.ReadString(entry.Offset + 8, 4) == "WAVE"))
                        || ((ext == ".ogg") && (arc_view.View.ReadString(entry.Offset, 4) == "OggS"))
                        )
                        return arc_view;
                }
            }
            throw new FileNotFoundException("file is no found"); //該当するsxstorageが見つからなければエラー
        }

        internal static byte[] UnpackZstd (byte[] data)
        {
            int unpacked_size = BigEndian.ToInt32 (data, 0);
            using (var dec = new Decompressor())
            {
                var packed = new ArraySegment<byte> (data, 4, data.Length - 4);
                return dec.Unwrap (packed, unpacked_size);
            }
        }

        internal static void DecryptData (byte[] data, uint key_lo, uint key_hi)
        {
            if (data.Length < 4)
                return;
            key_lo ^= 0x159A55E5;
            key_hi ^= 0x075BCD15;
            uint v1 = key_hi ^ (key_hi << 11) ^ ((key_hi ^ (key_hi << 11)) >> 8) ^ 0x549139A;
            uint v2 = v1 ^ key_lo ^ (key_lo << 11) ^ ((key_lo ^ (key_lo << 11) ^ (v1 >> 11)) >> 8);
            uint v3 = v2 ^ (v2 >> 19) ^ 0x8E415C26;
            uint v4 = v3 ^ (v3 >> 19) ^ 0x4D9D5BB8;
            int count = data.Length / 4;
            unsafe
            {
                fixed (byte* data_raw = data)
                {
                    uint* data32 = (uint*)&data_raw[0];
                    for (int i = 0; i < count; ++i)
                    {
                        uint t1 = v4 ^ v1 ^ (v1 << 11) ^ ((v1 ^ (v1 << 11) ^ (v4 >> 11)) >> 8);
                        uint t2 = v2 ^ (v2 << 11);
                        v2 = v4;
                        v4 = t1 ^ t2 ^ ((t2 ^ (t1 >> 11)) >> 8);
                        data32[i] ^= (t1 >> 4) ^ (v4 << 12);
                        v1 = v3;
                        v3 = t1;
                    }
                }
            }
        }
    }

    internal class SxIndexDeserializer
    {
        IBinaryStream   m_index;
        long            m_max_offset;
        string[]        m_name_list;
        List<Entry>     m_dir;

        public SxIndexDeserializer (IBinaryStream index, long max_offset)
        {
            m_index = index;
            m_max_offset = max_offset;
        }

        public List<Entry> Deserialize ()
        {
            m_index.Position = 8;
            int count = Binary.BigEndian (m_index.ReadInt32());
            m_name_list = new string[count];
            for (int i = 0; i < count; ++i)
            {
                int length = m_index.ReadUInt8();
                m_name_list[i] = m_index.ReadCString (length, Encoding.UTF8);
            }

            count = Binary.BigEndian (m_index.ReadInt32());
            m_dir = new List<Entry> (count);
            for (int i = 0; i < count; ++i)
            {
                ushort arc   = Binary.BigEndian (m_index.ReadUInt16());
                ushort flags = Binary.BigEndian (m_index.ReadUInt16());
                uint offset  = Binary.BigEndian (m_index.ReadUInt32());
                uint size    = Binary.BigEndian (m_index.ReadUInt32());
                var entry = new SxEntry {
                    Flags  = flags,
                    Offset = (long)offset << 4,
                    Size   = size,
                    IsPacked = 0 != (flags & 0x03),
                    ArcIndex = arc,
                };
                m_dir.Add (entry);
            }

            int arc_count = Binary.BigEndian (m_index.ReadUInt16());
            int arc_index = -1;
            for (int i = 0; i < arc_count; ++i)
            {
                m_index.ReadUInt32();
                m_index.ReadUInt32();
                m_index.ReadUInt32();
                long arc_size = (long)Binary.BigEndian (m_index.ReadUInt32()) << 4; // archive body length
                if (m_max_offset == arc_size)
                    arc_index = i;
                m_index.ReadUInt64();
                m_index.Seek (16, SeekOrigin.Current); // MD5 sum
            }

            count = Binary.BigEndian (m_index.ReadUInt16());
            if (count > 0)
                m_index.Seek (count * 24, SeekOrigin.Current);
            DeserializeTree();
            if (arc_count > 1 && arc_index != -1)
            {
                return m_dir.Where (e => (e as SxEntry).ArcIndex == arc_index).ToList();
            }
            return m_dir;
        }

        void DeserializeTree (string path = "")
        {
            int count = Binary.BigEndian (m_index.ReadUInt16());
            int name_index = Binary.BigEndian (m_index.ReadInt32());
            int file_index = Binary.BigEndian (m_index.ReadInt32());
            var name = Path.Combine (path, m_name_list[name_index]);
            if (-1 == file_index)
            {
                for (int i = 0; i < count; ++i)
                {
                    DeserializeTree (name);
                }
            }
            else
            {
                m_dir[file_index].Name = name;
                m_dir[file_index].Type = FormatCatalog.Instance.GetTypeFromName (name);
            }
        }
    }
}
