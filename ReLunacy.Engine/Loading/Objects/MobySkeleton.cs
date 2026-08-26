using System.Numerics;
using ReLunacy.Engine.Loading.Interfaces;
using ReLunacy.Engine.Loading.IO;

namespace ReLunacy.Engine.Loading.Objects;

/// <summary>
/// On-disk skeleton header (0x1C bytes) — layout confirmed against InsomniaToolset's `Skeleton`
/// struct, referenced identically (same field offsets) from both MobyV1 (old engine) and MobyV2
/// (new engine)'s own `skeleton` pointer field.
///
/// `tms0`/`tms1` are NOT read via the [Reference] array mechanism like `bones` — that mechanism
/// (FileUtils.ReadStructureArray) requires the element type to carry its own [FileStructure] size,
/// which a raw System.Numerics.Matrix4x4 doesn't have — so MobySkeletonReader dereferences
/// `tms0Pointer`/`tms1Pointer` manually, the same way RegionReader.ReadMatrix4x4 already does for
/// volume placement matrices.
/// </summary>
[FileStructure(0x1C)]
public record struct MobySkeleton : ILunaSerializable
{
    public const uint ID = 0xD300;
    public const uint Size = 0x1C;

    [FileOffset(0x00)] public ushort numBones;
    [FileOffset(0x02)] public ushort rootBone;
    [FileOffset(0x04), Reference(nameof(NumBones))] public MobyBone[] bones;
    [FileOffset(0x08)] public uint tms0Pointer;
    [FileOffset(0x0C)] public uint tms1Pointer;
    // Verified against real main.dat (metropolis level, 201 skeletons scanned): these are single
    // BYTES, not ushorts — a ushort read here always returns 0x0400 (1024) because the byte right
    // after each shift is always 0 padding, which silently produced a bogus "shift" that a
    // defensive Math.Clamp(0,15) in MobyReader.ConvertSkeleton then collapsed to posScale=1.0 (no
    // scaling at all) instead of the correct ~1/2048. scaleShift is constant (4) across every
    // skeleton observed; translationShift varies per-skeleton (seen: 1,4,6,7,8,9) — a sane 0-15
    // shift range, confirming the byte read is right and the ushort read was the bug.
    [FileOffset(0x10)] public byte scaleShift;
    [FileOffset(0x11)] public byte scaleShiftPad;
    [FileOffset(0x12)] public byte translationShift;
    [FileOffset(0x13)] public byte translationShiftPad;
    [FileOffset(0x14)] public uint spuRefPoseBufferPointer;
    [FileOffset(0x18)] public uint unkOffsetPointer;

    /// <summary>Bone i's bind-pose transform in moby-local space (not relative to its parent) —
    /// populated manually by MobySkeletonReader, not by the [FileOffset]-driven reflection pass.</summary>
    public Matrix4x4[] tms0;
    /// <summary>Inverse of tms0[i] — the matrix GPU skinning multiplies a vertex by, and also what
    /// InsomniaToolset composes against a child's tms0 to derive that child's parent-local transform
    /// (see MobySkeletonReader.ComputeLocalBindPose).</summary>
    public Matrix4x4[] tms1;

    public readonly uint NumBones => numBones;

    public readonly byte[] ToBytes(bool isOld, params object[]? additionalParams) => throw new NotImplementedException();
}
