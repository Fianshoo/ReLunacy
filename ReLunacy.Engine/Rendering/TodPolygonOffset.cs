namespace ReLunacy.Engine.Rendering;

/// <summary>
/// The non-opaque polygon offset of Tools of Destruction, as its EBOOT computes it.
///
/// NPUA80965 0x00537090 (TOC 0x008BB068) takes one float x and writes the RSX pair
/// SET_POLYGON_OFFSET_SCALE_FACTOR (0x0A78, glPolygonOffset's factor) / _BIAS (0x0A7C, its units):
///
///     t      = clamp(x * 0.01, 0, 1)        0.01 at 0x008C2B98, bounds 0 / 1 at 0x008C2B9C / 0x008C2BA0
///     factor = t * 0.96875 - 1              0.96875 at 0x008C2BA8
///     units  = 2 * (t * 124 - 128)          124 / 128 at 0x008C2BAC / 0x008C2BB0
///
/// so the factor runs from -1 (x = 0) to -0.03125 (x >= 100) and the units from -256 to -8.
/// It is called by the Moby draw handlers of passes 22, 31, 59 (0x00560760) and 95 (0x00562730),
/// only when the frag's mode byte (+0x25) is 1 or 2, with x = the float at +0x10 of the object each
/// draw-list entry points to. What that float is, per object, is not reversed yet.
///
/// The editor's old hard-coded pair (-87 units, -0.33972 factor, read from a RenderDoc capture) is
/// this function at x = 68.158: <see cref="CapturedParameter"/> keeps the rendering unchanged.
///
/// The new engine (QForce, All 4 One) has no such function. Its EBOOTs write the same registers in
/// three places: a constant factor -15 / units -0.0001 behind a render-state bit (QForce 0x006E7CDC,
/// All 4 One 0x0068263C; ToD has it too, 0x006120CC), and two shadow-map biases (per light: factor
/// byte / 32, units 2 x byte; per cascade: a lerp over a distance). Which new-engine draws use the
/// first is still open, so new-engine levels keep the ToD value for now.
/// </summary>
public static class TodPolygonOffset
{
    /// <summary>The x that reproduces the RenderDoc capture the editor was tuned on.</summary>
    public const float CapturedParameter = 68.158f;

    /// <summary>The (factor, units) pair the game sends for parameter <paramref name="x"/>.</summary>
    public static (float Factor, float Units) Compute(float x)
    {
        float t = Math.Clamp(x * 0.01f, 0f, 1f);
        return (t * 0.96875f - 1f, 2f * (t * 124f - 128f));
    }
}
