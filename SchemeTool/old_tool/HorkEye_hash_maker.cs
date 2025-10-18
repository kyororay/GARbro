//horkeye用の鍵データ（CRC64_WE:アーカイブ内ファイルパス）生成ツール

using System;
using System.IO;
using System.Text;
using GameRes;
using GameRes.Utility;

namespace SchemeTool
{
    class Program
    {
        static void Main(string[] args)
        {
            try
            {
                using (var sw = new StreamWriter(".\\GameData\\HorkEye_all_hash.lst", true, Encodings.cp932))
                {
                    //イベントCG
                    foreach (var a in new string[] { "", "z/" })
                    {
                        for (int b = 0; b < 1000; b++)
                        {
                            for (int c = 0; c < 100; c++)
                            {
                                for (int d = 0; d < 0xFF; d++)
                                {
                                    var name = String.Format("{0}ev/EV_{1:000}_{2:00}_{3:X2}.tlg", a, b, c, d);
                                    var name_bytes = Encodings.cp932.GetBytes(name);
                                    var crc64 = Crc64.Compute(name_bytes, 0, name_bytes.Length); //CRC64_WE
                                    sw.WriteLine(String.Format("{0:X16},{1}", crc64, name));
                                }
                                for (int d = 0; d < 10; d++)
                                {
                                    for (int e = 0; e < 25; e++)
                                    {
                                        var alp = Encodings.cp932.GetString(new byte[] { (byte)(0x41 + e) });
                                        var name = String.Format("{0}ev/EV_{1:000}_{2:00}_{3:0}{4}.tlg", a, b, c, d, alp);
                                        var name_bytes = Encodings.cp932.GetBytes(name);
                                        var crc64 = Crc64.Compute(name_bytes, 0, name_bytes.Length); //CRC64_WE
                                        sw.WriteLine(String.Format("{0:X16},{1}", crc64, name));
                                    }
                                }
                            }
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
}
