//! \file       KiriKiriCx.cs
//! \date       Sun Sep 07 06:50:11 2014
//! \brief      KiriKiri Cx encryption scheme implementation.
//
// Copyright (C) 2014-2016 by morkt
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
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Policy;
using System.Text;
using System.Windows.Controls;

namespace GameRes.Formats.KiriKiri
{
    public class CxProgramException : ApplicationException
    {
        public CxProgramException (string message) : base (message)
        {
        }
    }

    [Serializable]
    public class CxScheme
    {
        public uint     Mask;
        public uint     Offset;

        public byte[]   PrologOrder;
        public byte[]   OddBranchOrder;
        public byte[]   EvenBranchOrder;

        public uint[]   ControlBlock;
        public string   TpmFileName;
    }

    [Serializable]
    public class CxEncryption : ICrypt
    {
        protected uint  m_mask;
        protected uint  m_offset;

        protected byte[]     PrologOrder;
        protected byte[]  OddBranchOrder;
        protected byte[] EvenBranchOrder;

        protected uint[] ControlBlock;
        protected string TpmFileName;

        [NonSerialized]
        CxProgram[] m_program_list = new CxProgram[0x80];

        [OnDeserialized()]
        void PostDeserialization (StreamingContext context)
        {
            m_program_list = new CxProgram[0x80];
        }

        public CxEncryption (CxScheme scheme)
        {
            m_mask = scheme.Mask;
            m_offset = scheme.Offset;

            PrologOrder = scheme.PrologOrder;
            OddBranchOrder = scheme.OddBranchOrder;
            EvenBranchOrder = scheme.EvenBranchOrder;

            ControlBlock = scheme.ControlBlock;
            TpmFileName = scheme.TpmFileName;
        }

        public void MenberWriteLine()
        {
            Console.WriteLine("    m_mask : 0x{0:X8}", m_mask);
            Console.WriteLine("    m_offset : 0x{0:X8}", m_offset);
            Console.WriteLine("    PrologOrder : [{0}]", String.Join(", ", PrologOrder));
            Console.WriteLine("    OddBranchOrder : [{0}]", String.Join(", ", OddBranchOrder));
            Console.WriteLine("    EvenBranchOrder : [{0}]", String.Join(", ", EvenBranchOrder));
            if (ControlBlock == null)
                Console.WriteLine("    ControlBlock : null");
            else
                Console.WriteLine("    ControlBlock : [0x{0}]", string.Join(", 0x", ControlBlock.ToList().Select(num => Convert.ToString(num, 16).ToUpper())));
            Console.WriteLine("    TpmFileName : {0}", TpmFileName ?? "null");
        }

        public override string ToString ()
        {
            return string.Format ("{0}(0x{1:X}, 0x{2:X})", base.ToString(), m_mask, m_offset);
        }

        static readonly byte[] s_ctl_block_signature = Encoding.ASCII.GetBytes (" Encryption control block");

        /// <summary>
        /// Look for control block within specified TPM plugin file.
        /// </summary>

        public override void Init (ArcFile arc)
        {
            if (ControlBlock != null)
                return;
            if (string.IsNullOrEmpty (TpmFileName))
                throw new InvalidEncryptionScheme();

            var dir_name = VFS.GetDirectoryName (arc.File.Name);
            var tpm_name = VFS.CombinePath (dir_name, TpmFileName);
            using (var tpm = VFS.OpenView (tpm_name))
            {
                if (tpm.MaxOffset < 0x1000 || tpm.MaxOffset > uint.MaxValue)
                    throw new InvalidEncryptionScheme ("Invalid KiriKiri TPM plugin");
                using (var view = tpm.CreateViewAccessor(0, (uint)tpm.MaxOffset))
                {
                    if (tpm.MaxOffset == 0x1000 && Path.GetExtension(tpm_name) == ".bin") //ダンプから切り取ったControlBlock
                    {
                        ControlBlock = new uint[0x400];
                        for (int i = 0; i < 0x400; i++)
                        {
                            ControlBlock[i] = ~view.ReadUInt32(i * 4);
                        }

                        //ControlBlock確認用
                        foreach (var block in ControlBlock)
                            Console.WriteLine("0x{0:X8}", block);

                        return;
                    }
                    else
                        unsafe
                        {
                            byte* begin = view.GetPointer(0);
                            byte* end = begin + (((uint)tpm.MaxOffset - 0x1000u) & ~0x3u);
                            try
                            {
                                while (begin < end)
                                {
                                    int i;
                                    for (i = 0; i < s_ctl_block_signature.Length; ++i)
                                    {
                                        if (begin[i] != s_ctl_block_signature[i])
                                            break;
                                    }
                                    if (s_ctl_block_signature.Length == i)
                                    {
                                        ControlBlock = new uint[0x400];
                                        uint* src = (uint*)begin;
                                        for (i = 0; i < ControlBlock.Length; ++i)
                                            ControlBlock[i] = ~src[i];

                                        //ControlBlock確認用
                                        foreach (var block in ControlBlock)
                                            Console.WriteLine("0x{0:X8}", block);

                                        return;
                                    }
                                    begin += 4; // control block expected to be on a dword boundary
                                }
                                throw new InvalidEncryptionScheme("No control block found inside TPM plugin");
                            }
                            finally
                            {
                                view.SafeMemoryMappedViewHandle.ReleasePointer();
                            }
                        }
                }
            }
        }

        uint GetBaseOffset (uint hash)
        {
            return (hash & m_mask) + m_offset;
        }

        public override byte Decrypt (Xp3Entry entry, long offset, byte value)
        {
            Console.WriteLine(entry.Name);
            uint key = entry.Hash;
            uint base_offset = GetBaseOffset (key);
            if (offset >= base_offset)
            {
                key = (key >> 16) ^ key;
            }
            var buffer = new byte[1] { value };
            Decode (key, offset, buffer, 0, 1);
            return buffer[0];
        }

        public override void Decrypt (Xp3Entry entry, long offset, byte[] buffer, int pos, int count)
        {
            Console.WriteLine(entry.Name);
            uint key = entry.Hash;
            uint base_offset = GetBaseOffset (key);
            if (offset < base_offset)
            {
                int base_length = Math.Min ((int)(base_offset - offset), count);
                Decode (key, offset, buffer, pos, base_length);
                offset += base_length;
                pos += base_length;
                count -= base_length;
            }
            if (count > 0)
            {
                key = (key >> 16) ^ key;
                Decode (key, offset, buffer, pos, count);
            }
        }

        void Decode (uint key, long offset, byte[] buffer, int pos, int count)
        {
            Tuple<uint, uint> ret = ExecuteXCode (key);
            uint key1 = ret.Item2 >> 16;
            uint key2 = ret.Item2 & 0xffff;
            byte key3 = (byte)(ret.Item1);
            if (key1 == key2)
                key2 += 1;
            if (0 == key3)
                key3 = 1;

            if ((key2 >= offset) && (key2 < offset + count))
                buffer[pos + key2 - offset] ^= (byte)(ret.Item1 >> 16);
            
            if ((key1 >= offset) && (key1 < offset + count))
                buffer[pos + key1 - offset] ^= (byte)(ret.Item1 >> 8);

            for (int i = 0; i < count; ++i)
                buffer[pos + i] ^= key3;
        }

        public override void Encrypt (Xp3Entry entry, long offset, byte[] values, int pos, int count)
        {
            Decrypt (entry, offset, values, pos, count);
        }

        protected Tuple<uint, uint> ExecuteXCode (uint hash)
        {
            uint seed = hash & 0x7f;
            if (null == m_program_list[seed])
            {
                m_program_list[seed] = GenerateProgram (seed);
            }
            hash >>= 7;
            uint ret1 = m_program_list[seed].Execute (hash);
            uint ret2 = m_program_list[seed].Execute (~hash);
            return new Tuple<uint, uint> (ret1, ret2);
        }

        CxProgram GenerateProgram(uint seed)
        {
            Console.WriteLine("seed:{0}", seed);
            var program = NewProgram(seed);
            for (int stage = 5; stage > 0; --stage)
            {
                m_order_text = "";
                var rc = EmitCode(program, stage);
                Console.WriteLine("{0}: {1}", stage, m_order_text);
                if (rc)
                    return program;
                program.Clear();
            }
            throw new CxProgramException("Overly large CxEncryption bytecode");
        }

        private static string m_order_text = "";

        internal virtual CxProgram NewProgram (uint seed)
        {
            return new CxProgram (seed, ControlBlock);
        }

        bool EmitCode (CxProgram program, int stage)
        {
            return program.EmitNop (5)                      // 57 56 53 51 52
                && program.Emit (CxByteCode.MOV_EDI_ARG, 4) // 8B 7C 24 18
                && EmitBody (program, stage)
                && program.EmitNop (5)                      // 5A 59 5B 5E 5F
                && program.Emit (CxByteCode.RETN);          // C3
        }

        bool EmitBody (CxProgram program, int stage)
        {
            if (1 == stage)
                return EmitProlog (program);

            if (!program.Emit (CxByteCode.PUSH_EBX))    // 53
                return false;

            if (0 != (program.GetRandom() & 1))
            {
                if (!EmitBody (program, stage - 1))
                    return false;
            }
            else if (!EmitBody2 (program, stage - 1))
                return false;

            if (!program.Emit (CxByteCode.MOV_EBX_EAX, 2))  // 89 C3
                return false;

            if (0 != (program.GetRandom() & 1))
            {
                if (!EmitBody (program, stage - 1))
                    return false;
            }
            else if (!EmitBody2 (program, stage - 1))
                return false;

            return EmitOddBranch (program) && program.Emit (CxByteCode.POP_EBX); // 5B
        }

        bool EmitBody2 (CxProgram program, int stage)
        {
            if (1 == stage)
                return EmitProlog (program);

            bool rc = true;
            if (0 != (program.GetRandom() & 1))
                rc = EmitBody (program, stage - 1);
            else
                rc = EmitBody2 (program, stage - 1);

            return rc && EmitEvenBranch (program);
        }

        bool EmitProlog (CxProgram program)
        {

            bool rc = true;
            var prolog_idx = program.GetRandom() % 3;
            m_order_text += String.Format("P{0}", PrologOrder[prolog_idx]);
            switch (PrologOrder[prolog_idx])
            {
            case 2:
                // MOV EAX, (Random() & 0x3ff)
                // MOV EAX, EncryptionControlBlock[EAX]
                rc =   program.EmitNop (5)                              // BE + 4バイト
                    && program.Emit (CxByteCode.MOV_EAX_IMMED, 2)       // 8B 86
                    && program.EmitUInt32 (program.GetRandom() & 0x3ff) // ?? 0? 00 00
                    && program.Emit (CxByteCode.MOV_EAX_INDIRECT, 0);
                break;
            case 1:
                rc = program.Emit (CxByteCode.MOV_EAX_EDI, 2);      // 8B C7
                break;
            case 0:
                // MOV EAX, Random()
                rc =   program.Emit (CxByteCode.MOV_EAX_IMMED)      // B8
                    && program.EmitRandom();                        // 4バイト
                    break;
            }
            return rc;
        }

        bool EmitEvenBranch (CxProgram program)
        {
            bool rc = true;
            var even_idx = program.GetRandom() & 7;
            m_order_text += String.Format("E{0}", EvenBranchOrder[even_idx]);
            switch (EvenBranchOrder[even_idx])
            {
            case 0:
                rc = program.Emit (CxByteCode.NOT_EAX, 2);  // F7 D0
                break;
            case 1:
                rc = program.Emit (CxByteCode.DEC_EAX);     // 48
                break;
            case 2:
                rc = program.Emit (CxByteCode.NEG_EAX, 2);  // F7 D8
                break;
            case 3:
                rc = program.Emit (CxByteCode.INC_EAX);     // 40
                break;
            case 4:
                rc =   program.EmitNop (5)                            // BE + 4バイト
                    && program.Emit (CxByteCode.AND_EAX_IMMED)        // 25
                    && program.EmitUInt32 (0x3ff)                     // FF 03 00 00
                    && program.Emit (CxByteCode.MOV_EAX_INDIRECT, 3); // 8B 04 86
                break;
            case 5:
                rc =   program.Emit (CxByteCode.PUSH_EBX)           // 53
                    && program.Emit (CxByteCode.MOV_EBX_EAX, 2)     // 89 C3
                    && program.Emit (CxByteCode.AND_EBX_IMMED, 2)   // 81 E3
                    && program.EmitUInt32 (0xaaaaaaaa)              // AA AA AA AA
                    && program.Emit (CxByteCode.AND_EAX_IMMED)      // 25
                    && program.EmitUInt32 (0x55555555)              // 55 55 55 55
                    && program.Emit (CxByteCode.SHR_EBX_1, 2)       // D1 EB
                    && program.Emit (CxByteCode.SHL_EAX_1, 2)       // D1 E0
                    && program.Emit (CxByteCode.OR_EAX_EBX, 2)      // 09 D8
                    && program.Emit (CxByteCode.POP_EBX);           // 5B
                break;
            case 6:
                rc =   program.Emit (CxByteCode.XOR_EAX_IMMED)      // 35 + 4バイト
                    && program.EmitRandom();
                break;
            case 7:
                if (0 != (program.GetRandom() & 1))
                    rc = program.Emit (CxByteCode.ADD_EAX_IMMED);   // 05 + 4バイト
                else
                    rc = program.Emit (CxByteCode.SUB_EAX_IMMED);   // 2D + 4バイト
                    rc = rc && program.EmitRandom();
                break;
            }
            return rc;
        }

        bool EmitOddBranch (CxProgram program)
        {
            bool rc = true;
            var odd_idx = program.GetRandom() % 6;
            m_order_text += String.Format("O{0}", OddBranchOrder[odd_idx]);
            switch (OddBranchOrder[odd_idx])
            {
            case 0:
                rc =   program.Emit (CxByteCode.PUSH_ECX)       // 51
                    && program.Emit (CxByteCode.MOV_ECX_EBX, 2) // 89 D9
                    && program.Emit (CxByteCode.AND_ECX_0F, 3)  // 83 E1 0F
                    && program.Emit (CxByteCode.SHR_EAX_CL, 2)  // D3 E8
                    && program.Emit (CxByteCode.POP_ECX);       // 59
                break;
            case 1:
                rc =   program.Emit (CxByteCode.PUSH_ECX)       // 51
                    && program.Emit (CxByteCode.MOV_ECX_EBX, 2) // 89 D9
                    && program.Emit (CxByteCode.AND_ECX_0F, 3)  // 83 E1 0F
                    && program.Emit (CxByteCode.SHL_EAX_CL, 2)  // D3 E0
                    && program.Emit (CxByteCode.POP_ECX);       // 59
                break;
            case 2:
                rc = program.Emit (CxByteCode.ADD_EAX_EBX, 2);  // 01 D8
                break;
            case 3:
                rc =   program.Emit (CxByteCode.NEG_EAX, 2)      // F7 D8
                    && program.Emit (CxByteCode.ADD_EAX_EBX, 2); // 01 D8
                break;
            case 4:
                rc = program.Emit (CxByteCode.IMUL_EAX_EBX, 3); // 0F AF C3
                break;
            case 5:
                rc = program.Emit (CxByteCode.SUB_EAX_EBX, 2);  // 29 D8
                break;
            }
            return rc;
        }
    }

    enum CxByteCode
    {
        NOP,
        RETN,
        MOV_EDI_ARG,
        PUSH_EBX,
        POP_EBX,
        PUSH_ECX,
        POP_ECX,
        MOV_EAX_EBX,
        MOV_EBX_EAX,
        MOV_ECX_EBX,
        MOV_EAX_CONTROL_BLOCK,
        MOV_EAX_EDI,
        MOV_EAX_INDIRECT,
        ADD_EAX_EBX,
        SUB_EAX_EBX,
        IMUL_EAX_EBX,
        AND_ECX_0F,
        SHR_EBX_1,
        SHL_EAX_1,
        SHR_EAX_CL,
        SHL_EAX_CL,
        OR_EAX_EBX,
        NOT_EAX,
        NEG_EAX,
        DEC_EAX,
        INC_EAX,

        IMMED = 0x100,
        MOV_EAX_IMMED,
        AND_EBX_IMMED,
        AND_EAX_IMMED,
        XOR_EAX_IMMED,
        ADD_EAX_IMMED,
        SUB_EAX_IMMED,
    }

    internal class CxProgram
    {
        public const int    LengthLimit = 0x80;
        internal List<uint>  m_code = new List<uint> (LengthLimit);
        private uint[]      m_ControlBlock;
        private int         m_length;
        protected uint      m_seed;

        class Context
        {
            public uint eax;
            public uint ebx;
            public uint ecx;
            public uint edi;

            public Stack<uint> stack = new Stack<uint>();
        }

        public CxProgram (uint seed, uint[] control_block)
        {
            m_seed = seed;
            m_length = 0;
            m_ControlBlock = control_block;
        }

        public uint Execute (uint hash)
        {
            var context = new Context();
            using (var iterator = m_code.GetEnumerator())
            {
                uint immed = 0;
                while (iterator.MoveNext())
                {
                    var bytecode = (CxByteCode)iterator.Current;
                    if (CxByteCode.IMMED == (bytecode & CxByteCode.IMMED))
                    {
                        if (!iterator.MoveNext())
                            throw new CxProgramException ("Incomplete IMMED bytecode in CxEncryption program");
                        immed = iterator.Current;
                    }
                    switch (bytecode)
                    {
                    case CxByteCode.NOP: break;
                    case CxByteCode.IMMED: break;
                    case CxByteCode.MOV_EDI_ARG:    context.edi = hash; break;
                    case CxByteCode.PUSH_EBX:       context.stack.Push (context.ebx); break;
                    case CxByteCode.POP_EBX:        context.ebx = context.stack.Pop(); break;
                    case CxByteCode.PUSH_ECX:       context.stack.Push (context.ecx); break;
                    case CxByteCode.POP_ECX:        context.ecx = context.stack.Pop(); break;
                    case CxByteCode.MOV_EBX_EAX:    context.ebx = context.eax; break;
                    case CxByteCode.MOV_EAX_EDI:    context.eax = context.edi; break;
                    case CxByteCode.MOV_ECX_EBX:    context.ecx = context.ebx; break;
                    case CxByteCode.MOV_EAX_EBX:    context.eax = context.ebx; break;

                    case CxByteCode.AND_ECX_0F:     context.ecx &= 0x0f; break;
                    case CxByteCode.SHR_EBX_1:      context.ebx >>= 1; break;
                    case CxByteCode.SHL_EAX_1:      context.eax <<= 1; break;
                    case CxByteCode.SHR_EAX_CL:     context.eax >>= (int)context.ecx; break;
                    case CxByteCode.SHL_EAX_CL:     context.eax <<= (int)context.ecx; break;
                    case CxByteCode.OR_EAX_EBX:     context.eax |= context.ebx; break;
                    case CxByteCode.NOT_EAX:        context.eax = ~context.eax; break;
                    case CxByteCode.NEG_EAX:        context.eax = (uint)-context.eax; break;
                    case CxByteCode.DEC_EAX:        context.eax--; break;
                    case CxByteCode.INC_EAX:        context.eax++; break;

                    case CxByteCode.ADD_EAX_EBX:    context.eax += context.ebx; break;
                    case CxByteCode.SUB_EAX_EBX:    context.eax -= context.ebx; break;
                    case CxByteCode.IMUL_EAX_EBX:   context.eax *= context.ebx; break;

                    case CxByteCode.ADD_EAX_IMMED:  context.eax += immed; break;
                    case CxByteCode.SUB_EAX_IMMED:  context.eax -= immed; break;
                    case CxByteCode.AND_EBX_IMMED:  context.ebx &= immed; break;
                    case CxByteCode.AND_EAX_IMMED:  context.eax &= immed; break;
                    case CxByteCode.XOR_EAX_IMMED:  context.eax ^= immed; break;
                    case CxByteCode.MOV_EAX_IMMED:  context.eax = immed; break;
                    case CxByteCode.MOV_EAX_INDIRECT:
                        if (context.eax >= m_ControlBlock.Length)
                            throw new CxProgramException ("Index out of bounds in CxEncryption program");
                        context.eax = ~m_ControlBlock[context.eax];
                        break;

                    case CxByteCode.RETN:
                        if (context.stack.Count > 0)
                            throw new CxProgramException ("Imbalanced stack in CxEncryption program");
                        return context.eax;

                    default:
                        throw new CxProgramException ("Invalid bytecode in CxEncryption program");
                    }
                }
            }
            throw new CxProgramException ("CxEncryption program without RETN bytecode");
        }

        public void Clear ()
        {
            m_length = 0;
            m_code.Clear();
        }

        public bool EmitNop (int count)
        {
            if (m_length + count > LengthLimit)
                return false;
            m_length += count;
            return true;
        }

        public bool Emit (CxByteCode code, int length = 1)
        {
            if (m_length + length > LengthLimit)
                return false;
            m_length += length;
            m_code.Add ((uint)code);
            return true;
        }

        public bool EmitUInt32 (uint x)
        {
            if (m_length + 4 > LengthLimit)
                return false;
            m_length += 4;
            m_code.Add (x);
            return true;
        }

        public bool EmitRandom ()
        {
            return EmitUInt32 (GetRandom());
        }

        public virtual uint GetRandom ()
        {
            uint seed = m_seed;
            m_seed = 1103515245 * seed + 12345;
            return m_seed ^ (seed << 16) ^ (seed >> 16);
        }
    }

    internal class CxProgramNana : CxProgram
    {
        protected uint  m_random_seed;

        public CxProgramNana (uint seed, uint random_seed, uint[] control_block) : base (seed, control_block)
        {
            m_random_seed = random_seed;
        }

        public override uint GetRandom ()
        {
            uint s = m_seed ^ (m_seed << 17);
            s ^= (s << 18) | (s >> 15);
            m_seed = ~s;
            uint r = m_random_seed ^ (m_random_seed << 13);
            r ^= r >> 17;
            m_random_seed = r ^ (r << 5);
            return m_seed ^ m_random_seed;
        }
    }

    /* CxEncryption base branch order
    OddBranchOrder
    {
        case 0: SHR_EAX_CL
        case 1: SHL_EAX_CL
        case 2: ADD_EAX_EBX
        case 3: NEG_EAX; ADD_EAX_EBX
        case 4: IMUL_EAX_EBX
        case 5: SUB_EAX_EBX
    }

    EvenBranchOrder
    {
        case 0: NOT_EAX
        case 1: DEC_EAX
        case 2: NEG_EAX
        case 3: INC_EAX
        case 4: MOV_EAX_INDIRECT
        case 5: OR_EAX_EBX
        case 6: XOR_EAX_IMMED
        case 7: ADD_EAX_IMMED
    }
    */
}
