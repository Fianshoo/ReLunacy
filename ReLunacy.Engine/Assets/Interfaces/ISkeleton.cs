using System.Numerics;

namespace ReLunacy.Engine.Assets.Interfaces;

public interface IBone
{
    string Name { get; }
    /// <summary>Index into the owning ISkeleton.Bones list; -1 for the root bone.</summary>
    int ParentIndex { get; }
    /// <summary>Bind-pose transform in moby-local space (not relative to the parent bone).</summary>
    Matrix4x4 WorldBindPose { get; }
    /// <summary>Inverse of WorldBindPose — the matrix GPU skinning multiplies a vertex by.</summary>
    Matrix4x4 InverseBindPose { get; }
}

public interface ISkeleton
{
    IReadOnlyList<IBone> Bones { get; }
    int RootBoneIndex { get; }

    /// <summary>Dequantization scale for animation position tracks: 1 / (0x8000 >> translationShift)
    /// off the old-engine skeleton header (main.dat 0xD300 +0x12). 1 when unknown/unavailable (new
    /// engine, or a skeleton read without this field) - a scale of 1 leaves quantized values
    /// unscaled rather than silently wrong-by-a-guessed-factor.</summary>
    float PositionScale { get; }

    /// <summary>Dequantization scale for animation scale tracks: 1 / (0x8000 >> scaleShift) off the
    /// same header (+0x10). See <see cref="PositionScale"/>.</summary>
    float ScaleScale { get; }
}
