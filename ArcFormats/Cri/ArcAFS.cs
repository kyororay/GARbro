//! \file       ArcAFS.cs
//! \date       Tue Jan 06 10:27:51 2015
//! \brief      PS2 archive implementation.
//
// Copyright (C) 2014-2016 by morkt
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

using System.Collections.Generic;
using System.ComponentModel.Composition;

namespace GameRes.Formats.Cri
{
    [Export(typeof(ArchiveFormat))]
    public class AfsOpener : ArchiveFormat
    {
        public override string         Tag { get { return "AFS"; } }
        public override string Description { get { return "PS2 resource archive"; } }
        public override uint     Signature { get { return 0x00534641; } } // "AFS"
        public override bool  IsHierarchic { get { return false; } }
        public override bool      CanWrite { get { return false; } }

        public override ArcFile TryOpen (ArcView file)
        {
            int count = file.View.ReadInt32 (4);
            if (count <= 0 || count > 0xfffff)
                return null;
            uint index_offset = 8;
            var dir = new List<Entry> (count);
            for (int i = 0; i < count; ++i)
            {
                uint offset = file.View.ReadUInt32 (index_offset);
                uint size = file.View.ReadUInt32 (index_offset+4);
                var entry = new Entry { Offset = offset, Size = size };
                if (!entry.CheckPlacement (file.MaxOffset))
                    return null;
                dir.Add (entry);
                index_offset += 8;
            }
            var last_entry = dir[count-1];
            index_offset = (uint)AlignOffset (last_entry.Offset + last_entry.Size);
            for (int i = 0; i < count; ++i)
            {
                var name = file.View.ReadString (index_offset, 0x20);
                if (string.IsNullOrEmpty (name))
                    return null;
                dir[i].Name = name;
                dir[i].Type = FormatCatalog.Instance.GetTypeFromName (name);
                index_offset += 0x30;
            }
            return new ArcFile (file, this, dir);
        }

        long AlignOffset (long offset, uint block_size = 0x800)
        {
            offset += block_size - 1;
            return offset & ~(long)(block_size - 1);
        }
    }
}
