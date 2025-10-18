//HorkEyeのインデックスデータベース（NCFileMap.idx）にタイトルを追加するツール

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameRes;
using GameRes.Formats;
using GameRes.Utility;

namespace SchemeTool
{
    class Program
    {
        static void Main(string[] args)
        {
            bool append_flag = true; //2つ目以降のアーカイブの場合はtrue
            string title_key = "我が姫君に栄冠をＦＤ 将軍の誘惑 DL版";
            string arc_name = "E:\\GARbro開発用\\HorkEye\\[210930][みなとそふと] 我が姫君に栄冠を 将軍の誘惑\\arc1.dat";
            string line;
            byte[] line_bytes;
            ulong crc64;
            Dictionary<ulong, byte[]> key_dict = new Dictionary<ulong, byte[]>();

            try
            {
                using (var sr = new StreamReader(".\\GameData\\horkeye_name.lst", Encodings.cp932))
                //using (var sw = new StreamWriter(".\\GameData\\horkeye_scheme.lst", true, Encodings.cp932)) //デバッグ用
                {
                    while (sr.Peek() != -1)
                    {
                        line = sr.ReadLine();
                        line_bytes = Encodings.cp932.GetBytes(line);
                        crc64 = Crc64.Compute(line_bytes, 0, line_bytes.Length); //CRC64_WE
                        //sw.WriteLine(String.Format("{0:X16}", crc64) + "," + line); //デバッグ用
                        key_dict[crc64] = line_bytes;
                    }
                }

                using (var file = new ArcView(arc_name))
                {
                    if (!file.Name.HasExtension(".dat"))
                        return;
                    NcIndexReaderBase index;
                    int count;
                    uint key = 0x8B6A4E5F;
                    int signature_key = 0x26ACA46E;
                    count = file.View.ReadInt32(4) ^ (int)key;
                    if (0 < count && count < 0x40000)
                        index = new NcIndexReader(file, count, key) { IndexPosition = 8 };
                    else
                    {
                        count = file.View.ReadInt32(0) ^ signature_key;
                        if (0 < count && count < 0x40000)
                            index = new NcIndexReader(file, count);
                        else
                        {
                            count = Binary.BigEndian(file.View.ReadInt32(0)) ^ signature_key;
                            if (0 < count && count < 0x40000)
                                index = new MinatoIndexReader(file, count);
                            else
                                return;
                        }
                    }

                    //count = 10;
                    
                    using (var sw = new StreamWriter(".\\GameData\\horkeye_hash.lst", true, Encodings.cp932))
                    using (var fsw = new FileStream(".\\GameData\\NCFileMap.tmp", FileMode.Append, FileAccess.Write))
                    {
                        /*if (append_flag)
                        {
                            using (var fsr = new FileStream(".\\GameData\\NCFileMap.idx", FileMode.Open, FileAccess.Read))
                            {
                                var read_bytes = new byte[fsr.Length];
                                fsr.Read(read_bytes, 0, (int)fsr.Length);
                                fsw.Write(read_bytes, 0, (int)fsr.Length);
                            }
                        }*/
                        if (!append_flag)
                        {
                            using (var idx_stream = File.OpenRead(".\\GameData\\NCFileMap.idx"))
                            using (var idx = new BinaryReader(idx_stream))
                            {
                                int scheme_count = idx.ReadInt32();
                                idx.BaseStream.Seek(12, SeekOrigin.Current);
                                var map = new Dictionary<ulong, Tuple<uint, int>>(scheme_count);
                                for (int i = 0; i < scheme_count; ++i)
                                {
                                    ulong k = idx.ReadUInt64();
                                    uint o = idx.ReadUInt32();
                                    int c = idx.ReadInt32();
                                    map[k] = Tuple.Create(o + 0x10, c);
                                }

                                fsw.Write(BitConverter.GetBytes(scheme_count + 1), 0, 4);
                                fsw.Write(Enumerable.Repeat((byte)0x00, 12).ToArray(), 0, 12);

                                foreach (var item in map)
                                {
                                    fsw.Write(BitConverter.GetBytes(item.Key), 0, 8);
                                    fsw.Write(BitConverter.GetBytes(item.Value.Item1), 0, 4);
                                    fsw.Write(BitConverter.GetBytes(item.Value.Item2), 0, 4);
                                }

                                var title_bytes = Encodings.cp932.GetBytes(title_key);

                                fsw.Write(BitConverter.GetBytes(Crc64.Compute(title_bytes, 0, title_bytes.Length)), 0, 8);
                                fsw.Write(BitConverter.GetBytes((int)idx_stream.Length + 0x10), 0, 4);
                                fsw.Write(BitConverter.GetBytes(count), 0, 4);

                                var map_length = (int)idx_stream.Length - (scheme_count + 1) * 0x10;
                                fsw.Write(idx.ReadBytes(map_length), 0, map_length);
                            }
                        }

                        Console.WriteLine("【Progress】");
                        var error_count = 0;
                        index.m_input.Position = index.IndexPosition;
                        for (int i = 0; i < count; i++)
                        {
                            var entry = index.ReadEntry();
                            if (!key_dict.ContainsKey(entry.Hash))
                            {
                                error_count++;
                                sw.WriteLine(String.Format("{0:X16},", entry.Hash));
                                continue;
                            }

                            fsw.Write(BitConverter.GetBytes(entry.Hash), 0, 8); //hash 8バイト

                            using (var fsd = new FileStream(".\\GameData\\NCFileMap.dat", FileMode.Open, FileAccess.Read))
                            {
                                var read_bytes = new byte[fsd.Length];
                                fsd.Read(read_bytes, 0, (int)fsd.Length);
                                int adrs = 0;
                                
                                var span = read_bytes.AsSpan();
                                for (int j = 0; j < read_bytes.Length - key_dict[entry.Hash].Length; ++j)
                                {
                                    if (span.Slice(j, key_dict[entry.Hash].Length).ToArray().SequenceEqual(key_dict[entry.Hash]))
                                        adrs = j;
                                }

                                fsw.Write(BitConverter.GetBytes(adrs), 0, 4);
                                fsw.Write(BitConverter.GetBytes(key_dict[entry.Hash].Length), 0, 4);
                            }
                            sw.WriteLine(String.Format("{0:X16},{1}", entry.Hash, Encodings.cp932.GetString(key_dict[entry.Hash])));
                            Console.WriteLine("{0}/{1}", i + 1, count);
                        }
                        Console.WriteLine("【Result】");
                        Console.WriteLine("found hash : {0}/{1}", count - error_count, count);
                        Console.WriteLine("not found hash : {0}/{1}", error_count, count);
                    }
                    index.Dispose();

                    byte[] bytes;
                    using (var fsr = new FileStream(".\\GameData\\NCFileMap.tmp", FileMode.Open, FileAccess.Read))
                    {
                        bytes = new byte[fsr.Length];
                        fsr.Read(bytes, 0, (int)fsr.Length);
                        int scheme_count = ReadInt(bytes, 0);
                        int offset_adrs = scheme_count * 0x10 + 8;
                        int count_adrs = scheme_count * 0x10 + 12;
                        var count_bytes = BitConverter.GetBytes(((int)fsr.Length - ReadInt(bytes, offset_adrs)) / 0x10);
                        for (int i = 0; i < 4; ++i)
                            bytes[count_adrs + i] = count_bytes[i];                            
                    }
                    using (var fsw = new FileStream(".\\GameData\\NCFileMap.tmp", FileMode.Create, FileAccess.Write))
                        fsw.Write(bytes, 0, bytes.Length);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("<---------- Error message ---------->");
                Console.WriteLine(ex.Message);
                Console.WriteLine(ex.StackTrace);
            }

            Console.Write("\nPress <Enter> to exit... ");
            while (Console.ReadKey().Key != ConsoleKey.Enter) { }

        }

        private static int ReadInt(byte[] bytes, int offset)
        {
            int data = 0x00000000;
            for (int i = 3; i >= 0; --i)
                data = (data << 8) | bytes[offset + i];
            return data;
        }
    }

    internal class ArcDatEntry : PackedEntry
    {
        public ulong Hash;
        public int Flags;
    }

    internal abstract class NcIndexReaderBase : IDisposable
    {
        internal IBinaryStream m_input;
        private List<Entry> m_dir;
        private int m_count;
        private ArcView m_file;

        public long IndexPosition { get; set; }
        public long MaxOffset { get { return m_file.MaxOffset; } }
        public bool ExtendByteSign { get; protected set; }

        protected NcIndexReaderBase(ArcView file, int count)
        {
            m_input = file.CreateStream();
            m_dir = new List<Entry>(count);
            m_count = count;
            m_file = file;
            IndexPosition = 4;
        }

        internal abstract ArcDatEntry ReadEntry();

        #region IDisposable Members
        bool m_disposed = false;
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!m_disposed)
            {
                m_input.Dispose();
                m_disposed = true;
            }
        }
        #endregion
    }

    internal class NcIndexReader : NcIndexReaderBase
    {
        readonly uint m_master_key;

        public NcIndexReader(ArcView file, int count, uint master_key = 0) : base(file, count)
        {
            m_master_key = master_key;
        }

        internal override ArcDatEntry ReadEntry()
        {
            var hash = m_input.ReadUInt64();
            int flags = m_input.ReadByte() ^ (byte)hash;
            return new ArcDatEntry
            {
                Hash = hash,
                Flags = flags,
                Offset = m_input.ReadUInt32() ^ (uint)hash ^ m_master_key,
                Size = m_input.ReadUInt32() ^ (uint)hash,
                UnpackedSize = m_input.ReadUInt32() ^ (uint)hash,
                IsPacked = 0 != (flags & 2),
            };
        }
    }

    internal class MinatoIndexReader : NcIndexReaderBase
    {
        public MinatoIndexReader(ArcView file, int count) : base(file, count)
        {
            ExtendByteSign = true;
        }

        internal override ArcDatEntry ReadEntry()
        {
            uint key = Binary.BigEndian(m_input.ReadUInt32());
            int flags = m_input.ReadUInt8() ^ (byte)key;
            uint offset = Binary.BigEndian(m_input.ReadUInt32()) ^ key;
            uint packed_size = Binary.BigEndian(m_input.ReadUInt32()) ^ key;
            uint unpacked_size = Binary.BigEndian(m_input.ReadUInt32()) ^ key;
            return new ArcDatEntry
            {
                Hash = key,
                Flags = flags,
                Offset = offset,
                Size = packed_size,
                UnpackedSize = unpacked_size,
                IsPacked = 0 != (flags & 2),
            };
        }
    }
}
