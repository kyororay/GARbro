//! \file       ArcARC.cs
//! \date       2026 Feb 02
//! \brief      Edoire's resource archive.
//
// Copyright (C) 2018 by morkt
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
using System.Linq;
using System.Text;
using GameRes.Compression;
using GameRes.Formats.Unity;

namespace GameRes.Formats.Edoire
{
    public enum ArchiveFlags
    {
        CompressionTypeMask = 0x3f,
        BlocksAndDirectoryInfoCombined = 0x40,
        BlocksInfoAtTheEnd = 0x80,
        OldWebPluginCompatibility = 0x100,
        BlockInfoNeedPaddingAtStart = 0x200,
        UnityCNEncryption = 0x400
    }

    public enum StorageBlockFlags
    {
        CompressionTypeMask = 0x3f,
        Streamed = 0x40,
    }

    public enum CompressionType
    {
        None,
        Lzma,
        Lz4,
        Lz4HC,
        Lzham,
        Lz4Mr0k,
        Lz4Inv = 5,
        Zstd = 5,
        Lz4Lit4 = 4,
        Lz4Lit5 = 5,
    }

    public enum SerializedFileFormatVersion
    {
        Unsupported = 1,
        Unknown_2 = 2,
        Unknown_3 = 3,
        Unknown_5 = 5, // 1.2.0 to 2.0.0
        Unknown_6 = 6, // 2.1.0 to 2.6.1
        Unknown_7 = 7, // 3.0.0b
        Unknown_8 = 8, // 3.0.0 to 3.4.2
        Unknown_9 = 9, // 3.5.0 to 4.7.2
        Unknown_10 = 10, // 5.0.0aunk1
        HasScriptTypeIndex = 11, // 5.0.0aunk2
        Unknown_12 = 12, // 5.0.0aunk3
        HasTypeTreeHashes = 13, // 5.0.0aunk4
        Unknown_14 = 14, // 5.0.0unk
        SupportsStrippedObject = 15, // 5.0.1 to 5.4.0
        RefactoredClassId = 16, // 5.5.0a
        RefactorTypeData = 17, // 5.5.0unk to 2018.4
        RefactorShareableTypeTreeData = 18, // 2019.1a
        TypeTreeNodeWithTypeFlags = 19, // 2019.1unk
        SupportsRefObject = 20, // 2019.2
        StoresTypeDependencies = 21, // 2019.3 to 2019.4
        LargeFilesSupport = 22 // 2020.1 to x
    }

    public enum BuildTarget
    {
        NoTarget = -2,
        AnyPlayer = -1,
        ValidPlayer = 1,
        StandaloneOSX = 2,
        StandaloneOSXPPC = 3,
        StandaloneOSXIntel = 4,
        StandaloneWindows,
        WebPlayer,
        WebPlayerStreamed,
        Wii = 8,
        iOS = 9,
        PS3,
        XBOX360,
        Broadcom = 12,
        Android = 13,
        StandaloneGLESEmu = 14,
        StandaloneGLES20Emu = 15,
        NaCl = 16,
        StandaloneLinux = 17,
        FlashPlayer = 18,
        StandaloneWindows64 = 19,
        WebGL,
        WSAPlayer,
        StandaloneLinux64 = 24,
        StandaloneLinuxUniversal,
        WP8Player,
        StandaloneOSXIntel64,
        BlackBerry,
        Tizen,
        PSP2,
        PS4,
        PSM,
        XboxOne,
        SamsungTV,
        N3DS,
        WiiU,
        tvOS,
        Switch,
        Lumin,
        Stadia,
        CloudRendering,
        GameCoreXboxSeries,
        GameCoreXboxOne,
        PS5,
        EmbeddedLinux,
        QNX,
        UnknownPlatform = 9999
    }

    public enum GameType
    {
        Normal,
        UnityCN,
        GI,
        GI_Pack,
        GI_CB1,
        GI_CB2,
        GI_CB3,
        GI_CB3Pre,
        BH3,
        BH3Pre,
        BH3PrePre,
        ZZZ_CB1,
        SR_CB2,
        SR,
        TOT,
        Naraka,
        EnsembleStars,
        OPFP,
        FakeHeader,
        FantasyOfWind,
        ShiningNikki,
        HelixWaltz2,
        NetEase,
        AnchorPanic,
        DreamscapeAlbireo,
        ImaginaryFest,
        AliceGearAegis,
        ProjectSekai,
        CodenameJump,
        GirlsFrontline,
        Reverse1999,
        ArknightsEndfield,
        JJKPhantomParade,
        MuvLuvDimensions,
        PartyAnimals,
        LoveAndDeepspace,
        SchoolGirlStrikers,
        ExAstris,
        PerpetualNovelty,
    }

    public class Header
    {
        public string signature;
        public uint version;
        public string unityVersion;
        public string unityRevision;
        public long size;
        public uint compressedBlocksInfoSize;
        public uint uncompressedBlocksInfoSize;
        public ArchiveFlags flags;
    }

    public class StorageBlock
    {
        public uint compressedSize;
        public uint uncompressedSize;
        public StorageBlockFlags flags;
    }

    public class SerializedFileHeader
    {
        public uint m_MetadataSize;
        public long m_FileSize;
        public SerializedFileFormatVersion m_Version;
        public long m_DataOffset;
        public byte m_Endianess;
        public byte[] m_Reserved;
    }

    public class SerializedType
    {
        public int classID;
        public bool m_IsStrippedType;
        public short m_ScriptTypeIndex = -1;
        public TypeTree m_Type;
        public byte[] m_ScriptID; //Hash128
        public byte[] m_OldTypeHash; //Hash128
        public int[] m_TypeDependencies;
        public string m_ClassName;
        public string m_NameSpace;
        public string m_AsmName;
    }

    public class TypeTree
    {
        public List<TypeTreeNode> m_Nodes;
        public byte[] m_StringBuffer;
    }

    public class TypeTreeNode
    {
        public string m_Type;
        public string m_Name;
        public int m_ByteSize;
        public int m_Index;
        public int m_TypeFlags; //m_IsArray
        public int m_Version;
        public int m_MetaFlag;
        public int m_Level;
        public uint m_TypeStrOffset;
        public uint m_NameStrOffset;
        public ulong m_RefTypeHash;

        public TypeTreeNode() { }

        public TypeTreeNode(string type, string name, int level, bool align)
        {
            m_Type = type;
            m_Name = name;
            m_Level = level;
            m_MetaFlag = align ? 0x4000 : 0;
        }
    }

    public class ObjectInfo
    {
        public long byteStart;
        public uint byteSize;
        public int typeID;
        public int classID;
        public ushort isDestroyed;
        public byte stripped;
        public long m_PathID;
        public SerializedType serializedType;
    }

    public class Game
    {
        public string Name { get; set; }
        public GameType Type { get; }

        public Game(GameType type)
        {
            Name = type.ToString();
            Type = type;
        }

        public bool IsMhyGroup()
        {
            switch (Type)
            {
                case GameType.GI:
                case GameType.GI_Pack:
                case GameType.GI_CB1:
                case GameType.GI_CB2:
                case GameType.GI_CB3:
                case GameType.GI_CB3Pre:
                case GameType.BH3:
                case GameType.BH3Pre:
                case GameType.BH3PrePre:
                case GameType.SR_CB2:
                case GameType.SR:
                case GameType.ZZZ_CB1:
                case GameType.TOT:
                    return true;
                default:
                    return false;
            }
        }
    }

    public static class CommonString
    {
        public static readonly Dictionary<uint, string> StringBuffer = new Dictionary<uint, string>
        {
            {0, "AABB"},
            {5, "AnimationClip"},
            {19, "AnimationCurve"},
            {34, "AnimationState"},
            {49, "Array"},
            {55, "Base"},
            {60, "BitField"},
            {69, "bitset"},
            {76, "bool"},
            {81, "char"},
            {86, "ColorRGBA"},
            {96, "Component"},
            {106, "data"},
            {111, "deque"},
            {117, "double"},
            {124, "dynamic_array"},
            {138, "FastPropertyName"},
            {155, "first"},
            {161, "float"},
            {167, "Font"},
            {172, "GameObject"},
            {183, "Generic Mono"},
            {196, "GradientNEW"},
            {208, "GUID"},
            {213, "GUIStyle"},
            {222, "int"},
            {226, "list"},
            {231, "long long"},
            {241, "map"},
            {245, "Matrix4x4f"},
            {256, "MdFour"},
            {263, "MonoBehaviour"},
            {277, "MonoScript"},
            {288, "m_ByteSize"},
            {299, "m_Curve"},
            {307, "m_EditorClassIdentifier"},
            {331, "m_EditorHideFlags"},
            {349, "m_Enabled"},
            {359, "m_ExtensionPtr"},
            {374, "m_GameObject"},
            {387, "m_Index"},
            {395, "m_IsArray"},
            {405, "m_IsStatic"},
            {416, "m_MetaFlag"},
            {427, "m_Name"},
            {434, "m_ObjectHideFlags"},
            {452, "m_PrefabInternal"},
            {469, "m_PrefabParentObject"},
            {490, "m_Script"},
            {499, "m_StaticEditorFlags"},
            {519, "m_Type"},
            {526, "m_Version"},
            {536, "Object"},
            {543, "pair"},
            {548, "PPtr<Component>"},
            {564, "PPtr<GameObject>"},
            {581, "PPtr<Material>"},
            {596, "PPtr<MonoBehaviour>"},
            {616, "PPtr<MonoScript>"},
            {633, "PPtr<Object>"},
            {646, "PPtr<Prefab>"},
            {659, "PPtr<Sprite>"},
            {672, "PPtr<TextAsset>"},
            {688, "PPtr<Texture>"},
            {702, "PPtr<Texture2D>"},
            {718, "PPtr<Transform>"},
            {734, "Prefab"},
            {741, "Quaternionf"},
            {753, "Rectf"},
            {759, "RectInt"},
            {767, "RectOffset"},
            {778, "second"},
            {785, "set"},
            {789, "short"},
            {795, "size"},
            {800, "SInt16"},
            {807, "SInt32"},
            {814, "SInt64"},
            {821, "SInt8"},
            {827, "staticvector"},
            {840, "string"},
            {847, "TextAsset"},
            {857, "TextMesh"},
            {866, "Texture"},
            {874, "Texture2D"},
            {884, "Transform"},
            {894, "TypelessData"},
            {907, "UInt16"},
            {914, "UInt32"},
            {921, "UInt64"},
            {928, "UInt8"},
            {934, "unsigned int"},
            {947, "unsigned long long"},
            {966, "unsigned short"},
            {981, "vector"},
            {988, "Vector2f"},
            {997, "Vector3f"},
            {1006, "Vector4f"},
            {1015, "m_ScriptingClassIdentifier"},
            {1042, "Gradient"},
            {1051, "Type*"},
            {1057, "int2_storage"},
            {1070, "int3_storage"},
            {1083, "BoundsInt"},
            {1093, "m_CorrespondingSourceObject"},
            {1121, "m_PrefabInstance"},
            {1138, "m_PrefabAsset"},
            {1152, "FileSize"},
            {1161, "Hash128"}
        };
    }

    [Export(typeof(ArchiveFormat))]
    public class ArcOpener : ArchiveFormat
    {
        public override string Tag { get { return "ARC"; } }
        public override string Description { get { return "Edoire's resource archive"; } }
        public override uint Signature { get { return 0x43524140; } } // "@ARCH000"
        public override bool IsHierarchic { get { return true; } }
        public override bool CanWrite { get { return false; } }

        public ArcOpener()
        {
            Extensions = new string[] { "arc" };
        }

        public override ArcFile TryOpen(ArcView file)
        {
            if (!file.View.AsciiEqual(0, "@ARCH000"))
                return null;
            var index_offset = file.View.ReadInt64(file.MaxOffset - 8);
            if (index_offset <= 0 || index_offset >= file.MaxOffset - 12)
                return null;
            var count = file.View.ReadInt32(index_offset);
            if (!IsSaneCount(count))
                return null;
            index_offset += 4;
            var dir = new List<Entry>(count);
            for (var i = 0; i < count; i++)
            {
                var len = file.View.ReadByte(index_offset);
                index_offset += 1;
                var name = file.View.ReadString(index_offset, len, Encoding.UTF8);
                name = name.Replace(".bytes", ".png").Replace(".ogg", ".fsb");
                index_offset += len;
                var offset = file.View.ReadInt64(index_offset);
                index_offset += 8;
                var size = file.View.ReadInt64(index_offset);
                index_offset += 9;
                len = file.View.ReadByte(index_offset);
                index_offset += 1;
                var path = file.View.ReadString(index_offset, len, Encoding.UTF8);
                index_offset += len;
                if (path.StartsWith("/"))
                    path = path.Substring(1);
                if (!string.IsNullOrEmpty(path) && !path.EndsWith("/"))
                    path += "/";
                var entry = Create<Entry>(path + name);
                entry.Offset = offset;
                entry.Size = Convert.ToUInt32(size);
                if (!entry.CheckPlacement(file.MaxOffset))
                    return null;
                dir.Add(entry);
            }
            return new ArcFile(file, this, dir);
        }

        public override Stream OpenEntry(ArcFile arc, Entry entry)
        {
            using (var stream = arc.File.CreateStream(entry.Offset, entry.Size, entry.Name))
            using (var input = new AssetReader(stream))
            {
                var header = new Header()
                {
                    signature = input.ReadCString(),
                    version = input.ReadUInt32(),
                    unityVersion = input.ReadCString(),
                    unityRevision = input.ReadCString(),
                    size = input.ReadInt64(),
                    compressedBlocksInfoSize = input.ReadUInt32(),
                    uncompressedBlocksInfoSize = input.ReadUInt32(),
                    flags = (ArchiveFlags)input.ReadUInt32()
                };

                switch (header.signature)
                {
                    case "UnityFS":
                        {
                            var info = ReadBlocksInfo(input, header);
                            var reader = new AssetReader(ReadBlocks(input, info), null);
                            return SerializedFile(reader);
                        }
                    default:
                        throw new IOException($"Unsupported bundle type {header.signature}");
                }
            }
        }

        private List<StorageBlock> ReadBlocksInfo(AssetReader reader, Header header)
        {
            if (header.version >= 7)
            {
                reader.AlignStream(16);
            }
            var info_size = (int)header.compressedBlocksInfoSize;
            var info_bytes = reader.ReadBytes(info_size);
            var uncompressed_size = (int)header.uncompressedBlocksInfoSize;
            var uncompressed_bytes = new byte[(int)header.uncompressedBlocksInfoSize];

            Lz4Compressor.DecompressBlock(info_bytes, info_size, uncompressed_bytes, uncompressed_size);

            var blocks_info = new List<StorageBlock>();

            using (var uncompressed_stream = new MemoryStream(uncompressed_bytes))
            using (var info_reader = new AssetReader(uncompressed_stream, null))
            {
                var uncompressed_hash = info_reader.ReadBytes(16);
                var info_count = info_reader.ReadInt32();

                for (int i = 0; i < info_count; i++)
                {
                    blocks_info.Add(new StorageBlock
                    {
                        uncompressedSize = info_reader.ReadUInt32(),
                        compressedSize = info_reader.ReadUInt32(),
                        flags = (StorageBlockFlags)info_reader.ReadUInt16()
                    });
                }
            }
            if ((header.flags & ArchiveFlags.BlockInfoNeedPaddingAtStart) != 0)
            {
                reader.AlignStream(16);
            }

            return blocks_info;
        }

        private Stream ReadBlocks(AssetReader reader, List<StorageBlock> blocks_info)
        {
            Stream blocks_stream = new MemoryStream((int)blocks_info.Sum(x => x.uncompressedSize));

            for (int i = 0; i < blocks_info.Count; i++)
            {
                var block_info = blocks_info[i];
                var compression_type = (CompressionType)(block_info.flags & StorageBlockFlags.CompressionTypeMask);

                switch (compression_type)
                {
                    case CompressionType.None:
                        {
                            reader.CopyToStream(blocks_stream, (int)block_info.compressedSize);
                            break;
                        }
                    case CompressionType.Lz4HC:
                        {
                            var compressed_size = (int)block_info.compressedSize;
                            var compressed_bytes = reader.ReadBytes(compressed_size);
                            var uncompressed_size = (int)block_info.uncompressedSize;
                            var uncompressed_bytes = new byte[uncompressed_size];

                            Lz4Compressor.DecompressBlock(compressed_bytes, compressed_size, uncompressed_bytes, uncompressed_size);
                            blocks_stream.Write(uncompressed_bytes, 0, uncompressed_size);
                            break;
                        }
                    default:
                        throw new IOException($"Unsupported compression type {compression_type}");
                }
            }
            blocks_stream.Position = 0;

            return blocks_stream;
        }

        private string unityVersion = "2.5.0f5";
        private BuildTarget m_TargetPlatform = BuildTarget.UnknownPlatform;
        private bool m_EnableTypeTree = true;
        private List<SerializedType> m_Types;
        private int bigIDEnabled = 0;
        private List<ObjectInfo> m_Objects;

        private Stream SerializedFile(AssetReader reader)
        {
            // ReadHeader
            var header = new SerializedFileHeader();
            header = new SerializedFileHeader();
            header.m_MetadataSize = reader.ReadUInt32();
            header.m_FileSize = reader.ReadUInt32();
            header.m_Version = (SerializedFileFormatVersion)reader.ReadUInt32();
            header.m_DataOffset = reader.ReadUInt32();

            if (header.m_Version >= SerializedFileFormatVersion.Unknown_9)
            {
                header.m_Endianess = reader.ReadByte();
                header.m_Reserved = reader.ReadBytes(3);
            }
            else
            {
                reader.Position = header.m_FileSize - header.m_MetadataSize;
                header.m_Endianess = reader.ReadByte();
            }

            if (header.m_Version >= SerializedFileFormatVersion.LargeFilesSupport)
            {
                header.m_MetadataSize = reader.ReadUInt32();
                header.m_FileSize = reader.ReadInt64();
                header.m_DataOffset = reader.ReadInt64();
                reader.ReadInt64(); // unknown
            }

            // ReadMetadata
            if (header.m_Endianess == 0)
            {
                reader.SetupReaders((int)header.m_Version, true);
            }
            if (header.m_Version >= SerializedFileFormatVersion.Unknown_7)
            {
                unityVersion = reader.ReadCString();
            }
            if (header.m_Version >= SerializedFileFormatVersion.Unknown_8)
            {
                m_TargetPlatform = (BuildTarget)reader.ReadInt32();
            }
            if (header.m_Version >= SerializedFileFormatVersion.HasTypeTreeHashes)
            {
                m_EnableTypeTree = reader.ReadByte() != 0;
            }

            // Read Types
            int typeCount = reader.ReadInt32();
            m_Types = new List<SerializedType>();
            for (int i = 0; i < typeCount; i++)
            {
                m_Types.Add(ReadSerializedType(reader, header, false));
            }

            if (header.m_Version >= SerializedFileFormatVersion.Unknown_7 && header.m_Version < SerializedFileFormatVersion.Unknown_14)
            {
                bigIDEnabled = reader.ReadInt32();
            }

            // Read Objects
            int objectCount = reader.ReadInt32();
            m_Objects = new List<ObjectInfo>();
            for (int i = 0; i < objectCount; i++)
            {
                var objectInfo = new ObjectInfo();
                if (bigIDEnabled != 0)
                {
                    objectInfo.m_PathID = reader.ReadInt64();
                }
                else if (header.m_Version < SerializedFileFormatVersion.Unknown_14)
                {
                    objectInfo.m_PathID = reader.ReadInt32();
                }
                else
                {
                    reader.AlignStream(4);
                    objectInfo.m_PathID = reader.ReadInt64();
                }

                if (header.m_Version >= SerializedFileFormatVersion.LargeFilesSupport)
                    objectInfo.byteStart = reader.ReadInt64();
                else
                    objectInfo.byteStart = reader.ReadUInt32();

                objectInfo.byteStart += header.m_DataOffset;
                objectInfo.byteSize = reader.ReadUInt32();
                objectInfo.typeID = reader.ReadInt32();
                if (header.m_Version < SerializedFileFormatVersion.RefactoredClassId)
                {
                    objectInfo.classID = reader.ReadUInt16();
                    objectInfo.serializedType = m_Types.Find(x => x.classID == objectInfo.typeID);
                }
                else
                {
                    var type = m_Types[objectInfo.typeID];
                    objectInfo.serializedType = type;
                    objectInfo.classID = type.classID;
                }
                if (header.m_Version < SerializedFileFormatVersion.HasScriptTypeIndex)
                {
                    objectInfo.isDestroyed = reader.ReadUInt16();
                }
                if (header.m_Version >= SerializedFileFormatVersion.HasScriptTypeIndex && header.m_Version < SerializedFileFormatVersion.RefactorTypeData)
                {
                    var m_ScriptTypeIndex = reader.ReadInt16();
                    if (objectInfo.serializedType != null)
                        objectInfo.serializedType.m_ScriptTypeIndex = m_ScriptTypeIndex;
                }
                if (header.m_Version == SerializedFileFormatVersion.SupportsStrippedObject || header.m_Version == SerializedFileFormatVersion.RefactoredClassId)
                {
                    objectInfo.stripped = reader.ReadByte();
                }
                m_Objects.Add(objectInfo);
            }

            Stream data_stream = null;
            if (header.m_FileSize == reader.Source.Length)
            {
                foreach (var obj in m_Objects)
                {
                    if (obj.classID != 142) //AssetBundle Object以外
                    {
                        reader.Source.Position = obj.byteStart;
                        var name_length = reader.ReadUInt32(); // ファイル名(拡張子無し)文字数
                        reader.Position += (name_length + 3) / 4 * 4; // 4バイト境界に合わせる
                        var data_size = reader.ReadInt32();
                        data_stream = new MemoryStream(data_size);
                        reader.CopyToStream(data_stream, data_size);
                        data_stream.Position = 0;
                        break;
                    }
                }
            }
            else
            {
                reader.Source.Position = header.m_FileSize;
                var size = reader.Source.Length - header.m_FileSize;
                data_stream = new MemoryStream((int)size);
                reader.CopyToStream(data_stream, size);
                data_stream.Position = 0;
            }

            return data_stream;
        }

        private SerializedType ReadSerializedType(AssetReader reader, SerializedFileHeader header, bool isRefType)
        {
            var type = new SerializedType();
            type.classID = reader.ReadInt32();

            if (header.m_Version >= SerializedFileFormatVersion.RefactoredClassId)
            {
                type.m_IsStrippedType = reader.ReadByte() != 0;
            }

            if (header.m_Version >= SerializedFileFormatVersion.RefactorTypeData)
            {
                type.m_ScriptTypeIndex = reader.ReadInt16();
            }

            if (header.m_Version >= SerializedFileFormatVersion.HasTypeTreeHashes)
            {
                if (isRefType && type.m_ScriptTypeIndex >= 0)
                {
                    type.m_ScriptID = reader.ReadBytes(16);
                }
                else if ((header.m_Version < SerializedFileFormatVersion.RefactoredClassId && type.classID < 0) || (header.m_Version >= SerializedFileFormatVersion.RefactoredClassId && type.classID == 114))
                {
                    type.m_ScriptID = reader.ReadBytes(16);
                }
                type.m_OldTypeHash = reader.ReadBytes(16);
            }

            if (m_EnableTypeTree)
            {
                type.m_Type = new TypeTree();
                type.m_Type.m_Nodes = new List<TypeTreeNode>();
                TypeTreeBlobRead(reader, header, type.m_Type);
                if (header.m_Version >= SerializedFileFormatVersion.StoresTypeDependencies)
                {
                    if (isRefType)
                    {
                        type.m_ClassName = reader.ReadCString();
                        type.m_NameSpace = reader.ReadCString();
                        type.m_AsmName = reader.ReadCString();
                    }
                    else
                    {
                        type.m_TypeDependencies = reader.ReadInt32Array();
                    }
                }
            }

            return type;
        }

        private void TypeTreeBlobRead(AssetReader reader, SerializedFileHeader header, TypeTree m_Type)
        {
            int numberOfNodes = reader.ReadInt32();
            int stringBufferSize = reader.ReadInt32();
            for (int i = 0; i < numberOfNodes; i++)
            {
                var typeTreeNode = new TypeTreeNode();
                m_Type.m_Nodes.Add(typeTreeNode);
                typeTreeNode.m_Version = reader.ReadUInt16();
                typeTreeNode.m_Level = reader.ReadByte();

                typeTreeNode.m_TypeFlags = reader.ReadByte();
                typeTreeNode.m_TypeStrOffset = reader.ReadUInt32();
                typeTreeNode.m_NameStrOffset = reader.ReadUInt32();
                typeTreeNode.m_ByteSize = reader.ReadInt32();
                typeTreeNode.m_Index = reader.ReadInt32();
                typeTreeNode.m_MetaFlag = reader.ReadInt32();
                if (header.m_Version >= SerializedFileFormatVersion.TypeTreeNodeWithTypeFlags)
                {
                    typeTreeNode.m_RefTypeHash = reader.ReadUInt64();
                }
            }
            m_Type.m_StringBuffer = reader.ReadBytes(stringBufferSize);

            using (var stringBufferReader = new BinaryStream(new MemoryStream(m_Type.m_StringBuffer), null))
            {
                for (int i = 0; i < numberOfNodes; i++)
                {
                    var m_Node = m_Type.m_Nodes[i];
                    m_Node.m_Type = ReadString(stringBufferReader, m_Node.m_TypeStrOffset);
                    m_Node.m_Name = ReadString(stringBufferReader, m_Node.m_NameStrOffset);
                }
            }

            string ReadString(BinaryStream stringBufferReader, uint value)
            {
                var isOffset = (value & 0x80000000) == 0;
                if (isOffset)
                {
                    stringBufferReader.AsStream.Position = value;
                    return stringBufferReader.ReadCString();
                }
                var offset = value & 0x7FFFFFFF;
                if (CommonString.StringBuffer.TryGetValue(offset, out var str))
                {
                    return str;
                }
                return offset.ToString();
            }
        }
    }
}

