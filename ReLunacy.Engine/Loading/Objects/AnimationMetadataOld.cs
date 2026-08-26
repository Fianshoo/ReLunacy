using ReLunacy.Engine.Loading.IO;
using ReLunacy.Engine.Loading.Interfaces;

namespace ReLunacy.Engine.Loading.Objects;

/// <summary>Old-engine (Tools of Destruction) animation clip header — main.dat section 0xF000,
/// 0x40 bytes per record.
///
/// Layout and the two corrections below are independently verified against this project's own
/// Tools of Destruction metropolis main.dat (not just asserted from an EBOOT read):
///   - Section 0xF000 at file offset 0x198680, 642 records of 0x40 bytes: exact match.
///   - Of the 642 records, 605 have num8BitTracks &gt; 0 and 37 have 0 (94.2%): exact match against
///     the independently-reported ratio, which pins down +0x36/+0x38 being num16BitTracks/
///     num8BitTracks rather than some other pair of fields.
///   - namePtr resolves (as a plain absolute file offset, no relocation) to the C strings
///     "animate_spin" and "crumbling" for two specific records picked purely by their numeric field
///     values (bones/frames/stride) — see AnimationReader's remarks for the exact check.
///   - The clip "crumbling" (numFrames=151, frameStride=432, flags=0x04 i.e. packed, non-looping):
///     computing framesPtr + numFrames * PhysicalFrameStride(header) lands EXACTLY on rootMotionPtr
///     (0x25E680 + 151*448 = 0x26EEC0). Checked against all 642 clips in this main.dat: this exact
///     "numFrames * physical" formula only holds for the 373/642 NON-LOOPING clips - a looping clip
///     (flags &amp; 0x01) stores one extra physical frame (a wrap buffer letting the last real frame
///     interpolate into frame 0), so the general invariant is
///     <c>framesPtr + (numFrames + (looping ? 1 : 0)) * physical == rootMotionPtr</c>, exact for
///     642/642. This is the proof for <see cref="AnimationReader.PhysicalFrameStride"/>
///     — a per-frame 0x10-byte prefix ahead of the track payload, not the "two frames" (zero
///     filler + real frame) interpretation some other old-engine animation readers use for this
///     game, which this project's own reverse work found to be wrong for Tools of Destruction
///     specifically (see AnimationReader's remarks for the full comparison).
///
/// NOT independently verified here (kept as documented, reasoned assumptions rather than proven
/// facts): the exact 8-bit-track dequantization scale (base + signExtend(delta8) * 4) and the
/// control-block sub-array layout, both carried over from the same reverse work's cross-check
/// against a widely-used old-engine animation reader's already-working control-block code — see
/// AnimationControlOld and AnimationReader.DecodeTrack8.</summary>
[FileStructure(0x40)]
public record struct AnimationMetadataOld : ILunaSerializable
{
    public const uint ID = 0xF000;
    public const uint Size = 0x40;

    [FileOffset(0x00)] public ushort animIndex;
    [FileOffset(0x02)] public ushort flags;
    [FileOffset(0x04)] public ushort numBones;
    [FileOffset(0x06)] public ushort numFrames;

    [FileOffset(0x08)] public uint namePtr;

    /// <summary>Raw, unidentified 4 bytes at +0x0C (present in every record; not zero).</summary>
    [FileOffset(0x0C)] public uint Unknown0C;
    /// <summary>Raw, unidentified float at +0x10.</summary>
    [FileOffset(0x10)] public float Unknown10;

    [FileOffset(0x14)] public float linearSpeed;
    [FileOffset(0x18)] public float frameRate;

    /// <summary>Root motion data pointer (absolute main.dat file offset). Not decoded yet - see
    /// AnimationPlayer's ApplyRootMotion (default OFF): Phase 1 only uses this pointer as a
    /// cross-check for <see cref="AnimationReader.PhysicalFrameStride"/> (framesPtr + numFrames *
    /// physicalStride should land here for a packed clip with root motion - see this struct's
    /// remarks for the "crumbling" example).</summary>
    [FileOffset(0x1C)] public uint rootMotionPtr;

    [FileOffset(0x20)] public uint controlPtr;
    [FileOffset(0x24)] public uint framesPtr;

    /// <summary>Raw, unidentified 14 bytes at +0x28..+0x35 (10 bytes to +0x32's frameStride, plus
    /// alignment - kept whole rather than split into guessed sub-fields).</summary>
    [FileOffset(0x28)] public ulong Unknown28;
    [FileOffset(0x30)] public ushort Unknown30;

    /// <summary>On-disk payload size per frame BEFORE the per-frame 0x10-byte prefix and before
    /// alignment - see AnimationReader.PhysicalFrameStride for how this becomes the real byte step
    /// between frames.</summary>
    [FileOffset(0x32)] public ushort frameStride;
    [FileOffset(0x34)] public ushort numReferenceValues;
    [FileOffset(0x36)] public ushort num16BitTracks;
    [FileOffset(0x38)] public ushort num8BitTracks;

    /// <summary>Raw, unidentified 6 bytes at +0x3A..+0x3F.</summary>
    [FileOffset(0x3A)] public uint Unknown3A;
    [FileOffset(0x3E)] public ushort Unknown3E;

    public readonly bool IsLooping => (flags & 0x01) != 0;
    public readonly bool IsAdditive => (flags & 0x02) != 0;
    /// <summary>Bit 0x04: packed frame payload, aligned to 16 bytes. Clear means the payload is
    /// aligned to 128 bytes instead - see AnimationReader.PhysicalFrameStride.</summary>
    public readonly bool IsPacked => (flags & 0x04) != 0;

    public string Name;

    public static AnimationMetadataOld Read(StreamHelper sh, uint recordBase)
    {
        sh.Seek(recordBase);
        var h = FileUtils.ReadStructure<AnimationMetadataOld>(sh);
        h.Name = h.namePtr != 0 ? sh.ReadString(h.namePtr) : string.Empty;
        return h;
    }

    public readonly byte[] ToBytes(bool isOld, params object[]? additionalParams) => throw new NotImplementedException();
}
