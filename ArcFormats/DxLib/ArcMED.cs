//! \file       ArcMED.cs
//! \date       Mon May 30 12:35:24 2016
//! \brief      DxLib resource archive.
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
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameRes.Formats.Musica;
using GameRes.Formats.NeXAS;
using GameRes.Formats.Strings;
using GameRes.Utility;

namespace GameRes.Formats.DxLib
{
    public class MedMetaData : ImageMetaData
    {
        public string Base;
    }

    public interface IScriptEncryption
    {
        int StartOffset { get; }

        bool IsEncrypted (byte[] data);
        void Decrypt (byte[] data, int offset, int length);
    }

    [Serializable]
    public class FudegakiEncryption : IScriptEncryption
    {
        readonly byte[] Key;

        public FudegakiEncryption (string keyword)
        {
            Key = Encodings.cp932.GetBytes (keyword);
        }

        public int StartOffset { get { return 0x10; } }

        public bool IsEncrypted (byte[] data)
        {
            return LittleEndian.ToInt32 (data, 0) + 0x10 == data.Length && Key.Length > 0;
        }

        public void Decrypt (byte[] data, int offset, int length)
        {
            for (int i = 0; i < length; ++i)
            {
                data[offset+i] += Key[(offset+i) % Key.Length];
            }
        }
    }

    [Serializable]
    public class MedOptions : ResourceOptions
    {
        public IScriptEncryption Encryption;
    }

    [Serializable]
    public class ScrMedScheme : ResourceScheme
    {
        public Dictionary<string, IScriptEncryption> KnownSchemes;
    }

    internal class ScrMedArchive : ArcFile
    {
        public readonly IScriptEncryption Encryption;

        public ScrMedArchive (ArcView arc, ArchiveFormat impl, ICollection<Entry> dir, IScriptEncryption enc)
            : base (arc, impl, dir)
        {
            Encryption = enc;
        }
    }

    [Export(typeof(ArchiveFormat))]
    public class MedOpener : ArchiveFormat
    {
        public override string         Tag { get { return "MED"; } }
        public override string Description { get { return "DxLib engine resource archive"; } }
        public override uint     Signature { get { return 0; } }
        public override bool  IsHierarchic { get { return false; } }
        public override bool      CanWrite { get { return false; } }

        static readonly ResourceInstance<ImageFormat> PrsFormat = new ResourceInstance<ImageFormat> ("PRS");

        static ScrMedScheme DefaultScheme = new ScrMedScheme {
            KnownSchemes = new Dictionary<string, IScriptEncryption>()
        };
        public static Dictionary<string, IScriptEncryption> KnownSchemes { get { return DefaultScheme.KnownSchemes; } }

        public override ResourceScheme Scheme
        {
            get { return DefaultScheme; }
            set { DefaultScheme = (ScrMedScheme)value; }
        }

        public override ArcFile TryOpen (ArcView file)
        {
            if (!file.View.AsciiEqual (0, "MD"))
                return null;
            uint entry_length = file.View.ReadUInt16 (4);
            int count = file.View.ReadUInt16 (6);
            if (entry_length <= 8 || !IsSaneCount (count))
                return null;

            uint name_length = entry_length - 8;
            uint index_offset = 0x10;
            var dir = new List<Entry> (count);
            for (int i = 0; i < count; ++i)
            {
                var name = file.View.ReadString (index_offset, name_length);
                index_offset += name_length;
                uint offset = file.View.ReadUInt32 (index_offset+4);

                var entry = new AutoEntry (name, () => {
                    uint signature = file.View.ReadUInt32 (offset);
                    if (0x4259 == (signature & 0xFFFF)) // 'YB'
                        return PrsFormat.Value;
                    return AutoEntry.DetectFileType (signature);
                });
                entry.Size   = file.View.ReadUInt32 (index_offset);
                entry.Offset = offset;
                if (!entry.CheckPlacement (file.MaxOffset))
                    return null;
                dir.Add (entry);
                index_offset += 8;
            }
            var base_name = Path.GetFileNameWithoutExtension (file.Name);
            if (base_name.EndsWith ("_scr", StringComparison.OrdinalIgnoreCase)
                && KnownSchemes.Count > 0)
            {
                var encryption = QueryEncryption (file.Name);
                if (encryption != null)                                        
                    return new ScrMedArchive (file, this, dir, encryption);
            }

            //メタデータの初期化
            m_get_flag = false;
            m_metadata_dict.Clear();

            return new ArcFile (file, this, dir);
        }

        public override Stream OpenEntry (ArcFile arc, Entry entry)
        {
            var scr_arc = arc as ScrMedArchive;
            if (null == scr_arc || entry.Size <= scr_arc.Encryption.StartOffset)
                return base.OpenEntry (arc, entry);
            var data = arc.File.View.ReadBytes (entry.Offset, entry.Size);
            if (scr_arc.Encryption.IsEncrypted (data))
            {
                var offset = scr_arc.Encryption.StartOffset;
                scr_arc.Encryption.Decrypt (data, offset, data.Length-offset);
            }
            return new BinMemoryStream (data, entry.Name);
        }

        public override IImageDecoder OpenImage(ArcFile arc, Entry entry)
        {
            using (var decoder = base.OpenImage(arc, entry))
            {
                var source = decoder.Image.Bitmap;

                try
                {
                    SetParams(arc, entry);
                    if (m_metadata_dict.ContainsKey(entry.Name))
                    {
                        var info = m_metadata_dict[entry.Name];

                        //ベース画像のサイズ取得
                        if (!string.IsNullOrEmpty(info.Base))
                        {
                            var base_name = Path.GetDirectoryName(entry.Name) + info.Base;
                            var base_entry = arc.Dir.FirstOrDefault(e => e.Name.ToLower() == base_name.ToLower());
                            if (base_entry == null)
                            {
                                info.iWidth = info.OffsetX + source.PixelWidth;
                                info.iHeight = info.OffsetY + source.PixelHeight;
                            }
                            else
                            {
                                using (var input = arc.OpenImage(base_entry))
                                {
                                    info.iWidth = input.Image.Bitmap.PixelWidth;
                                    info.iHeight = input.Image.Bitmap.PixelHeight;
                                }
                            }
                        }

                        int byte_depth = info.BPP / 8;
                        int stride = info.iWidth * byte_depth;
                        int offset = info.OffsetY * stride + info.OffsetX * byte_depth;
                        var pixels = new byte[stride * info.Height];
                        source.CopyPixels(Int32Rect.Empty, pixels, stride, offset);
                        source = BitmapImage.Create(
                            info.iWidth,
                            info.iHeight,
                            ImageData.DefaultDpiX,
                            ImageData.DefaultDpiY,
                            PixelFormats.Bgra32,
                            null,
                            pixels,
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

        internal static Dictionary<string, MedMetaData> m_metadata_dict = new Dictionary<string, MedMetaData>(StringComparer.OrdinalIgnoreCase);
        internal static bool m_get_flag = false;

        private void SetParams(ArcFile arc, Entry entry)
        {
            if (!m_get_flag)
            {
                var med_dir = Path.GetDirectoryName(arc.File.Name);
                VFS.FullPath = new string[] { med_dir };

                string[] item_list;
                using (var med_arc = ArcFile.TryOpen(med_dir + "\\md_scr.med"))
                {
                    var bg_entry = med_arc.Dir.ToList().Find(e => e.Name == "_BGSET");
                    using (var input = med_arc.OpenEntry(bg_entry))
                    {
                        input.Position = 0x11;
                        if (input.ReadStringUntil(0x00, Encoding.UTF8) != "#RULE_BG2")
                        {
                            VFS.FullPath = new string[] { arc.File.Name, "" };
                            return;
                        }

                        input.Position = 0x1D;
                        using (var ms = new MemoryStream())
                        {
                            input.CopyTo(ms);
                            var data = ms.ToArray();
                            for (int i = 0; i < data.Length; ++i)
                            {
                                if (data[i] == 0x00)
                                    data[i] = 0x20;
                            }
                            item_list = Encoding.GetEncoding("UTF-8").GetString(data).Split(' ');
                        }
                    }
                }
                var name_list = arc.Dir.Select(e => e.Name);
                string prefix = "";
                string body = "";
                foreach (var item in item_list)
                {
                    if (item.StartsWith("PREFIX"))
                    {
                        prefix = item.Split(',')[1];
                    }
                    else if (item.StartsWith("BODY") && body == "")
                    {
                        var body_name = prefix + item.Split(',')[1] + ".prs";
                        if (name_list.Contains(body_name))
                            body = body_name;
                    }
                    else if (item.StartsWith("PARTS") || item.StartsWith("FACE"))
                    {
                        var parts = item.Split(',');
                        if (parts.Length != 4)
                            continue;
                        try
                        {
                            m_metadata_dict[prefix + parts[1] + ".prs"] = new MedMetaData
                            {
                                OffsetX = Convert.ToInt32(parts[2]),
                                OffsetY = Convert.ToInt32(parts[3]),
                                BPP = 32,
                                Base = body
                            };
                        }
                        catch{ } //誤記のケースがあるので、その対応
                    }
                    else if (item == "}")
                    {
                        body = "";
                    }
                }
                m_get_flag = true;
            }

            VFS.FullPath = new string[] { arc.File.Name, "" };
        }

        public override ResourceOptions GetDefaultOptions ()
        {
            return new MedOptions {
                Encryption = GetEncryption (Properties.Settings.Default.MEDScriptScheme),
            };
        }

        public override object GetAccessWidget ()
        {
            return new GUI.WidgetSCR();
        }

        public static IScriptEncryption GetEncryption (string scheme)
        {
            IScriptEncryption enc;
            if (string.IsNullOrEmpty (scheme) || !KnownSchemes.TryGetValue (scheme, out enc))
                return null;
            return enc;
        }

        IScriptEncryption QueryEncryption (string arc_name)
        {
            var title = FormatCatalog.Instance.LookupGame (arc_name);
            if (!string.IsNullOrEmpty (title) && KnownSchemes.ContainsKey (title))
                return KnownSchemes[title];
            var options = Query<MedOptions> (arcStrings.ArcEncryptedNotice);
            return options.Encryption;

            //return new FudegakiEncryption("RfCeIhYiNoOkAuUaLk");
        }
    }
}
