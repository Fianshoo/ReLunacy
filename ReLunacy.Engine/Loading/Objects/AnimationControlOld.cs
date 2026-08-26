using ReLunacy.Engine.Loading.IO;

namespace ReLunacy.Engine.Loading.Objects;

public enum AnimationTrackKind
{
    Rotation = 0,
    Scale = 1,
    Position = 2,
    Unknown = 3,
}

/// <summary>Decoded per-track routing mask (one raw u16 per reference/track16/track8 entry).
/// Bit layout carried over from an existing, already-working old-engine animation reader
/// (ReChimera, crates/lunalib/src/animation.rs) rather than independently re-derived here - this
/// project's own reverse work focused on the frame LAYOUT bug (see AnimationReader's remarks), not
/// the control block, which nothing found reason to doubt.</summary>
public readonly record struct AnimationTrackMask(ushort BoneIndex, byte Component, AnimationTrackKind Kind)
{
    public static AnimationTrackMask Unpack(ushort raw) => new(
        BoneIndex: (ushort)((raw >> 6) & 0x3FF),
        Component: (byte)((raw >> 2) & 0b11),
        Kind: (AnimationTrackKind)((raw >> 4) & 0b11));
}

/// <summary>Old-engine animation control block: per-clip reference pose + the mask/base tables that
/// say which bone/component each frame's track16/track8 payload entry drives. Pointed to by
/// <see cref="AnimationMetadataOld.controlPtr"/> (absolute main.dat offset).
///
/// Sub-block layout (each aligned to 16 bytes from the previous one's end) ported from ReChimera's
/// read_animation_control, which is itself cross-checked against a much larger and more varied
/// clip set (multiple Insomniac PS3 titles) than this project has independently verified against -
/// see this type's file remarks for what specifically WAS independently checked for Tools of
/// Destruction.</summary>
public sealed class AnimationControlOld
{
    public short[][] RefPoseRotations = []; // [bone][4] raw quantized (x,y,z,w)
    public short[] RefPoseValues = [];
    public AnimationTrackMask[] RefPoseMasks = [];
    public AnimationTrackMask[] Track16Masks = [];
    public AnimationTrackMask[] Track8Masks = [];
    public short[] Track8BaseValues = [];
    public byte[] BlendMasks = [];

    private static uint Align16(uint v) => (v + 15u) & ~15u;

    public static AnimationControlOld Read(StreamHelper sh, in AnimationMetadataOld header)
    {
        var ctrl = new AnimationControlOld();
        if (header.controlPtr == 0)
            return ctrl;

        uint nb = header.numBones;
        uint nrv = header.numReferenceValues;
        uint n16 = header.num16BitTracks;
        uint n8 = header.num8BitTracks;

        uint offRotations = 0;
        uint offValues = Align16(offRotations + nb * 8);
        uint offValueMasks = Align16(offValues + nrv * 2);
        uint offT16Masks = Align16(offValueMasks + nrv * 2);
        uint offT8Masks = Align16(offT16Masks + n16 * 2);
        uint offT8Base = Align16(offT8Masks + n8 * 2);

        uint baseOff = header.controlPtr;

        sh.Seek(baseOff + offRotations);
        ctrl.RefPoseRotations = new short[nb][];
        for (int i = 0; i < nb; i++)
            ctrl.RefPoseRotations[i] = [sh.ReadInt16(), sh.ReadInt16(), sh.ReadInt16(), sh.ReadInt16()];

        sh.Seek(baseOff + offValues);
        ctrl.RefPoseValues = new short[nrv];
        for (int i = 0; i < nrv; i++) ctrl.RefPoseValues[i] = sh.ReadInt16();

        sh.Seek(baseOff + offValueMasks);
        ctrl.RefPoseMasks = new AnimationTrackMask[nrv];
        for (int i = 0; i < nrv; i++) ctrl.RefPoseMasks[i] = AnimationTrackMask.Unpack(sh.ReadUInt16());

        sh.Seek(baseOff + offT16Masks);
        ctrl.Track16Masks = new AnimationTrackMask[n16];
        for (int i = 0; i < n16; i++) ctrl.Track16Masks[i] = AnimationTrackMask.Unpack(sh.ReadUInt16());

        sh.Seek(baseOff + offT8Masks);
        ctrl.Track8Masks = new AnimationTrackMask[n8];
        for (int i = 0; i < n8; i++) ctrl.Track8Masks[i] = AnimationTrackMask.Unpack(sh.ReadUInt16());

        sh.Seek(baseOff + offT8Base);
        ctrl.Track8BaseValues = new short[n8];
        for (int i = 0; i < n8; i++) ctrl.Track8BaseValues[i] = sh.ReadInt16();

        return ctrl;
    }
}
