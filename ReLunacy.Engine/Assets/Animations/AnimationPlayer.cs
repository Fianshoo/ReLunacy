using System.Numerics;
using ReLunacy.Engine.Assets.Interfaces;
using ReLunacy.Engine.Loading.Objects;

namespace ReLunacy.Engine.Assets.Animations;

/// <summary>Plays one <see cref="AnimationClip"/> against a skeleton: owns transport state (play/
/// pause/stop/seek/loop/speed) and turns the current time into per-bone skin matrices. Knows
/// nothing about rendering or the UI - <see cref="SamplePose"/> hands back plain matrices for
/// whatever CPU/GPU skinning path consumes them (currently the Asset Viewer's CPU skin preview).</summary>
public sealed class AnimationPlayer
{
    public AnimationClip? Clip { get; private set; }
    public bool IsPlaying { get; private set; }
    public bool Loop { get; set; }
    public float Speed { get; set; } = 1f;
    public bool ApplyRootMotion { get; set; } // Phase 1: root motion is never applied regardless of this flag - see class remarks on SamplePose.

    /// <summary>Current playback position, in seconds. Clamped to [0, duration] when not looping.</summary>
    public float Time { get; private set; }

    /// <summary>Frame index at the current <see cref="Time"/>. The epsilon matters: seeking to an
    /// exact frame stores Time = frame / FrameRate, and multiplying that back by FrameRate in float
    /// lands a hair BELOW the integer (e.g. 1/30f*30f == 0.99999994), so a bare (int) cast truncated
    /// straight back to the previous frame — which made frame-stepping look like a dead button.</summary>
    public int CurrentFrame => Clip is null || Clip.FrameRate <= 0f
        ? 0
        : Math.Clamp((int)MathF.Floor(Time * Clip.FrameRate + 1e-3f), 0, Math.Max(0, Clip.NumFrames - 1));

    public void SetClip(AnimationClip? clip, bool resetTime = true)
    {
        Clip = clip;
        if (resetTime) Time = 0f;
        IsPlaying = false;
    }

    /// <summary>Starts (or resumes) playback. A non-looping clip that has already run to the end
    /// parks at Time == duration with IsPlaying false; without the rewind below, pressing Play there
    /// would set IsPlaying true only for the very next Update to immediately re-detect the end and
    /// clear it again — the button would look permanently dead after the first playthrough.</summary>
    public void Play()
    {
        if (Clip is null) return;
        if (!Loop && Clip.DurationSeconds > 0f && Time >= Clip.DurationSeconds)
            Time = 0f;
        IsPlaying = true;
    }

    public void Pause() => IsPlaying = false;
    public void Stop() { IsPlaying = false; Time = 0f; }

    public void Seek(float seconds)
    {
        if (Clip is null) { Time = 0f; return; }
        float duration = Clip.DurationSeconds;
        Time = duration > 0f ? (Loop ? Wrap(seconds, duration) : Math.Clamp(seconds, 0f, duration)) : 0f;
    }

    /// <summary>Seeks to an exact frame index — wrapping when <see cref="Loop"/> is set, clamping
    /// otherwise. Preferred over Seek(frame / FrameRate) for anything frame-quantized (stepping,
    /// the timeline scrubber): it keeps the integer intent instead of round-tripping through a
    /// float that can land just short of the frame boundary.</summary>
    public void SeekToFrame(int frame)
    {
        if (Clip is null || Clip.FrameRate <= 0f || Clip.NumFrames <= 0) { Time = 0f; return; }

        int last = Clip.NumFrames - 1;
        frame = Loop
            ? ((frame % Clip.NumFrames) + Clip.NumFrames) % Clip.NumFrames
            : Math.Clamp(frame, 0, last);

        Time = frame / Clip.FrameRate;
    }

    public void Update(float dt)
    {
        if (!IsPlaying || Clip is null || Clip.FrameRate <= 0f) return;

        float duration = Clip.DurationSeconds;
        if (duration <= 0f) { IsPlaying = false; return; }

        Time += dt * Speed;

        if (Loop)
        {
            Time = Wrap(Time, duration);
        }
        else if (Time >= duration)
        {
            Time = duration;
            IsPlaying = false;
        }
        else if (Time < 0f)
        {
            Time = 0f;
            IsPlaying = false;
        }
    }

    private static float Wrap(float t, float duration)
    {
        float r = t % duration;
        return r < 0f ? r + duration : r;
    }

    /// <summary>Per-bone quantized pose for one frame, decoded on demand from the clip's control
    /// block + that one frame's raw track data. Kept as a small private record rather than exposed:
    /// callers only ever need the final dequantized/interpolated result from SamplePose.</summary>
    private readonly struct FramePose
    {
        public readonly short[] Rotation; // bone*4, quantized (x,y,z,w)
        public readonly short[] Position; // bone*3, quantized
        public readonly byte[] PositionMask; // bone, bit per component actually driven by a track/static ref
        public readonly short[] Scale; // bone*3, quantized
        public readonly byte[] ScaleMask;
        public readonly bool[] RotationAnimated;
        public readonly bool[] PositionAnimated;
        public readonly bool[] ScaleAnimated;

        public FramePose(int numBones)
        {
            Rotation = new short[numBones * 4];
            Position = new short[numBones * 3];
            PositionMask = new byte[numBones];
            Scale = new short[numBones * 3];
            ScaleMask = new byte[numBones];
            RotationAnimated = new bool[numBones];
            PositionAnimated = new bool[numBones];
            ScaleAnimated = new bool[numBones];
        }
    }

    private static FramePose DecodeFramePose(AnimationClip clip, int frameIndex, int numBones)
    {
        var pose = new FramePose(numBones);
        var ctrl = clip.Control;

        // Baseline rotation = this bone's reference-pose quaternion (identity-ish [0,0,0,32767] if
        // the clip carries none for this bone at all - a clip's numBones can be smaller than the
        // skeleton's, see effectiveBoneCount remarks on SamplePose).
        for (int b = 0; b < numBones; b++)
        {
            var r = b < ctrl.RefPoseRotations.Length ? ctrl.RefPoseRotations[b] : null;
            pose.Rotation[b * 4 + 0] = r?[0] ?? 0;
            pose.Rotation[b * 4 + 1] = r?[1] ?? 0;
            pose.Rotation[b * 4 + 2] = r?[2] ?? 0;
            pose.Rotation[b * 4 + 3] = r?[3] ?? 32767;
        }

        // Static per-(bone,component) reference overrides - same value on every frame. Position/
        // scale only; rotation's per-clip base already comes from RefPoseRotations above.
        for (int i = 0; i < ctrl.RefPoseMasks.Length && i < ctrl.RefPoseValues.Length; i++)
        {
            var m = ctrl.RefPoseMasks[i];
            if (m.BoneIndex >= numBones || m.Component >= 3) continue;
            short v = ctrl.RefPoseValues[i];
            if (m.Kind == AnimationTrackKind.Position)
            {
                pose.Position[m.BoneIndex * 3 + m.Component] = v;
                pose.PositionMask[m.BoneIndex] |= (byte)(1 << m.Component);
            }
            else if (m.Kind == AnimationTrackKind.Scale)
            {
                pose.Scale[m.BoneIndex * 3 + m.Component] = v;
                pose.ScaleMask[m.BoneIndex] |= (byte)(1 << m.Component);
            }
        }

        clip.ReadFrame(frameIndex, out short[] track16, out sbyte[] track8);

        void Apply(AnimationTrackMask m, short value)
        {
            if (m.BoneIndex >= numBones) return;
            switch (m.Kind)
            {
                case AnimationTrackKind.Rotation when m.Component < 4:
                    pose.Rotation[m.BoneIndex * 4 + m.Component] = value;
                    pose.RotationAnimated[m.BoneIndex] = true;
                    break;
                case AnimationTrackKind.Position when m.Component < 3:
                    pose.Position[m.BoneIndex * 3 + m.Component] = value;
                    pose.PositionMask[m.BoneIndex] |= (byte)(1 << m.Component);
                    pose.PositionAnimated[m.BoneIndex] = true;
                    break;
                case AnimationTrackKind.Scale when m.Component < 3:
                    pose.Scale[m.BoneIndex * 3 + m.Component] = value;
                    pose.ScaleMask[m.BoneIndex] |= (byte)(1 << m.Component);
                    pose.ScaleAnimated[m.BoneIndex] = true;
                    break;
            }
        }

        for (int i = 0; i < ctrl.Track16Masks.Length && i < track16.Length; i++)
            Apply(ctrl.Track16Masks[i], track16[i]);

        for (int i = 0; i < ctrl.Track8Masks.Length && i < track8.Length; i++)
        {
            short baseValue = i < ctrl.Track8BaseValues.Length ? ctrl.Track8BaseValues[i] : (short)0;
            Apply(ctrl.Track8Masks[i], Loading.Readers.AnimationReader.DecodeTrack8(baseValue, track8[i]));
        }

        return pose;
    }

    private static Quaternion DequantizeRotation(short[] raw, int bone)
    {
        const float inv = 1f / 32767f;
        var q = new Quaternion(raw[bone * 4 + 0] * inv, raw[bone * 4 + 1] * inv, raw[bone * 4 + 2] * inv, raw[bone * 4 + 3] * inv);
        float lenSq = q.LengthSquared();
        return lenSq > 1e-12f ? Quaternion.Multiply(q, 1f / MathF.Sqrt(lenSq)) : Quaternion.Identity;
    }

    /// <summary>Samples the current time into one moby-space SKIN matrix per skeleton bone -
    /// <c>InverseBindPose[bone] * animatedWorld[bone]</c> (row-vector convention: strip the bind
    /// transform, then reapply the animated one), the matrix a CPU/GPU skin pass multiplies each
    /// influencing vertex's BIND-pose position by.
    ///
    /// EFFECTIVE BONE COUNT: uses skeleton.Bones.Count for the hierarchy walk (every bone always
    /// gets a matrix, so a partial-rig clip can't leave trailing bones unset), but only trusts the
    /// clip's own track data for bone indices below clip.NumBones - a track mask never references a
    /// bone beyond what the clip's own header declares, so this is a safety bound, not a guess at
    /// which count is "correct". See AnimationMetadataOld remarks - this project has not indep-
    /// endently checked a clip whose header.numBones disagrees with the target skeleton's bone
    /// count (i.e. the header/skeleton/effective distinction the reverse notes flag as worth
    /// auditing), so no such remapping is attempted here yet.
    ///
    /// ROOT MOTION is never applied regardless of <see cref="ApplyRootMotion"/> - the format at
    /// AnimationMetadataOld.rootMotionPtr is not decoded (Phase 1 only uses that pointer as a
    /// frame-layout cross-check, see AnimationReader.SelfTest). The flag exists so the Asset Viewer
    /// can show the intended checkbox now without silently pretending root motion works.
    ///
    /// UN-ANIMATED CHANNELS fall back to this bone's own LOCAL bind-pose translation/rotation/scale
    /// (derived from WorldBindPose/InverseBindPose - see DecomposeLocalBind), not to the identity -
    /// a bone the clip never touches should stay exactly where the bind pose puts it relative to its
    /// parent, not snap to the moby's origin.</summary>
    /// <summary>Moby-space animated transform per bone from the most recent <see cref="SamplePose"/>
    /// call - what <see cref="SamplePose"/>'s skin matrices are derived from
    /// (<c>skin[b] = InverseBindPose[b] * LastAnimatedWorld[b]</c>). Exposed so a skeleton overlay
    /// can draw the ANIMATED pose (bone.Translation per element) instead of recomputing the same
    /// hierarchy walk a second time.</summary>
    public Matrix4x4[]? LastAnimatedWorld { get; private set; }

    public Matrix4x4[] SamplePose(ISkeleton skeleton)
    {
        int numBones = skeleton.Bones.Count;
        var skin = new Matrix4x4[numBones];

        if (Clip is null || Clip.FrameRate <= 0f || Clip.NumFrames <= 0)
        {
            for (int i = 0; i < numBones; i++) skin[i] = Matrix4x4.Identity;
            LastAnimatedWorld = null;
            return skin;
        }

        float t = Time * Clip.FrameRate;
        int frameA = Math.Clamp((int)MathF.Floor(t), 0, Clip.NumFrames - 1);
        int frameB = Loop ? (frameA + 1) % Clip.NumFrames : Math.Min(frameA + 1, Clip.NumFrames - 1);
        float frac = Clip.NumFrames > 1 ? t - MathF.Floor(t) : 0f;

        int clipBones = Math.Min(Clip.NumBones, numBones);
        var poseA = DecodeFramePose(Clip, frameA, clipBones);
        var poseB = frameA == frameB ? poseA : DecodeFramePose(Clip, frameB, clipBones);

        var localTranslation = new Vector3[numBones];
        var localRotation = new Quaternion[numBones];
        var localScale = new Vector3[numBones];

        for (int b = 0; b < numBones; b++)
        {
            var (bindT, bindR, bindS) = DecomposeLocalBind(skeleton, b);

            if (b < clipBones && poseA.RotationAnimated[b])
            {
                var qa = DequantizeRotation(poseA.Rotation, b);
                var qb = DequantizeRotation(poseB.Rotation, b);
                localRotation[b] = Quaternion.Slerp(qa, qb, frac);
            }
            else if (b < clipBones)
            {
                // No per-frame track, but the clip may still carry a per-clip reference quaternion
                // for this bone (RefPoseRotations) that differs from the skeleton bind pose -
                // that reference IS the pose for a bone this clip poses statically.
                localRotation[b] = DequantizeRotation(poseA.Rotation, b);
            }
            else
            {
                localRotation[b] = bindR;
            }

            if (b < clipBones && poseA.PositionAnimated[b])
            {
                localTranslation[b] = LerpQuantized3(poseA.Position, poseB.Position, poseA.PositionMask[b], b, frac, skeleton.PositionScale, bindT);
            }
            else if (b < clipBones && poseA.PositionMask[b] != 0)
            {
                localTranslation[b] = LerpQuantized3(poseA.Position, poseA.Position, poseA.PositionMask[b], b, 0f, skeleton.PositionScale, bindT);
            }
            else
            {
                localTranslation[b] = bindT;
            }

            if (b < clipBones && poseA.ScaleAnimated[b])
            {
                localScale[b] = LerpQuantized3(poseA.Scale, poseB.Scale, poseA.ScaleMask[b], b, frac, skeleton.ScaleScale, bindS);
            }
            else if (b < clipBones && poseA.ScaleMask[b] != 0)
            {
                localScale[b] = LerpQuantized3(poseA.Scale, poseA.Scale, poseA.ScaleMask[b], b, 0f, skeleton.ScaleScale, bindS);
            }
            else
            {
                localScale[b] = bindS;
            }
        }

        // Hierarchy walk: does NOT assume bones are stored parent-before-child by array index.
        // MobySkeletonReader's own cycle check only walks parent CHAINS to a root - it never
        // requires parentIndex < ownIndex - so a naive ascending loop reading animatedWorld[parent]
        // before that slot is written would silently read C#'s default Matrix4x4 (all ZEROS, not
        // identity) for any bone whose parent has a HIGHER array index, collapsing that entire
        // sub-branch to the origin. That failure shape - part of the mesh smeared/collapsed toward
        // one point while the rest looks fine - matches exactly what's been reported ("comme si on
        // avait tiré tous les sommets"), so this is computed on demand with memoization instead,
        // correct regardless of on-disk bone order.
        var animatedWorld = new Matrix4x4[numBones];
        var computed = new bool[numBones];

        Matrix4x4 ComputeWorld(int b, int depth)
        {
            if (computed[b]) return animatedWorld[b];
            // Cycle guard: MobySkeletonReader.Validate already rejects cyclic skeletons at load
            // time, but this stays defensive rather than stack-overflowing if that's ever wrong.
            if (depth > numBones)
            {
                computed[b] = true;
                return animatedWorld[b] = Matrix4x4.Identity;
            }

            var local = Matrix4x4.CreateScale(localScale[b])
                        * Matrix4x4.CreateFromQuaternion(localRotation[b])
                        * Matrix4x4.CreateTranslation(localTranslation[b]);

            int parent = skeleton.Bones[b].ParentIndex;
            var world = (parent >= 0 && parent < numBones && parent != b)
                ? local * ComputeWorld(parent, depth + 1)
                : local;

            computed[b] = true;
            return animatedWorld[b] = world;
        }

        for (int b = 0; b < numBones; b++)
        {
            var world = ComputeWorld(b, 0);
            skin[b] = skeleton.Bones[b].InverseBindPose * world;
        }

        LastAnimatedWorld = animatedWorld;
        return skin;
    }

    private static Vector3 LerpQuantized3(short[] a, short[] b, byte mask, int bone, float frac, float scale, Vector3 fallback)
    {
        Vector3 result = fallback;
        for (int c = 0; c < 3; c++)
        {
            if ((mask & (1 << c)) == 0) continue;
            float qa = a[bone * 3 + c];
            float qb = b[bone * 3 + c];
            float q = qa + (qb - qa) * frac;
            switch (c)
            {
                case 0: result.X = q * scale; break;
                case 1: result.Y = q * scale; break;
                case 2: result.Z = q * scale; break;
            }
        }
        return result;
    }

    /// <summary>Bone b's LOCAL (parent-relative) bind-pose translation/rotation/scale, derived from
    /// the already-composed WorldBindPose/InverseBindPose (moby-space per bone, not parent-relative -
    /// see IBone remarks): <c>local = WorldBindPose[b] * InverseBindPose[parent]</c> for a
    /// non-root bone (row-vector: strip the parent's world transform), or WorldBindPose[b] itself
    /// for the root. This is what an un-animated bone's channel falls back to, so it stays exactly
    /// at its bind position/orientation relative to its (possibly animated) parent instead of
    /// snapping to the moby's origin.</summary>
    private static (Vector3 translation, Quaternion rotation, Vector3 scale) DecomposeLocalBind(ISkeleton skeleton, int boneIndex)
    {
        var bone = skeleton.Bones[boneIndex];
        Matrix4x4 local = bone.ParentIndex >= 0
            ? bone.WorldBindPose * skeleton.Bones[bone.ParentIndex].InverseBindPose
            : bone.WorldBindPose;

        return DecomposeRobust(local);
    }

    /// <summary>Row-vector-length decomposition instead of <see cref="Matrix4x4.Decompose"/> -
    /// deliberately, not a style choice. <c>local</c> here is the PRODUCT of two bind matrices
    /// (WorldBindPose * InverseBindPose(parent)), and multiplying two matrices that each carry
    /// non-uniform scale on differently-oriented axes (routine for a real character rig - bones are
    /// essentially never parent-axis-aligned) produces genuine SHEAR in the result, not just
    /// rounding noise. .NET's Matrix4x4.Decompose targets pure TRS input and can return a
    /// technically-"successful" (true) but wildly wrong scale/rotation on a sheared matrix instead
    /// of failing loudly - this was very likely the dominant cause of the reported "limbs stretched
    /// in every direction / whole model turns huge" symptom, since this fallback runs for every
    /// bone a clip doesn't explicitly track (the majority, for almost any real clip). Extracting
    /// scale as each basis row's length and rotation from the resulting normalized (shear-free)
    /// rotation matrix can't remove the shear either - no method can - but it can't blow up into a
    /// huge/negative value the way Decompose could; same technique ReChimera's own
    /// extract_bind_rotation/decompose_skeleton_ref_pose uses for exactly this reason.</summary>
    private static (Vector3 translation, Quaternion rotation, Vector3 scale) DecomposeRobust(Matrix4x4 local)
    {
        Vector3 translation = local.Translation;

        Vector3 rowX = new(local.M11, local.M12, local.M13);
        Vector3 rowY = new(local.M21, local.M22, local.M23);
        Vector3 rowZ = new(local.M31, local.M32, local.M33);

        float sx = rowX.Length(), sy = rowY.Length(), sz = rowZ.Length();
        Vector3 scale = new(
            sx > 1e-8f ? sx : 1f,
            sy > 1e-8f ? sy : 1f,
            sz > 1e-8f ? sz : 1f);

        Quaternion rotation = Quaternion.Identity;
        if (sx > 1e-8f && sy > 1e-8f && sz > 1e-8f)
        {
            rowX /= sx; rowY /= sy; rowZ /= sz;
            var rot = new Matrix4x4(
                rowX.X, rowX.Y, rowX.Z, 0f,
                rowY.X, rowY.Y, rowY.Z, 0f,
                rowZ.X, rowZ.Y, rowZ.Z, 0f,
                0f, 0f, 0f, 1f);
            rotation = Quaternion.CreateFromRotationMatrix(rot);
        }

        return (translation, rotation, scale);
    }
}
