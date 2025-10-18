//! \file       ArcZIP.cs
//! \date       Sat Mar 05 14:47:42 2016
//! \brief      PKWARE ZIP archive format.
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
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using GameRes.Formats.Kogado;
using GameRes.Formats.Strings;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static System.Net.Mime.MediaTypeNames;
using SharpZip = ICSharpCode.SharpZipLib.Zip;

namespace GameRes.Formats.PkWare
{
    internal class TexEntry : PackedEntry
    {
        public string FileName = "";
    }

    internal class ZipEntry : TexEntry
    {
        public readonly SharpZip.ZipEntry NativeEntry;

        public ZipEntry (SharpZip.ZipEntry zip_entry)
        {
            NativeEntry = zip_entry;
            Name = zip_entry.Name;
            Type = FormatCatalog.Instance.GetTypeFromName (zip_entry.Name);
            IsPacked = true;
            // design decision of having 32bit entry sizes was made early during GameRes
            // library development. nevertheless, large files will be extracted correctly
            // despite the fact that size is reported as uint.MaxValue, because extraction is
            // performed by .Net framework based on real size value.
            Size = (uint)Math.Min (zip_entry.CompressedSize, uint.MaxValue);
            UnpackedSize = (uint)Math.Min (zip_entry.Size, uint.MaxValue);
            Offset = zip_entry.Offset;
        }
    }

    internal class PkZipArchive : ArcFile
    {
        readonly SharpZip.ZipFile m_zip;

        public SharpZip.ZipFile Native { get { return m_zip; } }

        public JObject JsonObj;

        public PkZipArchive (ArcView arc, ArchiveFormat impl, ICollection<Entry> dir, SharpZip.ZipFile native)
            : base (arc, impl, dir)
        {
            m_zip = native;
        }

        #region IDisposable implementation
        bool _zip_disposed = false;
        protected override void Dispose (bool disposing)
        {
            if (!_zip_disposed)
            {
                if (disposing)
                    m_zip.Close();
                _zip_disposed = true;
            }
            base.Dispose (disposing);
        }
        #endregion
    }

    [Serializable]
    public class ZipScheme : ResourceScheme
    {
        public Dictionary<string, string> KnownKeys;
    }

    [Export(typeof(ArchiveFormat))]
    public class ZipOpener : ArchiveFormat
    {
        public override string         Tag { get { return "ZIP"; } }
        public override string Description { get { return "PKWARE archive format"; } }
        public override uint     Signature { get { return 0; } }
        public override bool  IsHierarchic { get { return true; } }
        public override bool      CanWrite { get { return true; } }

        static readonly byte[] PkDirSignature = { (byte)'P', (byte)'K', 5, 6 };

        public ZipOpener ()
        {
            Settings = new[] { ZipEncoding };
            Extensions = new string[] { "zip", "vndat", "atx" };
        }

        EncodingSetting ZipEncoding = new EncodingSetting ("ZIPEncodingCP", "DefaultEncoding");

        public override ArcFile TryOpen (ArcView file)
        {
            if (-1 == SearchForSignature (file, PkDirSignature))
                return null;
            var input = file.CreateStream();
            try
            {
                return OpenZipArchive (file, input);
            }
            catch
            {
                input.Dispose();
                throw;
            }
        }

        internal ArcFile OpenZipArchive (ArcView file, Stream input)
        {
            SharpZip.ZipStrings.CodePage = Properties.Settings.Default.ZIPEncodingCP;
            var zip = new SharpZip.ZipFile (input);
            try
            {
                var files = zip.Cast<SharpZip.ZipEntry>().Where (z => !z.IsDirectory);
                bool has_encrypted = files.Any (z => z.IsCrypted);
                if (has_encrypted)
                    zip.Password = QueryPassword (file);
                var dir = files.Select (z => new ZipEntry (z) as Entry).ToList();
                return SetJsonEntry(new PkZipArchive(file, this, dir, zip));
            }
            catch
            {
                zip.Close();
                throw;
            }
        }

        private PkZipArchive SetJsonEntry(PkZipArchive arc)
        {
            var jent = arc.Dir.FirstOrDefault(e => Path.GetExtension(e.Name) == ".json");
            if (Path.GetExtension(arc.File.Name) != ".atx" || jent == null)
                return arc;

            JObject json_obj;
            using (var input = arc.OpenEntry(jent))
            using (var reader = new StreamReader(input))
            {
                try
                {
                    json_obj = JObject.Parse(reader.ReadToEnd());
                    //Console.WriteLine(json_obj.ToString());
                    var name = "";
                    var arc_name = Path.GetFileNameWithoutExtension(arc.File.Name);
                    foreach (var obj in json_obj["Block"])
                    {
                        name = obj["filenameOld"].ToString();
                        arc.Dir.Add(new TexEntry
                        {
                            Name = name == arc_name ? name : arc_name + '_' + name,
                            Type = "image",
                            Offset = 0,
                            Size = 0,
                            FileName = name
                        } as Entry);
                    }
                    arc.JsonObj = json_obj;
                }
                catch { } //jsonの構文が想定外
            }

            arc.Dir.FirstOrDefault(e => Path.GetExtension(e.Name) == ".pb").Type = ""; //pbファイルのType修正

            return arc;
        }

        public override Stream OpenEntry (ArcFile arc, Entry entry)
        {
            var tent = (TexEntry)entry;
            if (!String.IsNullOrEmpty(tent.FileName)) //ダミーエントリー
                return null;
            var zarc = (PkZipArchive)arc;
            var zent = (ZipEntry)entry;

            return zarc.Native.GetInputStream (zent.NativeEntry);
        }

        /// <summary>
        /// Search for ZIP 'End of central directory record' near the end of file.
        /// Returns offset of 'PK' signature or -1 if no signature was found.
        /// </summary>
        internal unsafe long SearchForSignature (ArcView file, byte[] signature)
        {
            if (signature.Length < 4)
                throw new ArgumentException ("Invalid ZIP file signature", "signature");

            uint tail_size = (uint)Math.Min (file.MaxOffset, 0x10016L);
            if (tail_size < 0x16)
                return -1;
            var start_offset = file.MaxOffset - tail_size;
            using (var view = file.CreateViewAccessor (start_offset, tail_size))
            using (var pointer = new ViewPointer (view, start_offset))
            {
                byte* ptr_end = pointer.Value;
                byte* ptr = ptr_end + tail_size-0x16;
                for (; ptr >= ptr_end; --ptr)
                {
                    if (signature[3] == ptr[3] && signature[2] == ptr[2] &&
                        signature[1] == ptr[1] && signature[0] == ptr[0])
                        return start_offset + (ptr-ptr_end);
                }
                return -1;
            }
        }

        string QueryPassword (ArcView file)
        {
            var options = Query<ZipOptions> (arcStrings.ZIPEncryptedNotice);
            return options.Password;
        }

        public override IImageDecoder OpenImage(ArcFile arc, Entry entry)
        {
            var tent = (TexEntry)entry;
            try
            {
                if (!String.IsNullOrEmpty(tent.FileName)) //ダミーエントリー
                {
                    var json_obj = ((PkZipArchive)arc).JsonObj;
                    var ddict = new Dictionary<string, IImageDecoder>();
                    int canvas_width = (int)json_obj["Canvas"]["Width"];
                    int canvas_height = (int)json_obj["Canvas"]["Height"];

                    var area_list = new List<int>();
                    foreach (var block in json_obj["Block"])
                        area_list.Add((int)block["width"] * (int)block["height"]);
                    var index = area_list.IndexOf(area_list.Max());

                    int base_width = (int)json_obj["Block"][index]["width"];
                    int base_height = (int)json_obj["Block"][index]["height"];
                    int canvas_offset_x = (int)json_obj["Block"][index]["offsetX"];
                    int canvas_offset_y = (int)json_obj["Block"][index]["offsetY"];
                    
                    int byte_depth = 4;
                    int stride = base_width * byte_depth;
                    var pixels = new byte[stride * base_height];

                    foreach (var block in json_obj["Block"])
                    {
                        if (block["filenameOld"].ToString() == tent.FileName)
                        {
                            foreach (var mesh in block["Mesh"])
                            {
                                var tex_name = "tex" + mesh["texNo"].ToString();
                                if (!ddict.ContainsKey(tex_name))
                                {
                                    var tex_ent = arc.Dir.FirstOrDefault(e => e.Name.Contains(tex_name));
                                    if (tex_ent == null)
                                        return null;
                                    ddict.Add(tex_name, base.OpenImage(arc, tex_ent));
                                }
                                var rect = new Int32Rect((int)mesh["viewX"], (int)mesh["viewY"], (int)mesh["width"], (int)mesh["height"]);
                                var offset_x = (int)mesh["srcOffsetX"] + (int)block["offsetX"] - canvas_offset_x;
                                var offset_y = (int)mesh["srcOffsetY"] + (int)block["offsetY"] - canvas_offset_y;
                                var offset = offset_y * stride + offset_x * byte_depth;
                                ddict[tex_name].Image.Bitmap.CopyPixels(rect, pixels, stride, offset);
                            }

                            return new BitmapSourceDecoder(BitmapImage.Create(
                                base_width,
                                base_height,
                                ImageData.DefaultDpiX,
                                ImageData.DefaultDpiY,
                                PixelFormats.Bgra32,
                                null,
                                pixels,
                                stride
                                ));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(entry.Name);
                Console.WriteLine(ex.Message);
                Console.WriteLine(ex.StackTrace);
            }

            return base.OpenImage(arc, entry);
        }

        public override ResourceOptions GetDefaultOptions ()
        {
            return new ZipOptions {
                CompressionLevel = Properties.Settings.Default.ZIPCompression,
                FileNameEncoding = ZipEncoding.Get<Encoding>(),
                Password = Properties.Settings.Default.ZIPPassword,
            };
        }

        public override ResourceOptions GetOptions (object widget)
        {
            if (widget is GUI.WidgetZIP)
                Properties.Settings.Default.ZIPPassword = ((GUI.WidgetZIP)widget).Password.Text;
            return GetDefaultOptions();
        }

        public override object GetAccessWidget ()
        {
            return new GUI.WidgetZIP (DefaultScheme.KnownKeys);
        }

        // TODO: GUI widget for options

        public override void Create (Stream output, IEnumerable<Entry> list, ResourceOptions options,
                                     EntryCallback callback)
        {
            var zip_options = GetOptions<ZipOptions> (options);
            int callback_count = 0;
            using (var zip = new ZipArchive (output, ZipArchiveMode.Create, true, zip_options.FileNameEncoding))
            {
                foreach (var entry in list)
                {
                    var zip_entry = zip.CreateEntry (entry.Name, zip_options.CompressionLevel);
                    using (var input = File.OpenRead (entry.Name))
                    using (var zip_file = zip_entry.Open())
                    {
                        if (null != callback)
                            callback (++callback_count, entry, arcStrings.MsgAddingFile);
                        input.CopyTo (zip_file);
                    }
                }
            }
        }

        ZipScheme DefaultScheme = new ZipScheme { KnownKeys = new Dictionary<string, string>() };

        public override ResourceScheme Scheme
        {
            get { return DefaultScheme; }
            set { DefaultScheme = (ZipScheme)value; }
        }
    }

    public class ZipOptions : ResourceOptions
    {
        public CompressionLevel CompressionLevel { get; set; }
        public         Encoding FileNameEncoding { get; set; }
        public           string         Password { get; set; }
    }
}
