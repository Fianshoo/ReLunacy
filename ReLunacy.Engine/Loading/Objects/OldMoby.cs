using System.Numerics;
using ReLunacy.Engine.Loading.Interfaces;
using ReLunacy.Engine.Loading.IO;

namespace ReLunacy.Engine.Loading.Objects;

[FileStructure(0xC0)]
public record struct OldMoby : IMoby
{
    public const uint ID = 0xD100;
    public const uint Size = 0xC0;

    [FileOffset(0x00)] public Vector4 boundingSphere;
    [FileOffset(0x10)] public ushort Unk1;
    [FileOffset(0x12)] public ushort Unk2;
    [FileOffset(0x14)] public ushort bonesCount;
    /// <summary>Number of animation clips owned by this Moby - independently verified against
    /// the real EBOOT (function ~0x6B6150 reads this exact field to size the runtime animation
    /// array it allocates) and against main.dat data: summing this field over every old-engine
    /// Moby in a level's animsets covers main.dat section 0xF000's 642 records exactly once each,
    /// with no gaps or overlaps. See <see cref="animationListPointer"/>.</summary>
    [FileOffset(0x16)] public ushort animationCount;
    [FileOffset(0x18)] public ushort bangleCount;
    [FileOffset(0x1A)] public ushort mobyId;
    [FileOffset(0x1C)] public ushort Null1;
    [FileOffset(0x1E)] public byte UnkBool;
    [FileOffset(0x1F)] public byte Null2;
    [FileOffset(0x20)] public uint skeletonPointer;
    /// <summary>Absolute main.dat offset (NOT relative to this moby's own stream, unlike
    /// skeletonPointer/boneMapOffset) of an array of <see cref="animationCount"/> u32 pointers.
    /// Each pointer is itself an absolute main.dat offset landing exactly on one 0xF000 record
    /// (i.e. <c>(pointer - f000Section.offset) % 0x40 == 0</c> for every entry, over every animated
    /// Moby in the level) - the actual per-Moby animation list, not a heuristic. Section 0xD500
    /// (642 x u32, one per 0xF000 record in the SAME order) confirms this same pointer table
    /// structure exists as a flat array elsewhere in main.dat, but this field is what a Moby
    /// actually walks: verified by resolving it for two different Mobys and finding their target
    /// 0xF000 names read back as coherent, class-appropriate animation sets (e.g. "guard_melee_idle*"
    /// for a 285-bone humanoid, "bot_security_*" for a 76-bone bot), not by name matching -
    /// see Loading.Readers.MobyAnimationResolver.</summary>
    [FileOffset(0x24)] public uint animationListPointer;
    [FileOffset(0x28), Reference(nameof(BangleCount))] public MobyBangle[] mobyBangles;
    [FileOffset(0x2C)] public uint UnkPointer2;
    [FileOffset(0x30)] public uint Null3;
    [FileOffset(0x34)] public uint indicesOffset;
    [FileOffset(0x38)] public uint verticesOffset;
    [FileOffset(0x3C)] public float scale;

    public readonly uint BangleCount => bangleCount;

    private ulong _tuid;
    public ulong TUID { readonly get => _tuid; init => _tuid = value; }

    public MobyBangle[] bangles { readonly get => mobyBangles; set => mobyBangles = value; }

    public static OldMoby Read(StreamHelper sh, int index)
    {
        var moby = FileUtils.ReadStructure<OldMoby>(sh);
        moby._tuid = (ulong)index;
        return moby;
    }

    public byte[] ToBytes(bool isOld, params object[]? additionalParams) => throw new NotImplementedException();
}
