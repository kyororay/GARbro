
using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Runtime.Remoting.Messaging;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameRes.Formats.Entis;
using GameRes.Formats.Musica;
using GameRes.Utility;

namespace GameRes.Formats.Turumiku
{
    [Export(typeof(ImageFormat))]
    public class Po2Format : ImageFormat
    {
        public override string Tag { get { return "PO2"; } }
        public override string Description { get { return "Turumiku script engine image format"; } }
        public override uint Signature { get { return 0x474E5089; } }

        public Po2Format()
        {
            Extensions = new string[] { "po2" };
        }

        //ImagePNG‚»‚Ì‚Ü‚Ü
        public override ImageMetaData ReadMetaData(IBinaryStream file)
        {
            file.ReadUInt32();
            if (file.ReadUInt32() != 0x0a1a0a0d)
                return null;
            uint chunk_size = Binary.BigEndian(file.ReadUInt32());
            byte[] chunk_type = file.ReadBytes(4);
            if (!Binary.AsciiEqual(chunk_type, "IHDR"))
                return null;

            var meta = new ImageMetaData();
            meta.Width = Binary.BigEndian(file.ReadUInt32());
            meta.Height = Binary.BigEndian(file.ReadUInt32());
            int bpp = file.ReadByte();
            if (bpp != 1 && bpp != 2 && bpp != 4 && bpp != 8 && bpp != 16)
                return null;
            int color_type = file.ReadByte();
            switch (color_type)
            {
                case 2: meta.BPP = bpp * 3; break;
                case 3: meta.BPP = 24; break;
                case 4: meta.BPP = bpp * 2; break;
                case 6: meta.BPP = bpp * 4; break;
                case 0: meta.BPP = bpp; break;
                default: return null;
            }
            SkipBytes(file, 7);

            for (; ; )
            {
                chunk_size = Binary.BigEndian(file.ReadUInt32());
                file.Read(chunk_type, 0, 4);
                if (Binary.AsciiEqual(chunk_type, "IDAT") || Binary.AsciiEqual(chunk_type, "IEND"))
                    break;
                if (Binary.AsciiEqual(chunk_type, "oFFs"))
                {
                    int x = Binary.BigEndian(file.ReadInt32());
                    int y = Binary.BigEndian(file.ReadInt32());
                    if (0 == file.ReadByte())
                    {
                        meta.OffsetX = x;
                        meta.OffsetY = y;
                    }
                    break;
                }
                SkipBytes(file, chunk_size + 4);
            }
            return meta;
        }

        //ImagePNG‚»‚Ì‚Ü‚Ü
        void SkipBytes(IBinaryStream file, uint num)
        {
            if (file.CanSeek)
                file.Seek(num, SeekOrigin.Current);
            else
            {
                for (int i = 0; i < num / 4; ++i)
                    file.ReadInt32();
                for (int i = 0; i < num % 4; ++i)
                    file.ReadByte();
            }
        }

        //ImagePNG‚»‚Ì‚Ü‚Ü
        public override ImageData Read(IBinaryStream file, ImageMetaData info)
        {
            var decoder = new PngBitmapDecoder(file.AsStream,
                BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames[0];
            frame.Freeze();
            return new ImageData(frame, info);
        }

        public override void Write(Stream file, ImageData image)
        {
            throw new System.NotImplementedException("Po2Format.Write not implemented");
        }
    }
}
