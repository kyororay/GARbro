//250204 K.Kimura ���ō쐬���� webp�̂ݑΉ��A���삪������ƒx��...

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

        //SakanaGL�G���W���̃A�[�J�C�u����webp�t�@�C�����������A�G���g���[���쐬
        public override ArcFile TryOpen (ArcView file)
        {
            //SakanaGL�G���W���̃A�[�J�C�u��Signature�������悤�Ȃ̂ŁA�t�@�C���g���q���画��
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
                //webp�t�@�C���̐擪�A�h���X������
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

                //�G���g���[���쐬
                var entry = FormatCatalog.Instance.Create<Entry>("Image" + j.ToString("d5") + ".webp");
                entry.Offset = addr;
                entry.Type = "image";
                entry.Size = chunk_size + 8;
                m_dir.Add(entry);
                addr = (long)(((UInt64)addr + chunk_size + 24) & 0xFFFFFFFFFFFFFFF0); //����webp���ʎq"RIFF"���������邽�߂ɃA�h���X�𒲐�
                find_flag = false;
                if (addr + 4 > file.MaxOffset)
                    break;
            }

            return new ArcFile(file, this, m_dir);
        }
    }
}
