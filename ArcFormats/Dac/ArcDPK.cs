﻿//! \file       ArcDPK.cs
//! \date       Mon Jun 01 13:29:09 2015
//! \brief      DPK archive
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
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Globalization;
using System.IO;
using GameRes.Formats.Strings;
using GameRes.Utility;

namespace GameRes.Formats.Dac
{
    internal class DpkOptions : ResourceOptions
    {
        public uint Key1;
        public uint Key2;
    }

    [Serializable]
    public class DpkScheme
    {
        public uint            Key1 { get; set; }
        public uint            Key2 { get; set; }
        public string          Name { get; set; }
        public string OriginalTitle { get; set; }
    }

    [Serializable]
    public class ArchiveScheme : ResourceScheme
    {
        public DpkScheme[] KnownSchemes;
    }

    internal class DpkEntry : Entry
    {
        public uint Hash;
    }

    internal class DpkArchive : ArcFile
    {
        public readonly uint Key1;
        public readonly uint Key2;

        public DpkArchive (ArcView arc, ArchiveFormat impl, ICollection<Entry> dir, DpkOptions opt)
            : base (arc, impl, dir)
        {
            Key1 = opt.Key1;
            Key2 = opt.Key2;
        }
    }

    [Export(typeof(ArchiveFormat))]
    public class DpkOpener : ArchiveFormat
    {
        public override string         Tag { get { return "DPK"; } }
        public override string Description { get { return "DAC engine resource archive"; } }
        public override uint     Signature { get { return 0x004b5044; } } // 'DPK'
        public override bool  IsHierarchic { get { return true; } }
        public override bool      CanWrite { get { return false; } }

        public static DpkScheme[] KnownSchemes = new DpkScheme[0];

        public override ArcFile TryOpen (ArcView file)
        {
            var header = new byte[8];
            if (8 != file.View.Read (8, header, 0, 8))
                return null;
            byte last = header[7];
            for (int i = 0; i < 8; i++)
            {
                header[i] ^= (byte)(i - 8);
            }
            int data_offset = LittleEndian.ToInt32 (header, 0);
            if (data_offset <= 16 || data_offset >= file.MaxOffset)
                return null;
            int index_length = data_offset - 16;
            var index = new byte[index_length];
            if (index_length != file.View.Read (16, index, 0, (uint)index_length))
                return null;
            DecryptIndex (index, 16, index_length, last);
            int count = LittleEndian.ToInt32 (index, 0);
            if (count <= 0 || count > 0xfffff)
                return null;

            var options = Query<DpkOptions> (arcStrings.ArcEncryptedNotice);
            var name_bytes = new byte[0x20];
            var dir = new List<Entry> (count);
            int base_offset = 4 + count * 4;
            for (int i = 0; i < count; ++i)
            {
                var index_offset = base_offset + LittleEndian.ToInt32 (index, 4+i*4);
                int name_begin = index_offset+0x0c;
                int name_end = Array.IndexOf (index, (byte)0, name_begin);
                if (-1 == name_end)
                    name_end = index.Length;
                if (name_end == name_begin)
                    continue;
                if ('z' == index[name_end-1])
                    --name_end; // strip 'z' from file extensions

                int name_length = name_end - name_begin;
                var name = Encodings.cp932.GetString (index, name_begin, name_length);
                if (name_length > name_bytes.Length)
                    name_bytes = new byte[name_length];
                // shift-jis characters sequence may contain '\\' that is not a path delimiter
                string name_base = Path.GetFileName (name);
                name_length = Encodings.cp932.GetBytes (name_base, 0, name_base.Length, name_bytes, 0);

                uint size = LittleEndian.ToUInt32 (index, index_offset + 4);
                var entry = new DpkEntry
                {
                    Name = name,
                    Type = FormatCatalog.Instance.GetTypeFromName (name),
                    Hash = GetNameHash (name_bytes, 0, name_length, options.Key1, options.Key2, size),
                    Offset = data_offset + LittleEndian.ToUInt32 (index, index_offset),
                    Size = size,
                };
                if (!entry.CheckPlacement (file.MaxOffset))
                    return null;
                dir.Add (entry);
            }
            if (0 == dir.Count)
                return null;
            return new DpkArchive (file, this, dir, options);
        }

        public override Stream OpenEntry (ArcFile arc, Entry entry)
        {
            var parc = arc as DpkArchive;
            var pentry = entry as DpkEntry;
            if (null == parc || null == pentry)
                return arc.File.CreateStream (entry.Offset, entry.Size);
            var data = new byte[entry.Size];
            arc.File.View.Read (entry.Offset, data, 0, entry.Size);
            DecryptEntry (data, parc.Key1, parc.Key2, pentry);
            return new BinMemoryStream (data, entry.Name);
        }

        private void DecryptIndex (byte[] buf, int base_offset, int length, byte last)
        {
            for (int i = 0; i < length; i++)
            {
                int key = base_offset + i + last;
                last = buf[i];
                buf[i] ^= (byte)key;
            }
        }

        private void DecryptEntry (byte[] data, uint key1, uint key2, DpkEntry entry)
        {
            for (uint i = 0; i < data.Length; ++i)
            {
                data[i] ^= (byte)(key1 + (key1 >> 8));
                data[i] -= (byte)entry.Hash;
                key1 += key2;
            }
        }

        private uint GetNameHash (byte[] name, int begin, int length, uint key1, uint key2, uint entry_size)
        {
            uint hash = 0;
            for (int i = begin+length-1; i >= begin; --i)
            {
                hash += key1 + key2 * (entry_size + name[i]);
            }
            return hash;
        }

        public override ResourceOptions GetDefaultOptions ()
        {
            return new DpkOptions {
                Key1 = Properties.Settings.Default.DPKKey1,
                Key2 = Properties.Settings.Default.DPKKey2,
            };
        }

        public override ResourceOptions GetOptions (object w)
        {
            var widget = w as GUI.WidgetDPK;
            if (null != widget)
            {
                uint result_key;
                if (uint.TryParse (widget.Key1.Text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result_key))
                    Properties.Settings.Default.DPKKey1 = result_key;
                if (uint.TryParse (widget.Key2.Text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result_key))
                    Properties.Settings.Default.DPKKey2 = result_key;
            }
            return this.GetDefaultOptions();
        }

        public override object GetAccessWidget ()
        {
            return new GUI.WidgetDPK();
        }

        public override ResourceScheme Scheme
        {
            get
            {
                var known = KnownSchemes.Clone() as DpkScheme[];
                // first entry should be a default scheme, replace localized "Name" field
                known[0] = new DpkScheme { Name = known[0].Name, Key1 = known[0].Key1, Key2 = known[0].Key2 };
                return new ArchiveScheme { KnownSchemes = known };
            }
            set
            {
                KnownSchemes = ((ArchiveScheme)value).KnownSchemes;
                if (string.IsNullOrEmpty (KnownSchemes[0].Name))
                    KnownSchemes[0].Name = arcStrings.ArcDefault;
            }
        }
    }
}
