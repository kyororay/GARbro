//! \file       ArcPFS.cs
//! \date       Tue Dec 27 22:27:58 2016
//! \brief      Artemis engine resource archive.
//
// Copyright (C) 2016-2017 by morkt
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
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows;
using System.Runtime.InteropServices;
using System.Linq;
using System;
using GameRes;
using System.Runtime.InteropServices.ComTypes;
using static System.Net.Mime.MediaTypeNames;
using GARbro.GUI;
using static ICSharpCode.SharpZipLib.Zip.ExtendedUnixData;

namespace GameRes.Formats.Artemis
{
    internal class PfsMetaData : ImageMetaData
    {
        public int EnBaseWidth;
        public int EnBaseHeight;
        public int ScrOffsetX;
        public int ScrOffsetY;
        public bool Flag;
    }

    [Export(typeof(ArchiveFormat))]
    public class PfsOpener : ArchiveFormat
    {
        public override string Tag { get { return "PFS"; } }
        public override string Description { get { return "Artemis engine resource archive"; } }
        public override uint Signature { get { return 0; } }
        public override bool IsHierarchic { get { return true; } }
        public override bool CanWrite { get { return false; } }

        public PfsOpener()
        {
            Extensions = new string[] { "pfs", "000", "001", "002", "003", "004", "005", "010" };
            ContainedFormats = new string[] { "PNG", "JPEG", "IPT", "OGG", "TXT", "SCR" };
            Settings = new[] { PfsEncoding };
        }

        internal static PfsMetaData m_info = new PfsMetaData { Flag = false };

        EncodingSetting PfsEncoding = new EncodingSetting("PFSEncodingCP", "DefaultEncoding");

        public override ArcFile TryOpen(ArcView file)
        {
            if (!file.View.AsciiEqual(0, "pf"))
                return null;
            int version = file.View.ReadByte(2) - '0';
            switch (version)
            {
                case 6:
                case 8:
                    try
                    {
                        return OpenPf(file, version, PfsEncoding.Get<Encoding>());
                    }
                    catch (System.ArgumentException)
                    {
                        return OpenPf(file, version, GetAltEncoding());
                    }
                case 2: return OpenPf2(file);
                default: return null;
            }
        }

        ArcFile OpenPf(ArcView file, int version, Encoding encoding)
        {
            uint index_size = file.View.ReadUInt32(3);
            int count = file.View.ReadInt32(7);
            if (!IsSaneCount(count) || 7L + index_size > file.MaxOffset)
                return null;
            var index = file.View.ReadBytes(7, index_size);
            int index_offset = 4;
            var dir = new List<Entry>(count);
            for (int i = 0; i < count; ++i)
            {
                int name_length = index.ToInt32(index_offset);
                var name = encoding.GetString(index, index_offset + 4, name_length);
                index_offset += name_length + 8;
                var entry = Create<Entry>(name);
                entry.Offset = index.ToUInt32(index_offset);
                entry.Size = index.ToUInt32(index_offset + 4);
                if (!entry.CheckPlacement(file.MaxOffset))
                    return null;
                index_offset += 8;
                dir.Add(entry);
            }
            if (version != 8 && version != 9 && version != 4 && version != 5)
                return new ArcFile(file, this, dir);

            // key calculated for archive versions 4, 5, 8 and 9
            using (var sha1 = SHA1.Create())
            {
                var key = sha1.ComputeHash(index);
                return new PfsArchive(file, this, dir, key);
            }
        }

        ArcFile OpenPf2(ArcView file)
        {
            uint index_size = file.View.ReadUInt32(3);
            int count = file.View.ReadInt32(0xB);
            if (!IsSaneCount(count) || 7L + index_size > file.MaxOffset)
                return null;
            var index = file.View.ReadBytes(7, index_size);
            int index_offset = 8;
            var dir = new List<Entry>(count);
            for (int i = 0; i < count; ++i)
            {
                int name_length = index.ToInt32(index_offset);
                var name = Encodings.cp932.GetString(index, index_offset + 4, name_length);
                index_offset += name_length + 0x10;
                var entry = Create<Entry>(name);
                entry.Offset = index.ToUInt32(index_offset);
                entry.Size = index.ToUInt32(index_offset + 4);
                if (!entry.CheckPlacement(file.MaxOffset))
                    return null;
                index_offset += 8;
                dir.Add(entry);
            }
            return new ArcFile(file, this, dir);
        }

        public override Stream OpenEntry(ArcFile arc, Entry entry)
        {
            var parc = arc as PfsArchive;
            var input = arc.File.CreateStream(entry.Offset, entry.Size, entry.Name);
            if (null == parc)
                return input;
            return new ByteStringEncryptedStream(input, parc.Key);
        }

        public override IImageDecoder OpenImage(ArcFile arc, Entry entry)
        {
            var decoder = base.OpenImage(arc, entry);
            //しろくまだんごの判定 ⇒ *.csvファイルの有無
            if (arc.Dir.FirstOrDefault(e => e.Name.Contains(".csv")) == null)
                return decoder;
            
            try
            {
                bool is_enlarged_base = Path.GetFileName(entry.Name) == "1.png";
                bool is_enlarged = Path.GetFileName(Path.GetDirectoryName(entry.Name)) == "拡大" || is_enlarged_base;

                SetParams(arc, entry, is_enlarged);
                if (!m_info.Flag)
                    return decoder;

                BitmapSource source;
                if (is_enlarged_base)
                    source = CreateEnlargedBase(arc, entry);
                else
                    source = decoder.Image.Bitmap;

                m_info.iWidth = source.PixelWidth;
                m_info.iHeight = source.PixelHeight;

                int base_width, base_height;
                if (is_enlarged)
                {
                    base_width = m_info.EnBaseWidth;
                    base_height = m_info.EnBaseHeight;
                }
                else
                {
                    base_width = m_info.iBaseWidth;
                    base_height = m_info.iBaseHeight;
                }

                int byte_depth = m_info.BPP / 8; //ビット深度（バイト換算）
                int stride = base_width * byte_depth; //1行当たりのバイト数
                var pixels = new byte[stride * base_height];
                int offset;
                Int32Rect source_rect; //ソース画像の切り取り領域

                if (m_info.Width == base_width + m_info.ScrOffsetX * 2 && m_info.Height == base_height + m_info.ScrOffsetY * 2) //ベース画像
                {
                    source_rect = new Int32Rect(m_info.ScrOffsetX, m_info.ScrOffsetY, base_width, base_height);
                    offset = 0;
                }
                else if ( //差分画像(メタデータ正常)
                    base_width + m_info.ScrOffsetX * 2 > m_info.Width + m_info.OffsetX &&
                    base_height + m_info.ScrOffsetY * 2 > m_info.Height + m_info.OffsetY &&
                    m_info.OffsetX != -1 && m_info.OffsetY != -1 &&
                    !(is_enlarged && m_info.EnBaseWidth == 0 && m_info.EnBaseHeight == 0)
                    )
                {
                    source_rect = Int32Rect.Empty;
                    offset = (m_info.OffsetY - m_info.ScrOffsetY) * stride + (m_info.OffsetX - m_info.ScrOffsetX) * byte_depth;
                }
                else //差分画像(メタデータ異常)
                    return decoder;


                source.CopyPixels(source_rect, pixels, stride, offset);
                source = BitmapImage.Create(
                    base_width,
                    base_height,
                    ImageData.DefaultDpiX,
                    ImageData.DefaultDpiY,
                    PixelFormats.Bgra32,
                    null,
                    pixels,
                    stride
                    );

                //decoder.Image.Bitmap = source;
                decoder = new BitmapSourceDecoder(source);
            }
            catch (Exception e)
            {
                Console.WriteLine(entry.Name);
                Console.WriteLine(e.Message);
                Console.WriteLine(e.StackTrace);
            }

            return decoder;
        }

        //メタデータ取得 for しろくまだんご
        private void SetParams(ArcFile arc, Entry entry, bool is_enlarged)
        {
            if (!m_info.Flag) //画像データ共通設定を未取得
            {
                try
                {
                    int scr_offset_x = 0, scr_offset_y = 0;

                    //ベース画像サイズ取得(root.pfs\system.ini)
                    var pfs_dir = Path.GetDirectoryName(arc.File.Name);
                    VFS.FullPath = new string[] { pfs_dir };
                    var pfs_arc = ArcFile.TryOpen(pfs_dir + "\\root.pfs");
                    using (var input = pfs_arc.OpenEntry(pfs_arc.Dir.FirstOrDefault(e => e.Name == "system.ini")))
                    {
                        var ini = ReadIniConfig(input.ReadCString());
                        m_info.iBaseWidth = Convert.ToInt32(ini["WIDTH"]);
                        m_info.iBaseHeight = Convert.ToInt32(ini["HEIGHT"]);
                    }

                    //スクリーンオフセット取得(root.pfs\system\config.iet)
                    using (var input = pfs_arc.OpenEntry(pfs_arc.Dir.FirstOrDefault(e => e.Name == "system\\config.iet")))
                    {
                        (scr_offset_x, scr_offset_y) = ReadIetConfig(input.ReadCString());
                        m_info.ScrOffsetX = scr_offset_x;
                        m_info.ScrOffsetY = scr_offset_y;
                    }

                    m_info.BPP = 32;
                    m_info.Flag = true;
                }
                catch
                {
                    m_info.Flag = false;
                }
                finally
                {
                    //元のアーカイブ、ディレクトリに戻す処理が必要
                    VFS.FullPath = new string[] { arc.File.Name, "" };
                    VFS.ChDir(Path.GetDirectoryName(entry.Name));
                }
            }

            //拡大画像サイズ取得(txt)
            if (is_enlarged)
            {
                bool exist;
                string txt_name = "";
                if (Path.GetFileName(entry.Name) == "1.png")
                {
                    txt_name = Path.GetDirectoryName(Path.GetDirectoryName(entry.Name)) + "\\" +
                        Path.GetFileName(Path.GetDirectoryName(entry.Name)) + ".txt";
                    exist = arc.Dir.ToList().Exists(e => e.Name == txt_name);
                }
                else
                {
                    var txt_entry = arc.Dir.Where(e => e.Name.Contains(Path.GetDirectoryName(entry.Name) + "\\")).FirstOrDefault(e => Path.GetExtension(e.Name) == ".txt");
                    exist = txt_entry != null;
                    if (exist)
                        txt_name = txt_entry.Name;
                }
                if (!exist)
                {
                    m_info.EnBaseWidth = 0;
                    m_info.EnBaseHeight = 0;
                    return;
                }
                Entry txt_ent = arc.Dir.ToList().Find(e => e.Name == txt_name);
                using (var input = arc.OpenEntry(txt_ent))
                {
                    (int canvas_width, int canvas_height) = ReadTxtConfig(input.ReadCString());
                    m_info.EnBaseWidth = canvas_width - m_info.ScrOffsetX * 2;
                    m_info.EnBaseHeight = canvas_height - m_info.ScrOffsetY * 2;
                }
            }

            //画像オフセット取得(csv)
            string csv_name = Path.ChangeExtension(entry.Name, "csv");
            if (!arc.Dir.ToList().Exists(e => e.Name == csv_name))
            {
                m_info.OffsetX = -1;
                m_info.OffsetY = -1;
                return;
            }

            Entry csv_ent = arc.Dir.ToList().Find(e => e.Name == csv_name);
            using (var input = arc.OpenEntry(csv_ent))
            {
                string[] pos = input.ReadCString().Split(',');
                m_info.OffsetX = Convert.ToInt32(pos[0]);
                m_info.OffsetY = Convert.ToInt32(pos[1]);
                //OffsetにScrOffsetが付加されていないケースの暫定対応
                if (m_info.OffsetX < m_info.ScrOffsetX || m_info.OffsetY < m_info.ScrOffsetY)
                {
                    m_info.OffsetX += m_info.ScrOffsetX;
                    m_info.OffsetY += m_info.ScrOffsetY;
                }
            }
        }

        //拡大版ベース画像の作成
        private BitmapSource CreateEnlargedBase(ArcFile arc, Entry entry)
        {
            int base_width = m_info.EnBaseWidth + m_info.ScrOffsetX * 2;
            int base_height = m_info.EnBaseHeight + m_info.ScrOffsetY * 2;
            int column = (int)(Math.Ceiling((double)base_width / 2048));

            int byte_depth = m_info.BPP / 8;
            int stride = base_width * byte_depth;
            var pixels = new byte[stride * base_height];
            int offset;

            BitmapSource source;
            foreach (var e in (arc.Dir.Where(e => e.Name.Contains(Path.GetDirectoryName(entry.Name) + "\\"))))
            {
                int index = Convert.ToInt32(Path.GetFileName(e.Name).Replace(".png", "")) - 1;
                source = base.OpenImage(arc, e).Image.Bitmap;
                offset = index / column * 2048 * stride + index % column * 2048 * byte_depth;
                source.CopyPixels(Int32Rect.Empty, pixels, stride, offset);
            }

            source = BitmapImage.Create(
                base_width,
                base_height,
                ImageData.DefaultDpiX,
                ImageData.DefaultDpiY,
                PixelFormats.Bgra32,
                null,
                pixels,
                stride
                );

            return source;
        }

        //渡したiniファイルのテキストからWINDOWSラベルのパラメータを抽出し、辞書型で返す
        private Dictionary<string, string> ReadIniConfig(string text)
        {
            var value = new Dictionary<string, string>();
            var line = text.Replace(" ", "").Replace("\r\n", "\n").Split('\n').Reverse();
            string[] split;

            foreach (string t in line)
            {
                if (t.Length == 0 || t[0] == ';') //改行, コメント
                    continue;
                if (t[0] == '[' && t.Last() == ']') //ラベル
                {
                    if (t == "[WINDOWS]")
                        break;
                    else
                    {
                        value.Clear();
                        continue;
                    }
                }
                split = t.Split('=');
                if (split.Length != 2)
                    continue;
                value.Add(split[0], split[1]);
            }

            return value;
        }

        //渡したietファイルのテキストからoffscreen.x, offscreen.yの設定値を抽出し、int型のタプルで返す
        private (int, int) ReadIetConfig(string text)
        {
            int? scr_offset_x = null, scr_offset_y = null;
            var lines = text.Replace("\r\n", "\n").Split('\n');
            foreach (string line in lines)
            {
                if (line.Contains("offscreen.x"))
                {
                    var parts = line.Split('"');
                    scr_offset_x = Convert.ToInt32(parts[parts.Length - 2]);
                }
                else if (line.Contains("offscreen.y"))
                {
                    var parts = line.Split('"');
                    scr_offset_y = Convert.ToInt32(parts[parts.Length - 2]);
                }
                if (scr_offset_x != null && scr_offset_y != null)
                    break;
            }
            if (scr_offset_x == null || scr_offset_y == null)
                throw new Exception("offscreen parameter is not found");

            return ((int)scr_offset_x, (int)scr_offset_y);
        }

        //渡したtxtファイルのテキストからキャンバスサイズ(拡大図)の設定値を抽出し、int型のタプルで返す
        private (int, int) ReadTxtConfig(string text)
        {
            int? canvas_width = null, canvas_height = null;
            var lines = text.Replace("\r\n", "\n").Split('\n');
            foreach (string line in lines)
            {
                if (line.Contains("t.CG.width"))
                    canvas_width = Convert.ToInt32(line.Split('"')[3]);
                else if (line.Contains("t.CG.height"))
                    canvas_height = Convert.ToInt32(line.Split('"')[3]);
                if (canvas_width != null && canvas_height != null)
                    break;
            }
            if (canvas_width == null || canvas_height == null)
                throw new Exception("canvas size parameter is not found");

            return ((int)canvas_width, (int)canvas_height);

        }

        Encoding GetAltEncoding()
        {
            var enc = PfsEncoding.Get<Encoding>();
            if (enc.CodePage == 932)
                return Encoding.UTF8;
            else
                return Encodings.cp932;
        }
    }

    internal class PfsArchive : ArcFile
    {
        public readonly byte[] Key;

        public PfsArchive(ArcView arc, ArchiveFormat impl, ICollection<Entry> dir, byte[] key)
            : base(arc, impl, dir)
        {
            Key = key;
        }
    }
}
