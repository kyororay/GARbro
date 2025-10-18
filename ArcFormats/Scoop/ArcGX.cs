//! \file       ArcGX.cs
//! \date       2017 Nov 25
//! \brief      Scoop resource archive.
//
// Copyright (C) 2017 by morkt
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
using GameRes.Compression;

namespace GameRes.Formats.Scoop
{
    [Export(typeof(ArchiveFormat))]
    public class GxOpener : ArchiveFormat
    {
        public override string         Tag { get { return "GX"; } }
        public override string Description { get { return "Scoop resource archive"; } }
        public override uint     Signature { get { return 0x52524150; } } // 'PARR'
        public override bool  IsHierarchic { get { return false; } }
        public override bool      CanWrite { get { return false; } }

        public GxOpener ()
        {
            Extensions = new string[] { "gx", "fx", "vx" };
        }

        public override ArcFile TryOpen (ArcView file)
        {
            if (!file.View.AsciiEqual (0, "PARROT1.0"))
                return null;
            int count = file.View.ReadInt16 (0xA);
            if (!IsSaneCount (count))
                return null;
            uint index_size = file.View.ReadUInt32 (0xC);
            if (index_size > file.View.Reserve (0, index_size))
                return null;

            uint index_offset = 0x10;
            var dir = new List<Entry> (count);
            for (int i = 0; i < count; ++i)
            {
                int compression = file.View.ReadUInt16 (index_offset);
                uint name_offset = file.View.ReadUInt32 (index_offset+2);
                if (name_offset > index_size)
                    return null;
                var name = file.View.ReadString (name_offset, index_size - name_offset);
                var entry = FormatCatalog.Instance.Create<PackedEntry> (name);
                entry.Offset = file.View.ReadUInt32 (index_offset+6);
                entry.Size   = file.View.ReadUInt32 (index_offset+0xA);
                entry.UnpackedSize = file.View.ReadUInt32 (index_offset+0xE);
                entry.IsPacked = compression > 1;
                if (!entry.CheckPlacement (file.MaxOffset))
                    return null;
                dir.Add (entry);
                index_offset += 0x12;
            }
            return new ArcFile (file, this, dir);
        }

        public override Stream OpenEntry (ArcFile arc, Entry entry)
        {
            var input = arc.File.CreateStream (entry.Offset, entry.Size);
            var pent = entry as PackedEntry;
            if (null == pent || !pent.IsPacked)
                return input;
            return new ZLibStream (input, CompressionMode.Decompress);
        }
    }
}
