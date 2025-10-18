﻿//2015年ぐらいの椎名里緒はexeファイルがxorで暗号化されているので、そのデコード用プログラム

using System;
using System.IO;

namespace SchemeTool
{
    class Program
    {
        static void Main(string[] args)
        {
            try
            {
                var dir = "E:\\GARbro開発用\\椎名里緒\\[150424][D：drive.] ツゴウノイイアイドル\\解析";

                //シュウカツ家庭教師
                //uint seed = 0x08FD39FC5;
                //int code_length = 0x9FA95; //program(.text)
                //int data_length = 0x30000; //data(.data)

                //ソーサリージョーカーズ
                //uint seed = 0x6D87EF0F;
                //int code_length = 0x9FBF5; //program(.text)
                //int data_length = 0x30000; //data(.data)

                //ツゴウノイイアイドル
                uint seed = 0x716B756B;
                int code_length = 0x9F8E5; //program(.text)
                int data_length = 0x30000; //data(.data)

                //コード領域(.text)
                byte[] code_bin = new byte[code_length];
                using (var fs = new FileStream(dir + "\\encoded_program", FileMode.Open))
                {
                    fs.Read(code_bin, 0, code_length);
                }

                for (int i = 0; i < code_length; ++i)
                {
                    seed = seed + seed * 4 + 0x3711;
                    code_bin[i] ^= Convert.ToByte(seed & 0x000000FF);
                }

                using (var fs = new FileStream(dir + "\\program", FileMode.Create, FileAccess.Write))
                {
                    fs.Write(code_bin, 0, code_length);
                }

                //変数領域(.data)
                byte[] data_bin = new byte[data_length];
                using (var fs = new FileStream(dir + "\\encoded_data", FileMode.Open))
                {
                    fs.Read(data_bin, 0, data_length);
                }

                for (int i = 0; i < data_length; ++i)
                {
                    seed = seed + seed * 4 + 0x3711;
                    data_bin[i] ^= Convert.ToByte(seed & 0x000000FF);
                }

                using (var fs = new FileStream(dir + "\\data", FileMode.Create, FileAccess.Write))
                {
                    fs.Write(data_bin, 0, data_length);
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
