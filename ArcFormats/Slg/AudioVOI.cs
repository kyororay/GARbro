//! \file       AudioVOI.cs
//! \date       Fri Mar 11 23:55:31 2016
//! \brief      SLG system obfuscated OGG file.
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

using System.ComponentModel.Composition;
using System.IO;

namespace GameRes.Formats.Slg
{
    [Export(typeof(AudioFormat))]
    public class VoiAudio : AudioFormat
    {
        public override string         Tag { get { return "VOI"; } }
        public override string Description { get { return "SLG system obfuscated Ogg audio"; } }
        public override uint     Signature { get { return 0; } } // 'OggS'

        public override SoundInput TryOpen (IBinaryStream file)
        {
            file.Position = 0x1E;
            int offset = file.ReadByte();
            if (offset <= 0)
                return null;
            file.Position = 0x20 + offset;
            if (!(file.ReadByte() == 'O' && file.ReadByte() == 'g' &&
                  file.ReadByte() == 'g' && file.ReadByte() == 'S'))
                return null;
            return new OggInput (new StreamRegion (file.AsStream, 0x20+offset));
        }
    }
}
