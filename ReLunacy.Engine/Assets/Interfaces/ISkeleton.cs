using System.Numerics;

namespace ReLunacy.Engine.Assets.Interfaces;

public interface IBone
{
    string Name { get; }
    int ParentIndex { get; }
    Matrix4x4 WorldBindPose { get; }
    Matrix4x4 InverseBindPose { get; }

    /// <summary>Raw D300 bone flags. Kept verbatim so animation/render semantics can be
    /// reverse-engineered without losing source information. Old-engine ToD data observed so far
    /// overwhelmingly uses 0 or bit 0; bit 0 is handled by the animation hierarchy as the
    /// scale-inheritance control.</summary>
    ushort Flags { get; }
}

public interface ISkeleton
{
    IReadOnlyList<IBone> Bones { get; }
    int RootBoneIndex { get; }
    float PositionScale { get; }
    float ScaleScale { get; }

    /// <summary>Raw D300 +0x11 fixed-point shift used by the native old-engine decoder for the
    /// quantized rotation channel. Retained explicitly for round-tripping/editing even though a
    /// uniform quaternion scale cancels when the decoded quaternion is normalized.</summary>
    byte RotationShift { get; }

    /// <summary>Reference-pose local translations decoded from D300 +0x14.</summary>
    IReadOnlyList<Vector3> ReferenceTranslations { get; }
}
