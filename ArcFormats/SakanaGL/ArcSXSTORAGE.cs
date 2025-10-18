//250204 K.Kimura 初版作成完了 webpのみ対応、動作がちょっと遅い...

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;

namespace GameRes.Formats.SakanaGL
{
    [Export(typeof(ArchiveFormat))]
    public class SxstorageOpener : ArchiveFormat
    {
        public override string         Tag { get { return "SXSTORAGE/SakanaGL"; } }
        public override string Description { get { return "SakanaGL resource archive"; } }
        public override uint     Signature { get { return 0; } }
        public override bool  IsHierarchic { get { return false; } }
        public override bool      CanWrite { get { return false; } }

        //SakanaGLエンジンのアーカイブからwebpファイルを検索し、エントリーを作成
        public override ArcFile TryOpen (ArcView file)
        {
            //SakanaGLエンジンのアーカイブはSignatureが無いようなので、ファイル拡張子から判定
            if (file.Name.Split('.').Last() != "sxstorage")
                return null;

            long count = 0;
            long addr = 0x0;
            uint chunk_size = 0x0;
            bool find_flag = false;

            List<Entry> m_dir = new List<Entry>();

            for (var j = 0; j >= 0; j++)
            {
                count = file.MaxOffset - addr;
                //webpファイルの先頭アドレスを検索
                for (var i = 0; i < count; i = i + 16)
                {
                    if (file.View.ReadString(addr + i, 4) == "RIFF")
                    {
                        if (file.View.ReadString(addr + i + 8, 4) == "WEBP")
                        {
                            addr = addr + i;
                            find_flag = true;
                            break;
                        }
                    }
                }
                if (!find_flag)
                    break;

                chunk_size = file.View.ReadUInt32(addr + 4);

                //エントリーを作成
                var entry = FormatCatalog.Instance.Create<Entry>("Image" + j.ToString("d5") + ".webp");
                entry.Offset = addr;
                entry.Type = "image";
                entry.Size = chunk_size + 8;
                m_dir.Add(entry);
                addr = (long)(((UInt64)addr + chunk_size + 24) & 0xFFFFFFFFFFFFFFF0); //次のwebp識別子"RIFF"を検索するためにアドレスを調整
                find_flag = false;
                if (addr + 4 > file.MaxOffset)
                    break;
            }

            return new ArcFile(file, this, m_dir);
        }
    }
}
