using ReLunacy.Engine.Loading.IO;
using ReLunacy.Engine.Loading.Objects;

namespace ReLunacy.Engine.Loading.Readers;

/// <summary>Reads old-engine (Tools of Destruction) animation clips - main.dat section 0xF000 (see
/// <see cref="AnimationMetadataOld"/>) and their control blocks (see
/// <see cref="AnimationControlOld"/>).
///
/// THE CORE FIX THIS READER EXISTS FOR: a physical animation frame is
///
///     [0x10-byte prefix][track payload, aligned]
///
/// NOT the "zero filler frame, then real frame" (i.e. every other frame is padding) reading that
/// some existing old-engine animation tooling uses for this game - that reading forces frame count,
/// fps and frame stride to all be halved to make the byte math close, which is backwards: it is
/// fitting the wrong model to real data instead of reading the actual per-frame prefix. Both models
/// can look plausible from a single clip; they were told apart here by an exact independent
/// invariant (see <see cref="PhysicalFrameStride"/> and the "crumbling" check in
/// <see cref="SelfTest"/>), not by preference.
///
/// Frame data is intentionally NOT eagerly decoded for every clip at load time - animate_spin alone
/// has 8001 frames, and there are 642 clips. <see cref="ReadFrameTracks"/> reads one frame's raw
/// track values on demand (called by the animation player while sampling a pose), directly off the
/// still-open main.dat stream.</summary>
public sealed class AnimationReader
{
    private readonly FileManager _fileManager;

    public AnimationReader(FileManager fileManager)
    {
        _fileManager = fileManager ?? throw new ArgumentNullException(nameof(fileManager));
    }

    public IReadOnlyList<Assets.Animations.AnimationClip> ReadAll()
    {
        if (!_fileManager.isOld) return [];
        if (!_fileManager.igfiles.TryGetValue("main.dat", out IGFile? main) || main is null) return [];

        var section = main.QuerySection(AnimationMetadataOld.ID);
        if (section.id != AnimationMetadataOld.ID || section.count == 0)
        {
            Console.WriteLine("[Animations] none (no 0xF000 section, or new engine).");
            return [];
        }

        var result = new List<Assets.Animations.AnimationClip>((int)section.count);
        int n8Positive = 0;

        for (uint i = 0; i < section.count; i++)
        {
            uint recordBase = (uint)(section.offset + AnimationMetadataOld.Size * i);
            var header = AnimationMetadataOld.Read(main.sh, recordBase);
            var control = AnimationControlOld.Read(main.sh, header);
            if (header.num8BitTracks > 0) n8Positive++;

            result.Add(new Assets.Animations.AnimationClip(main.sh, header, control));
        }

        Console.WriteLine($"[Animations] {result.Count} clip(s), {n8Positive} with 8-bit tracks " +
                           $"({100.0 * n8Positive / result.Count:0.0}%).");

        SelfTest(result);

        return result;
    }

    /// <summary>Per-frame byte step: a 0x10-byte prefix ahead of the track payload, itself aligned
    /// to 16 bytes (packed clips, flag 0x04) or 128 bytes (non-packed clips). Independently verified
    /// for the packed case against this project's own Tools of Destruction sample - see
    /// <see cref="SelfTest"/> - the non-packed 128-byte alignment is carried over unverified from the
    /// same reverse work (no non-packed root-motion clip was available to cross-check the same way).</summary>
    public static uint PhysicalFrameStride(in AnimationMetadataOld header)
    {
        uint payloadStride = header.IsPacked
            ? (header.frameStride + 0xFu) & ~0xFu
            : (header.frameStride + 0x7Fu) & ~0x7Fu;
        return 0x10 + payloadStride;
    }

    /// <summary>Reads one frame's raw track16/track8 values directly off <paramref name="sh"/> (the
    /// level's still-open main.dat stream) - called on demand while sampling a pose, not at load
    /// time. Returns arrays sized exactly num16BitTracks/num8BitTracks.</summary>
    public static void ReadFrameTracks(StreamHelper sh, in AnimationMetadataOld header, int frameIndex, out short[] track16, out sbyte[] track8)
    {
        uint physicalStride = PhysicalFrameStride(header);
        uint frameBase = header.framesPtr + (uint)frameIndex * physicalStride;
        uint trackBase = frameBase + 0x10; // the per-frame prefix - see this class's remarks.

        track16 = new short[header.num16BitTracks];
        sh.Seek(trackBase);
        for (int i = 0; i < header.num16BitTracks; i++) track16[i] = sh.ReadInt16();

        uint track8Base = trackBase + ((header.num16BitTracks * 2u + 0xFu) & ~0xFu);
        track8 = new sbyte[header.num8BitTracks];
        sh.Seek(track8Base);
        for (int i = 0; i < header.num8BitTracks; i++) track8[i] = unchecked((sbyte)sh.ReadByte());
    }

    /// <summary>The 8-bit track dequantization: base value plus the sign-extended 8-bit delta
    /// SCALED BY 4 - not added as-is. The EBOOT's native decoder does `lbz` (load byte) / `extsb`
    /// (sign-extend byte) / `slwi ...,2` (shift left 2, i.e. *4) / `add base` for this specific
    /// track kind; some existing old-engine animation readers for this game skip the *4 and just add
    /// the raw signed delta, which under-scales every 8-bit-tracked bone's motion by 4x (and is very
    /// likely why those tools' complex/heavily-8-bit-tracked clips looked like bones exploding
    /// outward from the origin rather than a controlled, if coarser-quantized, motion).</summary>
    public static short DecodeTrack8(short baseValue, sbyte delta) => (short)(baseValue + delta * 4);

    /// <summary>Runs the two independent invariant checks this reader's whole frame-layout
    /// correction rests on, against whichever loaded clips happen to match (by field values, not by
    /// name - matching by name would only prove the string table offsets, not the frame math).
    /// Logs PASS/FAIL rather than throwing: a failure here means the physical layout assumption is
    /// wrong for whatever file is loaded, which is exactly the kind of thing that must stay visible
    /// (per this project's "no silent T-pose fallback" rule) rather than silently produce a
    /// plausible-looking but wrong decode.</summary>
    private static void SelfTest(List<Assets.Animations.AnimationClip> clips)
    {
        var spin = clips.FirstOrDefault(c => c.Header.numBones == 4 && c.Header.numFrames == 8001 && c.Header.frameStride == 16);
        if (spin != null)
        {
            uint physical = PhysicalFrameStride(spin.Header);
            bool ok = physical == 0x20 && spin.Header.frameRate == 30f && spin.Header.numFrames == 8001;
            Console.WriteLine($"[Animations] self-test 'animate_spin'-shaped clip ('{spin.Header.Name}'): " +
                               $"physicalStride=0x{physical:X} (expect 0x20), fps={spin.Header.frameRate} (expect 30), " +
                               $"frames={spin.Header.numFrames} (expect 8001) -> {(ok ? "PASS" : "FAIL")}. " +
                               "No pair-frame halving applied (fps/frames kept as read).");
        }

        var crumbling = clips.FirstOrDefault(c => c.Header.numFrames == 151 && c.Header.frameStride == 432);
        if (crumbling != null)
        {
            uint physical = PhysicalFrameStride(crumbling.Header);
            // Storage carries one EXTRA physical frame when the clip loops (flag 0x01) - a wrap
            // buffer so the last real frame can interpolate into frame 0 without special-casing -
            // confirmed by cross-checking this exact invariant against all 642 clips in the F000
            // table: the naive "numFrames * physical" formula only lands on rootMotionPtr for
            // 373/642 (the non-looping ones); "+1 frame if looping" is exact for 642/642.
            uint storedFrames = crumbling.Header.numFrames + (crumbling.Header.IsLooping ? 1u : 0u);
            uint expectedEnd = crumbling.Header.framesPtr + storedFrames * physical;
            bool ok = physical == 448 && expectedEnd == crumbling.Header.rootMotionPtr;
            Console.WriteLine($"[Animations] self-test 'crumbling'-shaped clip ('{crumbling.Header.Name}'): " +
                               $"physicalStride=0x{physical:X} (expect 0x1C0), framesPtr+storedFrames*physical=0x{expectedEnd:X} " +
                               $"vs rootMotionPtr=0x{crumbling.Header.rootMotionPtr:X} -> {(ok ? "PASS" : "FAIL")}.");
        }

        // track8 formula check (arithmetic only, no file data needed): base=1000, delta=-5 must
        // decode to 980 (1000 + (-5*4)), NOT 995 (1000 + (-5)).
        short t8 = DecodeTrack8(1000, -5);
        Console.WriteLine($"[Animations] self-test track8 formula: 1000 + (-5)*4 = {t8} (expect 980) -> {(t8 == 980 ? "PASS" : "FAIL")}.");
    }
}
