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
            var arc_names = new string[]
            {
                "E:\\GARbro開発用\\HorkEye\\[210930][みなとそふと] 我が姫君に栄冠を 将軍の誘惑\\arc0.dat",
                "E:\\GARbro開発用\\HorkEye\\[210930][みなとそふと] 我が姫君に栄冠を 将軍の誘惑\\arc1.dat"
            };
            var key_dict = new Dictionary<ulong, string>();

            try
            {
                using (var sr = new StreamReader(".\\GameData\\HorkEye_all_hash.lst", Encodings.cp932))
                {
                    while (sr.Peek() != -1)
                    {
                        var line_parts = sr.ReadLine().Split(',');
                        key_dict[Convert.ToUInt64("0x" + line_parts[0], 16)] = line_parts[1];
                    }
                }
                using (var sw = new StreamWriter(".\\GameData\\HorkEye_hash.lst", true, Encodings.cp932))
                {
                    foreach (var arc_name in arc_names)
                    {
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

                            Console.WriteLine("【Progress】");
                            var error_count = 0;
                            index.m_input.Position = index.IndexPosition;
                            for (int i = 0; i < count; i++)
                            {
                                var entry = index.ReadEntry();
                                if (key_dict.ContainsKey(entry.Hash))
                                {
                                    sw.WriteLine(String.Format("{0:X16},{1}", entry.Hash, key_dict[entry.Hash]));
                                    Console.WriteLine("{0}/{1}", i + 1, count);
                                }
                                else
                                {
                                    error_count++;
                                    sw.WriteLine(String.Format("{0:X16},", entry.Hash));
                                    continue;
                                }
                            }
                            Console.WriteLine("【Result】");
                            Console.WriteLine("found hash : {0}/{1}", count - error_count, count);
                            Console.WriteLine("not found hash : {0}/{1}", error_count, count);
                            index.Dispose();
                        }
                    }
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
