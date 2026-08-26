using ReLunacy.Engine.Loading.IO;
using ReLunacy.Engine.Loading.Objects;

namespace ReLunacy.Engine.Loading.Readers;

/// <summary>Resolves an old-engine Moby's OWN animation set - main.dat 0xD100 fields
/// <see cref="OldMoby.animationCount"/> (+0x16) and <see cref="OldMoby.animationListPointer"/>
/// (+0x24) - into global indices into the 0xF000 animation table (see
/// <see cref="AnimationReader"/>).
///
/// This is the actual binary Moby-&gt;animation relationship the game uses, found by tracing the
/// EBOOT's own animation-array setup (the function that allocates <c>animationCount * 0x20</c>
/// bytes reads +0x16 for the count) and confirmed against main.dat: main.dat section 0xD500 is a
/// flat array of 642 u32 pointers, one per 0xF000 record in order
/// (<c>D500[i] == f000Section.offset + i * 0x40</c> for all 642 entries), and every animated
/// Moby's own +0x24 list points somewhere inside that same pointer space. Summing +0x16 over every
/// Moby in a level's distinct animsets covers all 642 0xF000 records exactly once - there is no
/// name-matching or bone-count heuristic involved.
///
/// A Moby with no animation set at all (animationCount == 0, the common case - static props,
/// triggers, etc.) resolves to an empty list, not an error.</summary>
public static class MobyAnimationResolver
{
    /// <param name="sh">The level's main.dat stream (old-engine Mobys are read directly out of
    /// main.dat, so animationListPointer - itself a main.dat-absolute offset, unlike
    /// skeletonPointer - is valid against this same stream).</param>
    /// <param name="f000SectionOffset">main.dat section 0xF000's own offset, for converting each
    /// resolved pointer back into a global "which of the 642 clips" index.</param>
    public static List<int> Resolve(StreamHelper sh, ushort animationCount, uint animationListPointer, uint f000SectionOffset)
    {
        var indices = new List<int>(animationCount);
        if (animationCount == 0 || animationListPointer == 0)
            return indices;

        for (int i = 0; i < animationCount; i++)
        {
            uint clipPtr = sh.ReadUInt32(animationListPointer + (uint)i * 4);
            if (clipPtr < f000SectionOffset) continue;

            uint delta = clipPtr - f000SectionOffset;
            if (delta % AnimationMetadataOld.Size != 0) continue; // doesn't land on a real record - skip rather than misresolve

            indices.Add((int)(delta / AnimationMetadataOld.Size));
        }

        return indices;
    }
}
