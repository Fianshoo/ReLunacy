using ReLunacy.Engine.Loading.IO;
using ReLunacy.Engine.Loading.Objects;
using ReLunacy.Engine.Loading.Readers;

namespace ReLunacy.Engine.Assets.Animations;

/// <summary>One old-engine (Tools of Destruction) animation clip - the decoded-on-demand header +
/// control block, plus a live reference to the level's main.dat stream so
/// <see cref="AnimationPlayer"/> can read individual frames while playing without the whole clip
/// (up to 8001 frames) ever being materialized in memory at once. See
/// <see cref="Readers.AnimationReader"/> for the format proof.</summary>
public sealed class AnimationClip
{
    public AnimationMetadataOld Header { get; }
    public AnimationControlOld Control { get; }

    public string Name => Header.Name;
    public int NumFrames => Header.numFrames;
    public int NumBones => Header.numBones;
    public float FrameRate => Header.frameRate;
    public bool Looping => Header.IsLooping;
    public bool Additive => Header.IsAdditive;
    public int Num16BitTracks => Header.num16BitTracks;
    public int Num8BitTracks => Header.num8BitTracks;

    /// <summary>Duration in seconds, 0 if frameRate is non-positive (a handful of clips read 0 fps
    /// on disk - treated as "cannot play", not a divide-by-zero.</summary>
    public float DurationSeconds => Header.frameRate > 0f ? Header.numFrames / Header.frameRate : 0f;

    private readonly StreamHelper _stream;

    public AnimationClip(StreamHelper mainStream, AnimationMetadataOld header, AnimationControlOld control)
    {
        _stream = mainStream;
        Header = header;
        Control = control;
    }

    /// <summary>Reads one frame's raw track values directly off the level's main.dat stream. Not
    /// cached: called by AnimationPlayer for whichever two frames are currently being interpolated
    /// between, once per Update, which is cheap relative to a per-frame CPU skin pass.</summary>
    public void ReadFrame(int frameIndex, out short[] track16, out sbyte[] track8) =>
        AnimationReader.ReadFrameTracks(_stream, Header, frameIndex, out track16, out track8);
}
