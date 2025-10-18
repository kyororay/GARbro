//! \file       ArcDAT.cs
//! \date       Sat May 14 02:20:37 2016
//! \brief      'non color' resource archive.
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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Shapes;
using GameRes.Compression;
using GameRes.Formats.Strings;
using GameRes.Utility;
using GARbro.GUI;
using static GameRes.Formats.NekoNyan.SpriteGameDatabase;

namespace GameRes.Formats.NonColor
{
    internal class ArcDatEntry : PackedEntry
    {
        public byte[]   RawName;
        public ulong    Hash;
        public int      Flags;
    }

    internal class ArcDatArchive : ArcFile
    {
        public readonly ulong MasterKey;

        public ArcDatArchive (ArcView arc, ArchiveFormat impl, ICollection<Entry> dir, ulong key)
            : base (arc, impl, dir)
        {
            MasterKey = key;
        }
    }

    [Serializable]
    public class Scheme
    {
        public string   Title;
        public ulong    Hash;
        public bool     LowCaseNames;
        public short[]  EvIdx;
        public bool     EvIsHex;

        public Scheme(string title)
        {
            Title = title;
            var key = Encodings.cp932.GetBytes(title);
            Hash = ComputeHash (key);
        }

        public virtual ulong ComputeHash (byte[] name)
        {
            return Crc64.Compute (name, 0, name.Length); //CRC64_WE
        }
    }

    [Serializable]
    public class ArcDatScheme : ResourceScheme
    {
        public Dictionary<string, Scheme> KnownSchemes;
    }

    public class ArcDatOptions : ResourceOptions
    {
        public string Scheme;
    }

    [Export(typeof(ArchiveFormat))]
    public class DatOpener : ArchiveFormat
    {
        public override string         Tag { get { return "ARC/noncolor"; } }
        public override string Description { get { return "'non color' resource archive"; } }
        public override uint     Signature { get { return 0; } }
        public override bool  IsHierarchic { get { return true; } }
        public override bool      CanWrite { get { return false; } }

        public DatOpener ()
        {
            Extensions = new string[] { "dat" };
        }

        internal const int SignatureKey = 0x26ACA46E;

        public override ArcFile TryOpen (ArcView file)
        {
            if (!file.Name.HasExtension (".dat"))
                return null;
            int count = file.View.ReadInt32 (0) ^ SignatureKey;
            if (!IsSaneCount (count))
                return null;
            var scheme = QueryScheme (file.Name);
            if (null == scheme)
                return null;
            using (var index = new NcIndexReader(file, count))
                return index.Read(this, scheme);
        }

        public override Stream OpenEntry (ArcFile arc, Entry entry)
        {
            var darc = arc as ArcDatArchive;
            var dent = entry as ArcDatEntry;
            if (null == darc || null == dent || 0 == dent.Size)
                return base.OpenEntry (arc, entry);
            var data = arc.File.View.ReadBytes (entry.Offset, entry.Size);
            if (dent.IsPacked)
            {
                if (darc.MasterKey != 0)
                    DecryptData (data, (uint)(dent.Hash ^ darc.MasterKey));
                else if (6 == dent.Flags)
                    DecryptData (data, (uint)dent.Hash);
                return new ZLibStream (new MemoryStream (data), CompressionMode.Decompress);
            }
            // 1 == dent.Flags
            if (dent.RawName != null && 0 != dent.Flags)
                DecryptWithName (data, dent.RawName);
            return new BinMemoryStream (data, entry.Name);
        }

        internal unsafe void DecryptData (byte[] data, uint key)
        {
            fixed (byte* data8 = data)
            {
                uint* data32 = (uint*)data8;
                for (int i = data.Length/4; i > 0; --i)
                    *data32++ ^= key;
            }
        }

        internal void DecryptWithName (byte[] data, byte[] name)
        {
            int block_length = data.Length / name.Length;
            int n = 0;
            for (int i = 0; i < name.Length-1; ++i)
            for (int j = 0; j < block_length; ++j)
                data[n++] ^= name[i];
        }

        static ArcDatScheme DefaultScheme = new ArcDatScheme { KnownSchemes = new Dictionary<string, Scheme>() };

        public static Dictionary<string, Scheme> KnownSchemes { get { return DefaultScheme.KnownSchemes; } }

        public override ResourceScheme Scheme
        {
            get { return DefaultScheme; }
            set { DefaultScheme = (ArcDatScheme)value; }
        }

        internal Scheme QueryScheme (string arc_name)
        {
            var title = FormatCatalog.Instance.LookupGame (arc_name);
            if (!string.IsNullOrEmpty (title) && KnownSchemes.ContainsKey (title))
                return KnownSchemes[title];
            var options = Query<ArcDatOptions> (arcStrings.ArcEncryptedNotice);
            Scheme scheme;
            if (string.IsNullOrEmpty (options.Scheme) || !KnownSchemes.TryGetValue (options.Scheme, out scheme))
                return null;
            return scheme;
        }

        public override ResourceOptions GetDefaultOptions ()
        {
            return new ArcDatOptions { Scheme = Properties.Settings.Default.NCARCScheme };
        }

        public override object GetAccessWidget ()
        {
            return new GUI.WidgetNCARC();
        }
    }

    internal abstract class NcIndexReaderBase : IDisposable
    {
        protected IBinaryStream m_input;
        private   List<Entry>   m_dir;
        private   int           m_count;
        private   ArcView       m_file;
        internal   Dictionary<ulong, string> m_file_map = new Dictionary<ulong, string>();

        public long IndexPosition { get; set; }
        public long MaxOffset { get { return m_file.MaxOffset; } }
        public bool ExtendByteSign { get; protected set; }

        protected NcIndexReaderBase (ArcView file, int count)
        {
            m_input = file.CreateStream();
            m_dir = new List<Entry> (count);
            m_count = count;
            m_file = file;
            IndexPosition = 4;
        }

        public ArcFile Read (DatOpener format, Scheme scheme)
        {
            /*var file_map = new Dictionary<ulong, string>();
            var dir = FormatCatalog.Instance.DataDirectory;
            using (var sr = new StreamReader(dir + "\\HorkEye_hash.lst", Encodings.cp932))
            {
                while (sr.Peek() != -1)
                {
                    var line_parts = sr.ReadLine().Split(',');
                    file_map[Convert.ToUInt64("0x" + line_parts[0], 16)] = line_parts[1];
                }
            }*/
            MakeHashKey(scheme);

            m_input.Position = IndexPosition;
            for (int i = 0; i < m_count; ++i)
            {
                var entry = ReadEntry();
                var name_bytes = new byte[] { };
                string name = null;
                if (m_file_map.ContainsKey(entry.Hash))
                {
                    name = m_file_map[entry.Hash];
                    name_bytes = Encodings.cp932.GetBytes(name);
                    entry.Name = name;
                    entry.Type = FormatCatalog.Instance.GetTypeFromName(entry.Name);
                    entry.RawName = name_bytes;
                    //Trace.WriteLine(string.Format("{0:X08} : {1}", entry.Hash, name), "[noncolor]");
                }
                /*else
                {
                    Trace.WriteLine (string.Format ("{0:X08} : ", entry.Hash), "[noncolor]");
                }*/
                if (0 == (entry.Flags & 2))
                {
                    if (null == name)
                        continue;
                    else
                    {
                        var raw_name = name_bytes;
                        entry.Offset ^= Extend8Bit(raw_name[raw_name.Length >> 1]);
                        entry.Size ^= Extend8Bit(raw_name[raw_name.Length >> 2]);
                        entry.UnpackedSize ^= Extend8Bit(raw_name[raw_name.Length >> 3]);
                    }
                }
                if (!entry.CheckPlacement(MaxOffset))
                    continue;
                if (string.IsNullOrEmpty(entry.Name))
                    entry.Name = string.Format("{0:D5}#{1:X8}", i, entry.Hash);
                m_dir.Add(entry);
            }
            if (0 == m_dir.Count)
                return null;
            
            return new ArcDatArchive (m_file, format, m_dir, scheme.Hash);
        }

        protected void MakeHashKey(Scheme scheme)
        {
            //ƒCƒxƒ“ƒgCG
            foreach (var a in new string[] { "", "z/"})
            {
                foreach (int b in scheme.EvIdx)
                {
                    for (int c = 0; c < 100; c++)
                    {
                        if (scheme.EvIsHex)
                        {
                            for (int d = 0; d < 0xFF; d++)
                            {
                                var name = String.Format("{0}ev/EV_{1:000}_{2:00}_{3:X2}.tlg", a, b, c, d);
                                if (scheme.LowCaseNames)
                                    name = name.ToLowerInvariant();
                                var name_bytes = Encodings.cp932.GetBytes(name);
                                var crc64 = Crc64.Compute(name_bytes, 0, name_bytes.Length); //CRC64_WE
                                m_file_map[crc64] = name;
                            }
                        }
                        else
                        {
                            for (int d = 0; d < 10; d++)
                            {
                                for (int e = 0; e < 25; e++)
                                {
                                    var alp = Encodings.cp932.GetString(new byte[] { (byte)(0x41 + e) });
                                    var name = String.Format("{0}ev/EV_{1:000}_{2:00}_{3:0}{4}.tlg", a, b, c, d, alp);
                                    if (scheme.LowCaseNames)
                                        name = name.ToLowerInvariant();
                                    var name_bytes = Encodings.cp932.GetBytes(name);
                                    var crc64 = Crc64.Compute(name_bytes, 0, name_bytes.Length); //CRC64_WE
                                    m_file_map[crc64] = name;
                                }
                            }
                        }
                    }
                }
            }

            //—§‚¿ŠG‚Æ‚©‚à’Ç‰Á‚µ‚½‚¯‚è‚á–½–¼‹K‘¥‚ÉŠî‚Ã‚¢‚ÄƒAƒ‹ƒSƒŠƒYƒ€’Ç‰Á‚µ‚Ä‚Ë
        }

        uint Extend8Bit (byte v)
        {
            // 0xFF -> -1 -> 0xFFFFFFFF
            return ExtendByteSign ? (uint)(int)(sbyte)v : v;
        }

        protected abstract ArcDatEntry ReadEntry ();

        #region IDisposable Members
        bool m_disposed = false;
        public void Dispose ()
        {
            Dispose (true);
            GC.SuppressFinalize (this);
        }

        protected virtual void Dispose (bool disposing)
        {
            if (!m_disposed)
            {
                m_input.Dispose();
                m_disposed = true;
            }
        }
        #endregion
    }

    internal class NcIndexReader : NcIndexReaderBase
    {
        readonly uint   m_master_key;

        public NcIndexReader (ArcView file, int count, uint master_key = 0) : base (file, count)
        {
            m_master_key = master_key;
        }

        protected override ArcDatEntry ReadEntry ()
        {
            var hash   = m_input.ReadUInt64();
            int flags  = m_input.ReadByte() ^ (byte)hash;
            return new ArcDatEntry {
                Hash   = hash,
                Flags  = flags,
                Offset = m_input.ReadUInt32() ^ (uint)hash ^ m_master_key,
                Size   = m_input.ReadUInt32() ^ (uint)hash,
                UnpackedSize = m_input.ReadUInt32() ^ (uint)hash,
                IsPacked = 0 != (flags & 2),
            };
        }
    }
}
