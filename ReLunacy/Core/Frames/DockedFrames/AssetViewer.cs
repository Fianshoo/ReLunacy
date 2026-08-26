using System.Numerics;
using Rectangle = System.Drawing.Rectangle;
using Point = System.Drawing.Point;
using Bliss.CSharp.Camera.Dim3;
using Bliss.CSharp.Geometry.Meshes;
using Bliss.CSharp.Geometry.Meshes.Data;
using Bliss.CSharp.Geometry.Models;
using Bliss.CSharp.Graphics.Rendering.Renderers;
using Bliss.CSharp.Graphics.Rendering.Renderers.Forward;
using Bliss.CSharp.Graphics.VertexTypes;
using Bliss.CSharp.Interact;
using Bliss.CSharp.Interact.Mice;
using Bliss.CSharp.Materials;
using Bliss.CSharp.Textures;
using Bliss.CSharp.Transformations;
using ReLunacy.Core.Frames.Modals;
using ReLunacy.Core.Selection;
using ReLunacy.Engine.Assets.Animations;
using ReLunacy.Engine.Assets.Interfaces;
using ReLunacy.Engine.Assets.Mobys;
using ReLunacy.Engine.Assets.Ties;
using ReLunacy.Engine.Export;
using ReLunacy.Engine.Rendering;
using ReLunacy.Engine.Scene;
using ReLunacy.Utility;
using ReLunacy.Utility.Localization;
using Veldrith;
using IMesh = ReLunacy.Engine.Assets.Interfaces.IMesh;

namespace ReLunacy.Core.Frames.DockedFrames;

public record struct MobyAsset
{
    public MobyAsset(Model[] mobyModel, Moby moby)
    {
        Moby = moby;
        Model = mobyModel;
        RenderModelMap = new bool[Model.Length];
        MobyName = moby.Name ?? moby.Id.ToString("X");
        for (int i = 0; i < Model.Length; i++)
        {
            RenderModelMap[i] = true;
            foreach (var mesh in Model[i].Meshes)
                verticesCount += mesh.VertexCount;
        }
    }

    public Model[] Model;
    public bool[] RenderModelMap;
    public Moby Moby;
    public string MobyName;
    public uint verticesCount;
}

public record struct TieAsset
{
    public TieAsset(Model tieModel, Tie tie)
    {
        Tie = tie;
        Model = tieModel;
        TieName = tie.Name ?? tie.Id.ToString("X");
        foreach (var mesh in Model.Meshes)
            verticesCount += mesh.VertexCount;
    }

    public Model Model;
    public Tie Tie;
    public string TieName;
    public uint verticesCount;
}

/// <summary>Terrain fragment, listed alongside Mobys and Ties even though it is not an "asset" in
/// the same sense — UFrags are not instanced, so each one IS its own single placement.
///
/// That is exactly why they belong here: a UFrag's bake is unambiguous. A Tie's lightmap depends on
/// which instance you are looking at (one Tie asset, many placements, a different bake index each),
/// so there is no context-free answer to "what does this asset's lightmap look like"; for a UFrag
/// there is. It is currently the only asset type whose baked lighting can be inspected on its own.
///
/// Holds no Bliss Model of its own: the preview reuses the scene EntityUFrag's already-built mesh
/// (see ResolveUFragMesh), so what is previewed is byte-identical to what the 3D view draws,
/// lightmap material and all, with no second copy to keep in sync or dispose.</summary>
public record struct UFragAsset
{
    public UFragAsset(ulong zoneId, int index, IUFrag ufrag)
    {
        ZoneId = zoneId;
        Index = index;
        UFrag = ufrag;
        var positions = ufrag.GetVertexPositions();
        verticesCount = (uint)(positions.Length / 3);
        triangleCount = (uint)(ufrag.GetIndices().Length / 3);

        // Measured off the geometry, NOT from GetBoundingRadius(): old-engine UFrags don't have a
        // decodable radius in their record (boundingSphere.W at 0x6C reads NaN for all 1987 UFrags in
        // metropolis, which is why ZoneReader substitutes a flat 2.5f). A constant is useless for
        // framing a preview, and these are raw fixed-point x256 units, so both values stay in that
        // space and get descaled with the rest of the transform.
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        for (int i = 0; i + 2 < positions.Length; i += 3)
        {
            var p = new Vector3(positions[i], positions[i + 1], positions[i + 2]);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        localCentre = verticesCount > 0 ? (min + max) * 0.5f : Vector3.Zero;
        localRadius = verticesCount > 0 ? (max - min).Length() * 0.5f : 0f;
        ushort lm = ufrag.LightmapIndex;
        UFragName = $"UFrag {index} (lm {(lm == ReLunacy.Engine.Loading.Objects.UFragMetadata.NoLightmap ? "-" : lm.ToString())})";
    }

    public ulong ZoneId;
    public int Index;
    public IUFrag UFrag;
    public string UFragName;
    public uint verticesCount;
    public uint triangleCount;
    /// <summary>Geometric centre and radius in RAW fixed-point x256 units — divide by 256 for world units.</summary>
    public Vector3 localCentre;
    public float localRadius;

    public readonly bool HasLightmap => UFrag.LightmapIndex != ReLunacy.Engine.Loading.Objects.UFragMetadata.NoLightmap;
}

public class AssetViewer : DockedFrame, ILevelListener
{
    protected override ImGuiCond DockingConditions { get; set; } = ImGuiCond.Appearing;
    protected override Vector2 DefaultPosition { get; set; } = ImGui.GetWorkCenter(ImGui.GetMainViewport());
    protected override ImGuiWindowFlags WindowFlags { get; set; } = ImGuiWindowFlags.NoScrollbar;

    public Rectangle RenderFrameSize { get; private set; }
    public Vector2 RenderFramePos { get; private set; }
    public Vector2 MousePos { get; private set; }
    public MouseGrabHandler rmbghandler = new() { mouseButton = Bliss.CSharp.Interact.Mice.MouseButton.Right };
    public MouseGrabHandler mmbghandler = new() { mouseButton = Bliss.CSharp.Interact.Mice.MouseButton.Middle };
    private readonly GraphicsDevice graphicsDevice;
    private RenderTexture2D renderTexture;
    private readonly DecalAwareForwardRenderer renderer;
    private readonly ImmediateRenderer immediateRenderer;
    private readonly PickingRenderer pickingRenderer;
    public readonly CommandList commandList;
    public readonly Cam3D Camera;
    private Renderable? cubeRenderable;
    private bool showSkeleton = true;

    private bool pickRequested;

    // Picking granularity for this viewport only (never fed into the shared scene-picking used
    // by View3D) — reuses local (bangleIndex, meshIndex) as the picking ID directly instead of
    // minting a globally-unique ID per mesh, since only one asset is ever previewed here at a
    // time. bangleIndex is always 0 for Ties (no bangle concept).
    private (int bangleIndex, int meshIndex)? selectedMesh;
    private int selectedVertexIndex;
    private bool vertexEditMode;

    // Screen-space pixel radii for the vertex-edit-mode overlay/picking — kept generous on the
    // pick radius specifically per the ask that vertex selection be tolerant, since a raw vertex
    // dot is a much smaller target than a mesh triangle.
    private const float VertexPointPixelRadius = 4f;
    private const float SelectedVertexPixelRadius = 7f;
    private const float VertexPickPixelRadius = 10f;

    // ImmediateRenderer's DrawBillboard always uses white-source * this to produce, for any
    // background pixel color C, a final color of (1,1,1) - C — i.e. the dot always reads as the
    // inverse of whatever's behind it, so it stays visible regardless of the underlying texture
    // (this is the whole reason for this blend state instead of a fixed dot color). Alpha is left
    // untouched (dest kept as-is) since only the color channels need inverting.
    private static readonly BlendStateDescription InvertBlendState = new(
        RgbaFloat.WHITE,
        new BlendAttachmentDescription(
            blendEnabled: true,
            sourceColorFactor: BlendFactor.InverseDestinationColor,
            destinationColorFactor: BlendFactor.Zero,
            colorFunction: BlendFunction.Add,
            sourceAlphaFactor: BlendFactor.Zero,
            destinationAlphaFactor: BlendFactor.One,
            alphaFunction: BlendFunction.Add));

    // Persisted, user-draggable pane sizes (pixels) — each tracks the pane immediately BEFORE its
    // splitter; the trailing pane on the other side of a splitter always just takes whatever
    // GetContentRegionAvail() leaves over, so only one size needs to be stored per split.
    private float treeListWidth = 260f;
    private float previewHeight = 300f;
    private float assetInfoWidth = 320f;
    private const float SplitterThickness = 6f;

    private List<Renderable> cachedRenderables = [];
    public List<MobyAsset> mobyAssets = [];
    public List<TieAsset> tieAssets = [];
    public List<UFragAsset> ufragAssets = [];

    // UFrag-tab state. The lightmapped/not split is the first question worth asking of any UFrag and
    // eyeballing "lm -" across ~2000 rows doesn't scale, so it gets its own filter rather than
    // reusing the Used/Unused one above — that one is meaningless here, since a UFrag is its own
    // single placement and is therefore always "used".
    private bool? ufragLightmapFilter;
    private bool ufragShowUVOverlay = true;
    private bool ufragShowUVWireframe = true;
    private bool ufragShowFloatInterpretation;
    private bool isDirty = true;
    public bool IsDirty
    {
        get => isDirty;
        set => isDirty = value;
    }

    private MobyAsset? selectedMobyAsset;
    public MobyAsset? SelectedMobyAsset
    {
        get => selectedMobyAsset;
        set
        {
            selectedMobyAsset = value;
            if (value != null) selectedTieAsset = null;
            selectedMesh = null;
            exportNameOverride = "";
            IsDirty = true;
            RebuildSelectedAssetMaterials();
            _animationPlayer.SetClip(null);
            _animationClipSearch = "";
            DisposeSkinnedMeshes();
        }
    }

    private void DisposeSkinnedMeshes()
    {
        foreach (var (mesh, _) in _skinnedMeshes) mesh.Dispose();
        _skinnedMeshes.Clear();
    }

    // Animation playback state for the currently selected Moby's preview. One player is reused
    // across selections (SetClip(null) above resets it whenever the selection changes) rather than
    // recreated, since it carries no per-clip unmanaged resources.
    private readonly ReLunacy.Engine.Assets.Animations.AnimationPlayer _animationPlayer = new();
    private string _animationClipSearch = "";
    private bool _animationDebugExpanded;

    // Rebuilt every frame while a clip is assigned and not sitting exactly at the bind pose (see
    // Render) — CPU-skinned Vertex3D meshes, one per (bangle, mesh) pair of the selected Moby, kept
    // separate from cachedRenderables' static path so a plain (non-animated) Moby preview never pays
    // for this. Disposed and cleared whenever the clip is cleared or the selection changes.
    private readonly List<(Mesh<Vertex3D> mesh, BasicMeshData data)> _skinnedMeshes = [];

    private TieAsset? selectedTieAsset;
    public TieAsset? SelectedTieAsset
    {
        get => selectedTieAsset;
        set
        {
            selectedTieAsset = value;
            if (value != null) { selectedMobyAsset = null; selectedUFragAsset = null; }
            selectedMesh = null;
            exportNameOverride = "";
            IsDirty = true;
            RebuildSelectedAssetMaterials();
        }
    }

    private UFragAsset? selectedUFragAsset;
    public UFragAsset? SelectedUFragAsset
    {
        get => selectedUFragAsset;
        set
        {
            selectedUFragAsset = value;
            if (value != null) { selectedMobyAsset = null; selectedTieAsset = null; }
            selectedMesh = null;
            exportNameOverride = "";
            IsDirty = true;
            RebuildSelectedAssetMaterials();
            if (value != null) FrameUFragInPreview(value.Value);
        }
    }

    // Lets the user rename an asset for export (textures/.bin/.gltf all take this name too — see
    // ExportModel/GetExportName) instead of being stuck with the asset's raw internal name, which
    // is routinely something like a full "levels/.../foo.entity.irb" path — not exactly what you
    // want a Models Resource submission's files named after. Reset to blank (falls back to the
    // asset's own default name) whenever the selection changes, above.
    private string exportNameOverride = "";

    private AssetManager? assetManager;
    private string assetSearch = "";

    private enum UsageFilter { All, Used, Unused }
    private UsageFilter assetUsageFilter = UsageFilter.All;

    // "Used" = has at least one placed instance in the currently loaded level (same definition
    // "Find usages" below already uses) — recomputed once per TransmitAssets call rather than
    // walking EntityManager.AllEntities() on every frame for every asset in the list.
    private HashSet<ulong> usedMobyIds = [];
    private HashSet<ulong> usedTieIds = [];

    // Moby materials are grouped per bangle (a material used by several bangles shows up under
    // each) since bangles are independently toggleable — seeing which bangle actually pulls in a
    // material matters. Ties have no bangles, so their materials are just a flat deduped list.
    private readonly List<(int bangleIndex, List<IMaterial> materials)> selectedMobyMaterialsByBangle = [];
    private readonly List<IMaterial> selectedTieMaterials = [];

    // Placed instances of the currently selected asset found in the loaded level, populated on
    // demand by the "Find usages" button (mirrors TexturesExplorer's usage lookup) — cleared
    // whenever the selection changes so a stale result list from a previous asset can't linger.
    private List<EntityMoby>? mobyUsageResults;
    private List<EntityTie>? tieUsageResults;

    public AssetViewer(GraphicsDevice gd)
    {
        FrameName = LM.Get("GUI_Frame_AssetViewer");
        graphicsDevice = gd;
        commandList = gd.ResourceFactory.CreateCommandList();
        Camera = new Cam3D(
            gd,
            new Vector3(0, 0, -10),
            Vector3.Zero,
            1f,
            Vector3.UnitY,
            ProjectionType.Perspective,
            // Custom, not Orbital: Orbital drives its own scroll-to-zoom internally with no
            // notion of ImGui window/hover boundaries, which is why scrolling used to zoom this
            // camera no matter where the cursor was. Zoom is handled manually in Tick() instead,
            // gated on hovering the render image — same pattern View3D's camera already uses.
            CameraMode.Custom,
            Program.Settings.CamFOV,
            0.001f,
            100f);
        renderTexture = new RenderTexture2D(gd, 300u, 300u, true, (TextureSampleCount)Program.Settings.MSAA_Level);
        renderer = new DecalAwareForwardRenderer(gd);
        immediateRenderer = new ImmediateRenderer(gd);
        pickingRenderer = new PickingRenderer(gd);
    }

    /// <summary>Drops every reference to the level that's about to be unloaded — mobyAssets/
    /// tieAssets wrap AssetManager-owned Models that are about to be disposed, and the selected-
    /// asset/usage-result state references entities from the same level.</summary>
    public void OnLevelUnloading()
    {
        selectedMobyAsset = null;
        selectedTieAsset = null;
        selectedUFragAsset = null;
        selectedMesh = null;
        RebuildSelectedAssetMaterials();
        _animationPlayer.SetClip(null);
        DisposeSkinnedMeshes();
        mobyAssets.Clear();
        tieAssets.Clear();
        // UFragAsset holds an IUFrag owned by the level being torn down, and the preview borrows the
        // scene entity's mesh — both die with the level, so the list must not outlive it.
        ufragAssets.Clear();
        usedMobyIds.Clear();
        usedTieIds.Clear();
        cachedRenderables.Clear();
        assetManager = null;
        IsDirty = true;
    }

    public void OnLevelLoaded()
    {
        var window = LunaWindow.Instance;
        if (window.AssetManager != null && window.Level != null)
            TransmitAssets(window.AssetManager, window.Level.Mobys, window.Level.Ties);
    }

    public void TransmitAssets(AssetManager assetManager, IReadOnlyDictionary<ulong, Moby> mobys, IReadOnlyDictionary<ulong, Tie>? ties = null)
    {
        this.assetManager = assetManager;
        mobyAssets.Clear();
        foreach (var (tuid, mobyModel) in assetManager.Mobys)
        {
            if (mobys.TryGetValue(tuid, out var moby))
                mobyAssets.Add(new(mobyModel, moby));
        }

        tieAssets.Clear();
        if (ties != null)
        {
            foreach (var (tuid, tieModel) in assetManager.Ties)
            {
                if (ties.TryGetValue(tuid, out var tie))
                    tieAssets.Add(new(tieModel, tie));
            }
        }

        // UFrags come off the level's zones rather than AssetManager: they are not shared assets
        // keyed by TUID like Mobys/Ties, they belong to the zone that parsed them, and the (zone,
        // index) pair is the only stable way to name one.
        ufragAssets.Clear();
        var level = LunaWindow.Instance.Level;
        if (level != null)
        {
            foreach (var (zoneId, zone) in level.Zones)
            {
                for (int i = 0; i < zone.UFrags.Count; i++)
                    ufragAssets.Add(new UFragAsset(zoneId, i, zone.UFrags[i]));
            }
        }

        usedMobyIds = EntityManager.Singleton.AllEntities().OfType<EntityMoby>().Select(e => e.BaseMoby.Id).ToHashSet();
        usedTieIds = EntityManager.Singleton.AllEntities().OfType<EntityTie>().Select(e => e.BaseTie.Id).ToHashSet();
    }

    private void RebuildSelectedAssetMaterials()
    {
        selectedMobyMaterialsByBangle.Clear();
        selectedTieMaterials.Clear();
        mobyUsageResults = null;
        tieUsageResults = null;

        static void AddMaterial(HashSet<ulong> seen, List<IMaterial> into, IMaterial mat)
        {
            if (seen.Add(mat.Id))
                into.Add(mat);
        }

        if (selectedMobyAsset != null)
        {
            var bangles = selectedMobyAsset.Value.Moby.Bangles;
            for (int i = 0; i < bangles.Count; i++)
            {
                var seen = new HashSet<ulong>();
                var materials = new List<IMaterial>();
                foreach (var mesh in bangles[i].Meshes)
                    AddMaterial(seen, materials, mesh.Material);
                if (materials.Count > 0)
                    selectedMobyMaterialsByBangle.Add((i, materials));
            }
        }
        else if (selectedTieAsset != null)
        {
            var seen = new HashSet<ulong>();
            foreach (var mesh in selectedTieAsset.Value.Tie.Meshes)
                AddMaterial(seen, selectedTieMaterials, mesh.Material);
        }
        else if (selectedUFragAsset != null)
        {
            // A UFrag is one mesh with one shader, so it reuses the Tie list rather than needing its
            // own — the shader grid renders whatever is in there.
            AddMaterial(new HashSet<ulong>(), selectedTieMaterials, selectedUFragAsset.Value.UFrag.Material);
        }
    }

    private sealed class HierarchyNode<T>
    {
        public Dictionary<string, HierarchyNode<T>> Children { get; } = [];
        public List<T> Items { get; } = [];
    }

    // Builds a folder tree out of the "/"-separated path a name selector returns; the last
    // segment is the leaf's own display name, everything before it becomes nested TreeNodes.
    private static HierarchyNode<T> BuildHierarchy<T>(List<T> assets, Func<T, string> nameSelector)
    {
        var root = new HierarchyNode<T>();
        foreach (var asset in assets)
        {
            var segments = nameSelector(asset).Split('/', StringSplitOptions.RemoveEmptyEntries);
            var node = root;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                if (!node.Children.TryGetValue(segments[i], out var child))
                {
                    child = new HierarchyNode<T>();
                    node.Children[segments[i]] = child;
                }
                node = child;
            }
            node.Items.Add(asset);
        }
        return root;
    }

    private static void RenderHierarchyNode<T>(HierarchyNode<T> node, string idPrefix, Func<T, string> nameSelector, Action<T> renderLeaf)
    {
        foreach (var (name, child) in node.Children.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (ImGui.TreeNode($"{name}##hierarchy_{idPrefix}{name}"))
            {
                RenderHierarchyNode(child, $"{idPrefix}{name}/", nameSelector, renderLeaf);
                ImGui.TreePop();
            }
        }
        foreach (var item in node.Items.OrderBy(nameSelector, StringComparer.OrdinalIgnoreCase))
            renderLeaf(item);
    }

    /// <summary>Compact "All / Used / Unused" radio row shared by both the Moby and Tie tabs below
    /// — one filter for the whole asset library, same as the search box above it.</summary>
    private void RenderUsageFilterControl()
    {
        int filter = (int)assetUsageFilter;
        ImGui.RadioButton(LM.Get("GUI_Common_FilterAll"), ref filter, (int)UsageFilter.All);
        ImGui.SameLine();
        ImGui.RadioButton(LM.Get("GUI_Common_FilterUsed"), ref filter, (int)UsageFilter.Used);
        ImGui.SameLine();
        ImGui.RadioButton(LM.Get("GUI_Common_FilterUnused"), ref filter, (int)UsageFilter.Unused);
        assetUsageFilter = (UsageFilter)filter;
    }

    private IEnumerable<MobyAsset> FilteredMobyAssets() => assetUsageFilter switch
    {
        UsageFilter.Used => mobyAssets.Where(a => usedMobyIds.Contains(a.Moby.Id)),
        UsageFilter.Unused => mobyAssets.Where(a => !usedMobyIds.Contains(a.Moby.Id)),
        _ => mobyAssets,
    };

    private IEnumerable<TieAsset> FilteredTieAssets() => assetUsageFilter switch
    {
        UsageFilter.Used => tieAssets.Where(a => usedTieIds.Contains(a.Tie.Id)),
        UsageFilter.Unused => tieAssets.Where(a => !usedTieIds.Contains(a.Tie.Id)),
        _ => tieAssets,
    };

    private void RenderMobyLeaf(MobyAsset asset)
    {
        string label = asset.MobyName.Split('/')[^1];
        bool isSelected = selectedMobyAsset?.Moby.Id == asset.Moby.Id;
        bool isUsed = usedMobyIds.Contains(asset.Moby.Id);

        if (!isUsed) ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        if (ImGui.Selectable($"{label}##moby_{asset.Moby.Id:X}", isSelected))
            SelectedMobyAsset = asset;
        if (!isUsed) ImGui.PopStyleColor();
    }

    private void RenderTieLeaf(TieAsset asset)
    {
        string label = asset.TieName.Split('/')[^1];
        bool isSelected = selectedTieAsset?.Tie.Id == asset.Tie.Id;
        bool isUsed = usedTieIds.Contains(asset.Tie.Id);

        if (!isUsed) ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        if (ImGui.Selectable($"{label}##tie_{asset.Tie.Id:X}", isSelected))
            SelectedTieAsset = asset;
        if (!isUsed) ImGui.PopStyleColor();
    }

    /// <summary>Lightmapped / not, the UFrag tab's own filter. Separate from Used/Unused above, which
    /// is meaningless for terrain: a UFrag is its own single placement, so it is always "used".</summary>
    private void RenderUFragFilterControl()
    {
        int filter = ufragLightmapFilter switch { null => 0, true => 1, false => 2 };
        ImGui.RadioButton($"{LM.Get("GUI_Common_FilterAll")}##ufrag_f", ref filter, 0);
        ImGui.SameLine();
        ImGui.RadioButton(LM.Get("GUI_Frame_AssetViewer_UFragFilterLit"), ref filter, 1);
        ImGui.SameLine();
        ImGui.RadioButton(LM.Get("GUI_Frame_AssetViewer_UFragFilterUnlit"), ref filter, 2);
        ufragLightmapFilter = filter switch { 1 => true, 2 => false, _ => null };
    }

    private IEnumerable<UFragAsset> FilteredUFragAssets() => ufragLightmapFilter switch
    {
        true => ufragAssets.Where(a => a.HasLightmap),
        false => ufragAssets.Where(a => !a.HasLightmap),
        _ => ufragAssets,
    };

    private void RenderUFragLeaf(UFragAsset asset)
    {
        // Identity is (zone, index), not an asset id — UFrags aren't keyed by TUID, and index alone
        // repeats across zones.
        bool isSelected = selectedUFragAsset is { } sel && sel.ZoneId == asset.ZoneId && sel.Index == asset.Index;
        if (!asset.HasLightmap) ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        if (ImGui.Selectable($"{asset.UFragName}##ufrag_{asset.ZoneId}_{asset.Index}", isSelected))
            SelectedUFragAsset = asset;
        if (!asset.HasLightmap) ImGui.PopStyleColor();
    }

    /// <summary>Selects the moby with the given asset id, e.g. when jumping here from the Texture Explorer's "used by" list. Returns false if it isn't in the currently transmitted set.</summary>
    public bool SelectMobyById(ulong mobyId)
    {
        var match = mobyAssets.FirstOrDefault(a => a.Moby.Id == mobyId);
        if (match.Moby == null) return false;

        SelectedMobyAsset = match;
        return true;
    }

    /// <summary>Selects the tie with the given asset id, e.g. when jumping here from the Texture Explorer's "used by" list. Returns false if it isn't in the currently transmitted set.</summary>
    public bool SelectTieById(ulong tieId)
    {
        var match = tieAssets.FirstOrDefault(a => a.Tie.Id == tieId);
        if (match.Tie == null) return false;

        SelectedTieAsset = match;
        return true;
    }

    private static List<EntityMoby> FindMobyInstances(ulong mobyId) =>
        EntityManager.Singleton.AllEntities().OfType<EntityMoby>().Where(e => e.BaseMoby.Id == mobyId).ToList();

    private static List<EntityTie> FindTieInstances(ulong tieId) =>
        EntityManager.Singleton.AllEntities().OfType<EntityTie>().Where(e => e.BaseTie.Id == tieId).ToList();

    private static void SelectInstanceInView3D(Entity entity)
    {
        SelectionManager.Singleton.Select(entity);
        var v3d = LunaWindow.Instance.GetFirstFrame<View3D>();
        if (v3d != null)
        {
            v3d.SelectedEntity = entity;
            v3d.Focus();
        }
    }

    private void OpenShaderInBrowser(ulong tuid)
    {
        var browser = LunaWindow.Instance.GetFirstFrame<ShaderBrowser>();
        if (browser == null)
        {
            browser = new ShaderBrowser();
            LunaWindow.Instance.AddFrame(browser);
        }
        if (LunaWindow.Instance.Level != null)
            browser.TransmitShaders(LunaWindow.Instance.Level);
        browser.SelectShader(tuid);
        browser.Focus();
    }

    private static void RenderUsageResults<TEntity>(List<TEntity>? results, string idPrefix) where TEntity : Entity
    {
        if (results == null) return;

        if (results.Count == 0)
        {
            ImGui.TextDisabled(LM.Get("GUI_Frame_AssetViewer_NoInstancesFound"));
            return;
        }

        ImGui.Text(LM.Get("GUI_Frame_AssetViewer_Instances", results.Count));
        for (int i = 0; i < results.Count; i++)
        {
            var entity = results[i];
            var pos = entity.Transform.Translation;
            if (ImGui.Selectable($"{entity.Name} ({pos.X:0.#}, {pos.Y:0.#}, {pos.Z:0.#})##{idPrefix}_{i}"))
                SelectInstanceInView3D(entity);
        }
    }

    // Shader preview uses the material's albedo texture — same convention as the Shader Browser's
    // own texture-reference thumbnails — since a shader has no rendering of its own worth showing.
    private void RenderShaderGrid(IReadOnlyList<IMaterial> materials, string columnsId)
    {
        int columns = Math.Max(1, (int)ImGui.GetContentRegionAvail().X / 72);
        ImGui.Columns(columns, columnsId, false);
        foreach (var mat in materials)
        {
            if (mat.AlbedoTexture != null && assetManager != null && assetManager.BuiltTextures.TryGetValue(mat.AlbedoTexture.Id, out var tex2D))
            {
                var ptr = LunaWindow.Instance.imGuiController.GetOrCreateImGuiBinding(graphicsDevice.ResourceFactory, tex2D.DeviceTexture);
                ImGui.Image(ptr, new Vector2(64, 64), Vector2.UnitY, Vector2.UnitX);
                if (ImGui.IsItemClicked())
                    OpenShaderInBrowser(mat.Id);
            }
            if (ImGui.Selectable($"{mat.Name ?? mat.Id.ToString("X")}##shader_grid_{mat.Id:X}"))
                OpenShaderInBrowser(mat.Id);
            ImGui.NextColumn();
        }
        ImGui.Columns(1);
    }

    protected override void Render(double deltaTime)
    {
        Vector2 totalAvail = ImGui.GetContentRegionAvail();
        treeListWidth = Math.Clamp(treeListWidth, 150f, Math.Max(150f, totalAvail.X - 200f));

        ImGui.BeginGroup();
        if (ImGui.BeginChild("assets_explorer", new Vector2(treeListWidth, totalAvail.Y), ImGuiChildFlags.None))
        {
            if (ImGui.Button(LM.Get("GUI_Frame_AssetViewer_Unselect")))
            {
                SelectedMobyAsset = null;
                SelectedTieAsset = null;
                SelectedUFragAsset = null;
            }
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
            ImGui.InputTextWithHint("##asset_viewer_search", LM.Get("GUI_Frame_AssetViewer_SearchHint", mobyAssets.Count + tieAssets.Count + ufragAssets.Count), ref assetSearch, 128);
            RenderUsageFilterControl();

            if (ImGui.BeginTabBar(LM.Get("GUI_Frame_AssetViewer_Tab")))
            {
                if (ImGui.BeginTabItem(LM.Get("GUI_Frame_AssetViewer_MobyTab")))
                {
                    if (ImGui.BeginChild("asset_viewer_moby_tab", ImGui.GetContentRegionAvail(), ImGuiChildFlags.Borders, ImGuiWindowFlags.AlwaysVerticalScrollbar))
                    {
                        var filtered = FilteredMobyAssets().ToList();
                        if (string.IsNullOrWhiteSpace(assetSearch))
                        {
                            RenderHierarchyNode(BuildHierarchy(filtered, a => a.MobyName), "", a => a.MobyName, RenderMobyLeaf);
                        }
                        else
                        {
                            foreach (var moby in filtered)
                            {
                                if (moby.MobyName.Contains(assetSearch, StringComparison.OrdinalIgnoreCase))
                                    RenderMobyLeaf(moby);
                            }
                        }
                    }
                    ImGui.EndChild();

                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem(LM.Get("GUI_Frame_AssetViewer_TieTab")))
                {
                    if (ImGui.BeginChild("asset_viewer_tie_tab", ImGui.GetContentRegionAvail(), ImGuiChildFlags.Borders, ImGuiWindowFlags.AlwaysVerticalScrollbar))
                    {
                        var filtered = FilteredTieAssets().ToList();
                        if (string.IsNullOrWhiteSpace(assetSearch))
                        {
                            RenderHierarchyNode(BuildHierarchy(filtered, a => a.TieName), "", a => a.TieName, RenderTieLeaf);
                        }
                        else
                        {
                            foreach (var tie in filtered)
                            {
                                if (tie.TieName.Contains(assetSearch, StringComparison.OrdinalIgnoreCase))
                                    RenderTieLeaf(tie);
                            }
                        }
                    }
                    ImGui.EndChild();

                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem(LM.Get("GUI_Frame_AssetViewer_UFragTab")))
                {
                    RenderUFragFilterControl();
                    if (ImGui.BeginChild("asset_viewer_ufrag_tab", ImGui.GetContentRegionAvail(), ImGuiChildFlags.Borders, ImGuiWindowFlags.AlwaysVerticalScrollbar))
                    {
                        // Flat list, no BuildHierarchy: UFrag names are synthesized ("UFrag 12 (lm
                        // 780)"), not "/"-separated asset paths, so there is no folder tree to build.
                        foreach (var ufrag in FilteredUFragAssets())
                        {
                            if (!string.IsNullOrWhiteSpace(assetSearch) &&
                                !ufrag.UFragName.Contains(assetSearch, StringComparison.OrdinalIgnoreCase))
                                continue;
                            RenderUFragLeaf(ufrag);
                        }
                    }
                    ImGui.EndChild();

                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem(LM.Get("GUI_Frame_AssetViewer_FoliageTab")))
                {
                    if (ImGui.BeginChild("asset_viewer_foliage_tab", ImGui.GetContentRegionAvail(), ImGuiChildFlags.Borders, ImGuiWindowFlags.AlwaysVerticalScrollbar))
                    {
                        RenderFoliageList();
                    }
                    ImGui.EndChild();

                    ImGui.EndTabItem();
                }


                ImGui.EndTabBar();
            }
        }
        ImGui.EndChild();
        ImGui.EndGroup();

        VerticalSplitter("##split_tree", ref treeListWidth, totalAvail.Y);

        ImGui.BeginGroup();
        float rightWidth = ImGui.GetContentRegionAvail().X;
        previewHeight = Math.Clamp(previewHeight, 100f, Math.Max(100f, totalAvail.Y - 150f));
        if (ImGui.BeginChild("asset_view", new Vector2(rightWidth, previewHeight), ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar))
        {
            UpdateWindowSize();
            Tick(deltaTime);

            commandList.Begin();
            commandList.SetFramebuffer(renderTexture.Framebuffer);
            commandList.ClearColorTarget(0, Bliss.CSharp.Colors.Color.LightBlue.ToRgbaFloat());
            commandList.ClearDepthStencil(1f);

            Camera.Begin(commandList);
            Camera.Update(deltaTime);
            // Depth test disabled: the skeleton overlay (see DrawSkeleton below) should always
            // read on top of the mesh, not get hidden behind it when bones sit inside the model.
            immediateRenderer.Begin(commandList, renderTexture.Framebuffer.OutputDescription, depthStencilState: DepthStencilStateDescription.DISABLED);

            if (selectedMobyAsset == null && selectedTieAsset == null && selectedUFragAsset == null)
            {
                if (IsDirty || cubeRenderable is null)
                {
                    var cube = Primitives.CreateCube(graphicsDevice, new Material(GlobalResource.DefaultModelEffect));
                    cube.Material.AddMaterialMap(new MaterialMapKey(MaterialMapType.Albedo), 0, new MaterialMap(GlobalResource.DefaultModelTexture, color: Bliss.CSharp.Colors.Color.White));

                    cubeRenderable = new Renderable(cube, new Transform
                    {
                        Rotation = Quaternion.Identity,
                        Scale = Vector3.One,
                        Translation = Vector3.Zero
                    });
                }
                renderer.DrawRenderable(cubeRenderable!);
                renderer.Draw(commandList, renderTexture.Framebuffer.OutputDescription);
            }
            else
            {
                // Animation playback: advance time and, while a clip is assigned, re-skin this
                // Moby's preview meshes every frame regardless of IsDirty (the pose changes every
                // frame even though nothing about the SELECTION changed) - UpdateSkinnedVertices
                // owns cachedRenderables entirely for this case, including (re)building it when
                // stale. Stopped/no-clip falls through to the plain static Model path below exactly
                // as before - zero overhead and zero behaviour change for the common case.
                bool animatingMoby = selectedMobyAsset != null && _animationPlayer.Clip != null
                    && selectedMobyAsset.Value.Moby.Skeleton != null;
                if (selectedMobyAsset != null && selectedMobyAsset.Value.Moby.Skeleton != null)
                    _animationPlayer.Update((float)deltaTime);
                if (animatingMoby)
                {
                    UpdateSkinnedVertices(selectedMobyAsset!.Value, selectedMobyAsset.Value.Moby.Skeleton!, forceRebuild: IsDirty);
                    IsDirty = false;
                }

                if (IsDirty)
                {
                    cachedRenderables.Clear();
                    if (selectedMobyAsset != null)
                    {
                        var models = selectedMobyAsset.Value.Model;
                        var renderMap = selectedMobyAsset.Value.RenderModelMap;
                        for (int i = 0; i < models.Length; i++)
                        {
                            if (!renderMap[i]) continue;
                            foreach (var mesh in models[i].Meshes)
                                cachedRenderables.Add(new Renderable(mesh, new Transform { Rotation = Quaternion.Identity, Scale = Vector3.One, Translation = Vector3.Zero }));
                        }
                    }
                    else if (selectedTieAsset != null)
                    {
                        foreach (var mesh in selectedTieAsset.Value.Model.Meshes)
                            cachedRenderables.Add(new Renderable(mesh, new Transform { Rotation = Quaternion.Identity, Scale = Vector3.One, Translation = Vector3.Zero }));
                    }
                    else if (selectedUFragAsset != null && ResolveUFragMesh(selectedUFragAsset.Value) is { } ufragMesh)
                    {
                        // Scale matches EntityUFrag exactly (raw positions are fixed-point x256 on both
                        // engines) rather than being normalised per UFrag to fit the viewport. A
                        // per-selection scale would silently change the apparent lighting from one
                        // UFrag to the next - specular and the normal-map derivatives are not
                        // scale-invariant - and comparing bakes across UFrags is what this tab is for.
                        // The camera moves instead; see FrameUFragInPreview.
                        cachedRenderables.Add(new Renderable(ufragMesh, new Transform
                        {
                            Rotation = Quaternion.Identity,
                            Scale = Vector3.One / 256f,
                            Translation = -selectedUFragAsset.Value.localCentre / 256f,
                        }));
                    }

                    IsDirty = false;
                    LunaLog.LogDebug($"Updated {cachedRenderables.Count} renderables");
                }

                // The mesh materials are the shared AssetManager's, so with lighting enabled they use
                // the lit effect, which declares the environment cubemap at set 10. This preview has
                // its OWN renderer, distinct from View3D's, so it must bind the cube too — otherwise
                // that descriptor set is left unbound and the GPU faults (segfault) the moment a lit
                // mesh draws. AssetManager always provides a view (a 1x1 fallback when the level has
                // no cubemap). See DecalAwareForwardRenderer / AssetManager.BuildLitModelEffect.
                renderer.EnvironmentCubemap = assetManager?.EnvironmentCubemapView;

                foreach (var renderable in cachedRenderables)
                    renderer.DrawRenderable(renderable);
                renderer.Draw(commandList, renderTexture.Framebuffer.OutputDescription);

                if (showSkeleton && selectedMobyAsset?.Moby.Skeleton is { } skeleton)
                    DrawSkeleton(skeleton, immediateRenderer, animatingMoby ? _animationPlayer.LastAnimatedWorld : null);

                if (vertexEditMode && ResolveSelectedMesh() is { } selectedMeshForOverlay)
                    DrawVertexOverlay(selectedMeshForOverlay);
            }

            if (pickRequested)
            {
                if (vertexEditMode && selectedMesh != null)
                    PickVertexUnderCursor();
                else
                    PickMeshUnderCursor();
            }
            pickRequested = false;

            immediateRenderer.End();
            Camera.End();

            commandList.End();
            graphicsDevice.SubmitCommands(commandList);
            ImGui.Image(
                LunaWindow.Instance.imGuiController.GetOrCreateImGuiBinding(graphicsDevice.ResourceFactory, renderTexture.ColorTexture),
                RenderFrameSize.GetSizeF(),
                Vector2.Zero,
                Vector2.One
            );
        }
        ImGui.EndChild();

        HorizontalSplitter("##split_preview", ref previewHeight, rightWidth);

        // Distance to Target (the orbit pivot), not Camera.Position.Length() (distance to world
        // zero) — those were the same thing before middle-click pan could move Target away from
        // Vector3.Zero, but "distance to origin" now means "distance to wherever the pivot is."
        ImGui.Text($"{RenderFrameSize.Width}x{RenderFrameSize.Height} - Distance to target: {Vector3.Distance(Camera.Position, Camera.Target)}m");
        ImGui.Separator();

        // Lower part split vertically: asset info/shaders/export on the left (unchanged content),
        // selected-mesh inspector (from GPU picking in the preview above) on the right.
        Vector2 lowerAvail = ImGui.GetContentRegionAvail();
        assetInfoWidth = Math.Clamp(assetInfoWidth, 150f, Math.Max(150f, lowerAvail.X - 150f));
        if (ImGui.BeginChild("asset_lower_left", new Vector2(assetInfoWidth, lowerAvail.Y), ImGuiChildFlags.None))
        {
        ImGui.Text("Asset");

        if (selectedMobyAsset != null)
        {
            var moby = selectedMobyAsset.Value.Moby;
            string mobyDefaultName = moby.Name ?? $"Moby_{moby.Id:X}";

            ImGui.Separator();
            ImGui.SetNextItemWidth(200);
            ImGui.InputTextWithHint("##export_name_moby", LM.Get("GUI_Frame_AssetViewer_ExportNameHint", mobyDefaultName), ref exportNameOverride, 128);
            ImGui.SameLine();
            ImGuiPlus.HelpMarker(LM.Get("GUI_Frame_AssetViewer_ExportNameHelp"));
            if (ImGui.Button(LM.Get("GUI_Frame_AssetViewer_ExportGltf")))
                ExportModel(GltfExporter.Export, "glb", GetExportName(mobyDefaultName), GetMobyGroups(moby), moby.Skeleton);
            ImGui.SameLine();
            if (ImGui.Button(LM.Get("GUI_Frame_AssetViewer_ExportGltfSeparate")))
                ExportModel(GltfExporter.ExportGltfSeparate, "gltf", GetExportName(mobyDefaultName), GetMobyGroups(moby), moby.Skeleton, ownFolder: true);
            ImGui.SameLine();
            if (ImGui.Button(LM.Get("GUI_Frame_AssetViewer_ExportObj")))
                ExportModel(ObjExporter.Export, "obj", GetExportName(mobyDefaultName), GetMobyGroups(moby), moby.Skeleton);

            ImGui.BeginGroup();
            ImGui.Text("Id");
            ImGui.Text("Name");
            ImGui.Text("Scale");
            ImGui.Text("Bangles");
            ImGui.Text("Vertices");
            ImGui.Text(LM.Get("GUI_Frame_AssetViewer_Skeleton"));
            ImGui.EndGroup();
            ImGui.SameLine();
            ImGui.BeginGroup();
            ImGui.Text(moby.Id.ToString("X"));
            ImGui.Text(moby.Name ?? "-");
            ImGui.Text(moby.Scale.ToString("0.###"));
            ImGui.Text(moby.Bangles.Count.ToString());
            ImGui.Text(selectedMobyAsset.Value.verticesCount.ToString());
            ImGui.Text(moby.Skeleton != null
                ? LM.Get("GUI_Frame_AssetViewer_SkeletonBones", moby.Skeleton.Bones.Count)
                : LM.Get("GUI_Frame_AssetViewer_SkeletonNone"));
            ImGui.EndGroup();

            if (moby.Skeleton != null)
                ImGui.Checkbox(LM.Get("GUI_Frame_AssetViewer_ShowSkeleton"), ref showSkeleton);

            if (moby.Skeleton != null)
                DrawAnimationPanel(moby);

            if (ImGui.BeginChild("moby_bangles_switches", new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetContentRegionAvail().Y / 2), ImGuiChildFlags.Borders, ImGuiWindowFlags.AlwaysVerticalScrollbar))
            {
                var renderMap = selectedMobyAsset.Value.RenderModelMap;
                for (int i = 0; i < renderMap.Length; i++)
                {
                    if (ImGui.Checkbox($"Bangle_{i}", ref renderMap[i]))
                        IsDirty = true;
                }
            }
            ImGui.EndChild();

            ImGui.Text(LM.Get("GUI_Frame_AssetViewer_Shaders"));
            if (ImGui.BeginChild("moby_shaders", ImGui.GetContentRegionAvail(), ImGuiChildFlags.Borders, ImGuiWindowFlags.AlwaysVerticalScrollbar))
            {
                foreach (var (bangleIndex, materials) in selectedMobyMaterialsByBangle)
                {
                    if (ImGui.TreeNodeEx($"Bangle_{bangleIndex}##moby_shader_bangle_{bangleIndex}", ImGuiTreeNodeFlags.DefaultOpen))
                    {
                        RenderShaderGrid(materials, $"moby_shader_grid_{bangleIndex}");
                        ImGui.TreePop();
                    }
                }
            }
            ImGui.EndChild();

            ImGui.Separator();
            if (ImGui.Button(LM.Get("GUI_Frame_AssetViewer_FindUsages")))
                mobyUsageResults = FindMobyInstances(moby.Id);
            RenderUsageResults(mobyUsageResults, "moby_usage");
        }
        else if (selectedTieAsset != null)
        {
            var tie = selectedTieAsset.Value.Tie;
            ImGui.Separator();
            string tieAssetName = tie.Name ?? $"Tie_{tie.Id:X}";
            var tieGroups = new List<MeshGroup> { new(tieAssetName, tie.Meshes) };
            ImGui.SetNextItemWidth(200);
            ImGui.InputTextWithHint("##export_name_tie", LM.Get("GUI_Frame_AssetViewer_ExportNameHint", tieAssetName), ref exportNameOverride, 128);
            ImGui.SameLine();
            ImGuiPlus.HelpMarker(LM.Get("GUI_Frame_AssetViewer_ExportNameHelp"));
            if (ImGui.Button(LM.Get("GUI_Frame_AssetViewer_ExportGltf")))
                ExportModel(GltfExporter.Export, "glb", GetExportName(tieAssetName), tieGroups);
            ImGui.SameLine();
            if (ImGui.Button(LM.Get("GUI_Frame_AssetViewer_ExportGltfSeparate")))
                ExportModel(GltfExporter.ExportGltfSeparate, "gltf", GetExportName(tieAssetName), tieGroups, ownFolder: true);
            ImGui.SameLine();
            if (ImGui.Button(LM.Get("GUI_Frame_AssetViewer_ExportObj")))
                ExportModel(ObjExporter.Export, "obj", GetExportName(tieAssetName), tieGroups);
            
            ImGui.BeginGroup();
            ImGui.Text("Id");
            ImGui.Text("Name");
            ImGui.Text("Scale");
            ImGui.Text("Vertices");
            ImGui.EndGroup();
            ImGui.SameLine();
            ImGui.BeginGroup();
            ImGui.Text(tie.Id.ToString("X"));
            ImGui.Text(tie.Name ?? "-");
            ImGui.Text(tie.Scale.ToString("0.###"));
            ImGui.Text(selectedTieAsset.Value.verticesCount.ToString());
            ImGui.EndGroup();

            ImGui.Text(LM.Get("GUI_Frame_AssetViewer_Shaders"));
            if (ImGui.BeginChild("tie_shaders", ImGui.GetContentRegionAvail(), ImGuiChildFlags.Borders, ImGuiWindowFlags.AlwaysVerticalScrollbar))
            {
                RenderShaderGrid(selectedTieMaterials, "tie_shader_grid");
            }
            ImGui.EndChild();

            ImGui.Separator();
            if (ImGui.Button(LM.Get("GUI_Frame_AssetViewer_FindUsages")))
                tieUsageResults = FindTieInstances(tie.Id);
            RenderUsageResults(tieUsageResults, "tie_usage");
        }
        else if (selectedUFragAsset != null)
        {
            RenderUFragPanel(selectedUFragAsset.Value);
        }
        }
        ImGui.EndChild();

        VerticalSplitter("##split_lower", ref assetInfoWidth, lowerAvail.Y);

        if (ImGui.BeginChild("asset_lower_right", ImGui.GetContentRegionAvail(), ImGuiChildFlags.Borders))
            RenderSelectedMeshPanel();
        ImGui.EndChild();

        ImGui.EndGroup();
    }

    /// <summary>Blank exportNameOverride falls back to the asset's own default name; otherwise the
    /// user's typed name is used verbatim (still gets sanitized for filesystem-illegal characters
    /// by ExportModel below either way) — this is the one place that decides what name every
    /// exported file (model, .bin, and every texture) ultimately gets built from.</summary>
    private string GetExportName(string defaultName) => string.IsNullOrWhiteSpace(exportNameOverride) ? defaultName : exportNameOverride;

    /// <summary>
    /// Shared by every Moby/Tie export button — builds a sanitized output path under
    /// EditorPath/Exported/Models (asset names routinely contain path-like characters, e.g.
    /// "levels/great_clock_a/entities/.../foo.entity.irb", which would otherwise be interpreted
    /// as subdirectories) and hands off to ExportRunner for the actual background export + progress
    /// modal + result modal (shared with the whole-level export in GameBrowserFrame/FileMenuDraw).
    /// </summary>
    /// <param name="ownFolder">True for exporters that write more than one file alongside the
    /// main one (e.g. GltfExporter.ExportGltfSeparate's .bin + texture PNGs) — puts the asset in
    /// its own Exported/Models/&lt;name&gt;/ folder instead of dropping several loose files
    /// directly into Exported/Models next to every other asset's exports.</param>
    private static void ExportModel(Action<string, string, IReadOnlyList<MeshGroup>, ISkeleton?, Action<float>?> exporter, string extension, string assetName, IReadOnlyList<MeshGroup> groups, ISkeleton? skeleton = null, bool ownFolder = false)
    {
        string safeName = ExportPaths.SanitizeFileName(assetName);
        string directory = ownFolder
            ? Path.Combine(Program.EditorPath, "Exported", "Models", safeName)
            : Path.Combine(Program.EditorPath, "Exported", "Models");
        string path = Path.Combine(directory, $"{safeName}.{extension}");

        ExportRunner.Run(LM.Get("GUI_Frame_AssetViewer_ExportingTitle"), path, directory,
            progress => exporter(path, safeName, groups, skeleton, progress));
    }

    /// <summary>One MeshGroup per bangle (indexed name fallback for unnamed bangles) — keeps
    /// bangles as distinct submeshes/nodes on export instead of flattening the whole Moby into a
    /// single mesh, since bangles are independently toggleable parts (see RenderModelMap above),
    /// not interchangeable LOD/skin variants.</summary>
    /// <summary>The scene entity's own already-built GPU mesh for this UFrag, or null if the level
    /// produced no entity for it. Borrowed, never owned: building a second Mesh here would duplicate
    /// the vertex buffer AND detach the preview from the material the 3D view actually renders with —
    /// including its bound lightmap atlases, which is the whole point of previewing a UFrag.</summary>
    private static Bliss.CSharp.Geometry.Meshes.IMesh? ResolveUFragMesh(UFragAsset asset) =>
        EntityManager.Singleton.AllEntities().OfType<EntityUFrag>()
            .FirstOrDefault(e => ReferenceEquals(e.UFrag, asset.UFrag))?.UFragMesh;

    /// <summary>Pulls the camera back far enough to frame the selected UFrag. Necessary because the
    /// mesh keeps its true 1/256 scale (see the renderable build) and UFrags vary from a few world
    /// units across to tens — a fixed camera distance shows either a speck or the inside of a wall.
    /// Clamped under the camera's 100f far plane so a large chunk can't land entirely beyond it.</summary>
    private void FrameUFragInPreview(UFragAsset asset)
    {
        float radius = MathF.Max(asset.localRadius / 256f, 0.01f);
        float distance = Math.Clamp(radius * 2.5f, 0.05f, 80f);
        Camera.Target = Vector3.Zero;
        Camera.Position = new Vector3(0f, radius * 0.35f, -distance);
    }

    /// <summary>Export payload for a UFrag: one mesh, one shader. Positions are descaled by 256 to
    /// world units, and the placement ANCHOR is deliberately not applied — the export is asset-local,
    /// matching Moby/Tie export, so a UFrag lands at the origin rather than wherever it sits in the
    /// level. Real normals/tangents are passed through so GeometryData doesn't recompute them from
    /// triangles when the file already told us (its tangent handedness is still derived, as always).</summary>
    private static List<MeshGroup> GetUFragGroups(UFragAsset asset, string name)
    {
        var ufrag = asset.UFrag;
        var raw = ufrag.GetVertexPositions();
        var positions = new float[raw.Length];
        for (int i = 0; i < raw.Length; i++) positions[i] = raw[i] / 256f;

        var geometry = new ReLunacy.Engine.Assets.Geometry.GeometryData(
            id: (ulong)asset.Index,
            positions: positions,
            uvs: ufrag.GetTextureCoordinates(),
            indices: ufrag.GetIndices(),
            normals: ufrag.GetNormals(),
            tangents: ufrag.GetTangents());

        return [new MeshGroup(name, new IMesh[] { new ReLunacy.Engine.Assets.Geometry.Mesh(geometry, ufrag.Material, name) })];
    }

    private void RenderUFragPanel(UFragAsset asset)
    {
        var ufrag = asset.UFrag;
        string defaultName = $"UFrag_{asset.ZoneId:X}_{asset.Index}";

        ImGui.Separator();
        ImGui.SetNextItemWidth(200);
        ImGui.InputTextWithHint("##export_name_ufrag", LM.Get("GUI_Frame_AssetViewer_ExportNameHint", defaultName), ref exportNameOverride, 128);
        ImGui.SameLine();
        ImGuiPlus.HelpMarker(LM.Get("GUI_Frame_AssetViewer_ExportNameHelp"));

        string exportName = GetExportName(defaultName);
        if (ImGui.Button(LM.Get("GUI_Frame_AssetViewer_ExportGltf")))
            ExportModel(GltfExporter.Export, "glb", exportName, GetUFragGroups(asset, exportName));
        ImGui.SameLine();
        if (ImGui.Button(LM.Get("GUI_Frame_AssetViewer_ExportGltfSeparate")))
            ExportModel(GltfExporter.ExportGltfSeparate, "gltf", exportName, GetUFragGroups(asset, exportName), ownFolder: true);
        ImGui.SameLine();
        if (ImGui.Button(LM.Get("GUI_Frame_AssetViewer_ExportObj")))
            ExportModel(ObjExporter.Export, "obj", exportName, GetUFragGroups(asset, exportName));
        ImGuiPlus.HelpMarker(LM.Get("GUI_Frame_AssetViewer_UFragExportNote"));

        ImGui.BeginGroup();
        ImGui.Text(LM.Get("GUI_Frame_AssetViewer_UFragEngine"));
        ImGui.Text(LM.Get("GUI_Frame_AssetViewer_UFragAnchor"));
        ImGui.Text("Vertices");
        ImGui.Text(LM.Get("GUI_Frame_AssetViewer_UFragTriangles"));
        ImGui.EndGroup();
        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.Text(ufrag.IsOldEngine ? "Old" : "New");
        ImGui.Text(ufrag.GetAnchor().ToString("0.###"));
        ImGui.Text(asset.verticesCount.ToString());
        ImGui.Text(asset.triangleCount.ToString());
        ImGui.EndGroup();
        ImGui.TextDisabled(LM.Get("GUI_Frame_AssetViewer_UFragZone", asset.ZoneId.ToString("X"), asset.Index));

        if (ResolveUFragMesh(asset) == null)
            ImGui.TextDisabled(LM.Get("GUI_Frame_AssetViewer_UFragNoPreview"));

        RenderUFragBakedSection(asset);

        ImGui.Text(LM.Get("GUI_Frame_AssetViewer_Shaders"));
        if (ImGui.BeginChild("ufrag_shaders", new Vector2(ImGui.GetContentRegionAvail().X, 90f), ImGuiChildFlags.Borders, ImGuiWindowFlags.AlwaysVerticalScrollbar))
            RenderShaderGrid(selectedTieMaterials, "ufrag_shader_grid");
        ImGui.EndChild();

        ImGui.SeparatorText(LM.Get("GUI_Frame_ShaderBrowser_RawMetadataSection"));
        ImGui.Checkbox(LM.Get("GUI_Frame_ShaderBrowser_ShowAsFloats"), ref ufragShowFloatInterpretation);
        if (ufrag.Metadata is not { } meta)
        {
            ImGui.TextDisabled(LM.Get("GUI_Frame_AssetViewer_UFragNoMetadata"));
            return;
        }
        ImGui.Text($"0x40 indexOffset:   {meta.indexOffset}");
        ImGui.Text($"0x44 vertexOffset:  {meta.vertexOffset}");
        ImGui.Text($"0x48 indexCount:    {meta.indexCount}");
        ImGui.Text($"0x4A vertexCount:   {meta.vertexCount}");
        ImGui.Text($"0x4E lightmapIndex: {meta.lightmapIndex}");
        ImGui.Text($"0x50 shaderIndex:   {meta.shaderIndex}");
        DrawUFragHexDump("Unk1", 0x00, meta.Unk1);
        DrawUFragHexDump("Unk2", 0x4C, meta.Unk2);
        DrawUFragHexDump("Unk3", 0x52, meta.Unk3);
        if (meta.Unk4 is { Length: > 0 }) DrawUFragHexDump("Unk4", 0x6C, meta.Unk4);
        if (meta.Unk3b is { Length: > 0 }) DrawUFragHexDump("Unk3b", 0x7C, meta.Unk3b);
    }

    /// <summary>The baked-lighting readout: which atlas entry this UFrag resolves to, the UV rectangle
    /// its vertices occupy, and the atlases themselves with the UV island drawn on top.
    ///
    /// The rect and the overlay separate the two failure modes that look identical on screen — a UFrag
    /// rendering black because its atlas region genuinely IS black, versus because it is addressing the
    /// wrong region. That distinction is what caught the UVs2 decode bug (islands were landing about
    /// two texels wide, see UFragVertex.UVs2), so it stays even though that particular bug is fixed.</summary>
    private void RenderUFragBakedSection(UFragAsset asset)
    {
        var ufrag = asset.UFrag;
        ImGui.SeparatorText(LM.Get("GUI_Frame_AssetViewer_UFragLightmapSection"));

        ushort lm = ufrag.LightmapIndex;
        ImGui.Text(LM.Get("GUI_Frame_AssetViewer_UFragLightmapIndex",
            asset.HasLightmap ? lm.ToString() : LM.Get("GUI_Frame_ShaderBrowser_None")));

        var lmUVs = ufrag.GetLightmapUVs();
        if (lmUVs == null || lmUVs.Length < 2)
        {
            ImGui.TextDisabled(LM.Get("GUI_Frame_AssetViewer_UFragNoLightmapUVs"));
        }
        else
        {
            float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
            for (int i = 0; i + 1 < lmUVs.Length; i += 2)
            {
                minU = MathF.Min(minU, lmUVs[i]); maxU = MathF.Max(maxU, lmUVs[i]);
                minV = MathF.Min(minV, lmUVs[i + 1]); maxV = MathF.Max(maxV, lmUVs[i + 1]);
            }
            ImGui.Text(LM.Get("GUI_Frame_AssetViewer_UFragUVRect", minU, maxU, minV, maxV));
            ImGui.Text(LM.Get("GUI_Frame_AssetViewer_UFragUVExtent", maxU - minU, maxV - minV));
            if (minU < -0.001f || maxU > 1.001f || minV < -0.001f || maxV > 1.001f)
                ImGui.TextColored(new Vector4(1f, 0.5f, 0.3f, 1f), LM.Get("GUI_Frame_AssetViewer_UFragUVOutOfRange"));
        }

        if (!asset.HasLightmap) return;

        // Preview-local, so these can be swept while looking at one UFrag without disturbing the 3D
        // view. They drive THIS frame's own renderer instance, which is also why the UV overlay below
        // reads its transform from the same place: the overlay has to describe the shader that drew
        // the image next to it, or it lies.
        if (renderer is DecalAwareForwardRenderer lit)
        {
            ImGui.SeparatorText(LM.Get("GUI_Frame_AssetViewer_UFragPreviewSection"));
            if (!Program.Settings.EnableLighting)
                ImGui.TextDisabled(LM.Get("GUI_Frame_AssetViewer_UFragNeedsLighting"));

            ImGui.DragFloat(LM.Get("GUI_Frame_AssetViewer_UFragBakedScale"), ref lit.BakedLightScale, 0.05f, 0f, 64f, "%.2f");
            ImGuiPlus.HelpMarker(LM.Get("GUI_Frame_AssetViewer_UFragBakedScaleHelp"));
            ImGui.Checkbox(LM.Get("GUI_Frame_AssetViewer_UFragDebugView"), ref lit.BakedDebugView);
            ImGuiPlus.HelpMarker(LM.Get("GUI_Frame_AssetViewer_UFragDebugViewHelp"));
            ImGui.SameLine();
            if (ImGui.SmallButton($"{LM.Get("GUI_Common_Reset")}##ufrag_preview_reset"))
            {
                lit.BakedLightScale = 4f;
                lit.BakedDebugView = false;
            }
        }

        var am = LunaWindow.Instance.AssetManager;
        if (am == null) return;

        ImGui.Checkbox(LM.Get("GUI_Frame_AssetViewer_UFragShowUVOverlay"), ref ufragShowUVOverlay);
        if (ufragShowUVOverlay)
        {
            ImGui.SameLine();
            ImGui.Checkbox(LM.Get("GUI_Frame_AssetViewer_UFragShowUVWireframe"), ref ufragShowUVWireframe);
        }

        const float size = 256f;
        if (lm < am.ZoneLightmaps.Count && am.ZoneLightmaps[lm] is { } colour)
        {
            ImGui.Text(LM.Get("GUI_Frame_AssetViewer_UFragLightColour"));
            var ptr = LunaWindow.Instance.imGuiController.GetOrCreateImGuiBinding(LunaWindow.Instance.GraphicsDevice.ResourceFactory, colour.DeviceTexture);
            ImGui.Image(ptr, new(size, size), Vector2.UnitY, Vector2.UnitX);
            if (ufragShowUVOverlay)
                DrawUFragUVOverlay(ufrag, ImGui.GetItemRectMin(), size, colour);
        }
        if (lm < am.ZoneDirectionals.Count && am.ZoneDirectionals[lm] is { } dir)
        {
            ImGui.Text(LM.Get("GUI_Frame_AssetViewer_UFragLightDirection"));
            var ptr = LunaWindow.Instance.imGuiController.GetOrCreateImGuiBinding(LunaWindow.Instance.GraphicsDevice.ResourceFactory, dir.DeviceTexture);
            ImGui.Image(ptr, new(size, size), Vector2.UnitY, Vector2.UnitX);
        }
    }

    /// <summary>Projects this UFrag's lightmap UVs onto the atlas image just drawn.</summary>
    private void DrawUFragUVOverlay(IUFrag ufrag, Vector2 origin, float size, Texture2D atlas)
    {
        var uvs = ufrag.GetLightmapUVs();
        if (uvs == null || uvs.Length < 6) return;

        // Read from THIS frame's renderer, not View3D's — the overlay must describe the shader that
        // produced the preview beside it. Identity in normal use; the fields exist as research knobs.
        var lit = renderer as DecalAwareForwardRenderer;
        Vector2 scale = lit?.LightmapUVScale ?? Vector2.One;
        Vector2 offset = lit?.LightmapUVOffset ?? Vector2.Zero;
        Vector2 pivot = lit?.LightmapUVPivot ?? new Vector2(0.5f, 0.5f);
        float rotDeg = lit?.LightmapUVRotation ?? 0f;
        float sin = MathF.Sin(rotDeg * MathF.PI / 180f);
        float cos = MathF.Cos(rotDeg * MathF.PI / 180f);

        // Must match LitModelShaderSource exactly: rotate about the pivot, then scale, then offset.
        Vector2 Transform(float u, float v)
        {
            var c = new Vector2(u, v) - pivot;
            var r = new Vector2(c.X * cos - c.Y * sin, c.X * sin + c.Y * cos) + pivot;
            return r * scale + offset;
        }

        // The atlas is drawn with uv0=(0,1)/uv1=(1,0), i.e. V-FLIPPED, so v=1 is at the top of the
        // image. Screen Y therefore uses (1 - v) — forgetting this silently mirrors the overlay.
        Vector2 ToScreen(Vector2 uv) => new(origin.X + uv.X * size, origin.Y + (1f - uv.Y) * size);

        var draw = ImGui.GetWindowDrawList();
        uint colPoint = ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 1f, 0.4f, 0.95f));
        uint colWire = ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 1f, 0.4f, 0.35f));
        uint colRect = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.85f, 0.2f, 0.9f));

        draw.PushClipRect(origin, origin + new Vector2(size, size), true);

        if (ufragShowUVWireframe)
        {
            // Capped: a UFrag can carry thousands of triangles and ImGui's draw list is not the place
            // to spend them. A subsample still shows the island's shape and winding.
            var indices = ufrag.GetIndices();
            int triCount = indices.Length / 3;
            int step = Math.Max(1, triCount / 1500);
            for (int t = 0; t < triCount; t += step)
            {
                int i0 = (int)indices[t * 3], i1 = (int)indices[t * 3 + 1], i2 = (int)indices[t * 3 + 2];
                if (i0 * 2 + 1 >= uvs.Length || i1 * 2 + 1 >= uvs.Length || i2 * 2 + 1 >= uvs.Length) continue;
                draw.AddTriangle(
                    ToScreen(Transform(uvs[i0 * 2], uvs[i0 * 2 + 1])),
                    ToScreen(Transform(uvs[i1 * 2], uvs[i1 * 2 + 1])),
                    ToScreen(Transform(uvs[i2 * 2], uvs[i2 * 2 + 1])), colWire, 1f);
            }
        }

        float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
        int vertStep = Math.Max(1, (uvs.Length / 2) / 2000);
        for (int i = 0; i + 1 < uvs.Length; i += 2 * vertStep)
        {
            var uv = Transform(uvs[i], uvs[i + 1]);
            minU = MathF.Min(minU, uv.X); maxU = MathF.Max(maxU, uv.X);
            minV = MathF.Min(minV, uv.Y); maxV = MathF.Max(maxV, uv.Y);
            draw.AddCircleFilled(ToScreen(uv), 1.5f, colPoint);
        }

        // Bounding box last so it sits above the points.
        draw.AddRect(ToScreen(new Vector2(minU, maxV)), ToScreen(new Vector2(maxU, minV)), colRect, 0f, ImDrawFlags.None, 1.5f);
        draw.PopClipRect();

        // In TEXELS, because that is the unit the implausibility shows up in: a terrain chunk mapping
        // to a handful of texels cannot resolve a baked shadow no matter where it lands.
        ImGui.TextDisabled(LM.Get("GUI_Frame_AssetViewer_UFragIslandTexels",
            (maxU - minU) * atlas.Width, (maxV - minV) * atlas.Height, atlas.Width, atlas.Height));
    }

    private void DrawUFragHexDump(string label, int baseOffset, byte[]? data)
    {
        if (data == null || data.Length == 0)
        {
            ImGui.Text($"{label}: ({LM.Get("GUI_Frame_ShaderBrowser_Empty")})");
            return;
        }

        ImGui.Text($"{label} (0x{baseOffset:X2}, {data.Length} bytes):");
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < data.Length; i += 16)
        {
            sb.Append($"  {baseOffset + i:X4}: ");
            int lineEnd = Math.Min(i + 16, data.Length);
            for (int j = i; j < lineEnd; j++)
                sb.Append($"{data[j]:X2} ");
            sb.Append('\n');
        }
        ImGui.TextUnformatted(sb.ToString());

        if (!ufragShowFloatInterpretation || data.Length < 4)
            return;

        // Big-endian, and aligned to the FILE's absolute offset rather than this array's start — a real
        // float field sits on a real 4-byte boundary, so aligning to the array splits every value.
        var floatSb = new System.Text.StringBuilder();
        int firstAligned = (4 - (baseOffset & 3)) & 3;
        for (int i = firstAligned; i + 3 < data.Length; i += 4)
        {
            float f = System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(data.AsSpan(i, 4));
            floatSb.Append($"  {baseOffset + i:X4}: {f,14:0.000000}\n");
        }
        ImGui.TextUnformatted(floatSb.ToString());
    }

    private static List<MeshGroup> GetMobyGroups(IMoby moby) =>
        moby.Bangles.Select((bangle, i) => new MeshGroup(string.IsNullOrEmpty(bangle.Name) ? $"Bangle_{i}" : bangle.Name, bangle.Meshes)).ToList();

    /// <summary>
    /// Draws each bone-to-parent segment as a red line, using WorldBindPose's translation
    /// directly with no extra scale applied — unlike the raw fixed-point vertex positions
    /// (MobyMesh.GetBuffers multiplies those by moby.Scale), the skeleton's tms0/tms1 matrices are
    /// plain floats already in the same absolute space the scaled mesh geometry ends up in
    /// (confirmed against InsomniaToolset: its glTF exporter applies meshScale only to the vertex
    /// position attribute, never to the skeleton matrices). The preview's own meshes are drawn at
    /// an identity Transform, so no further placement transform belongs here either.
    /// </summary>
    /// <summary>Old-engine (Tools of Destruction) animation controls for the selected Moby - a
    /// clickable list of the clips THIS Moby actually owns (main.dat 0xD100
    /// animationCount/animationListPointer, see Loading.Readers.MobyAnimationResolver - NOT a
    /// bone-count compatibility guess over every clip in the level), plus transport, timeline, and
    /// a collapsible raw-field debug block.</summary>
    private void DrawAnimationPanel(Moby moby)
    {
        ISkeleton? skeleton = moby.Skeleton;
        if (skeleton == null) return;

        if (moby.Animations.Count == 0)
        {
            ImGui.TextDisabled("This Moby has no animation set of its own.");
            ImGui.TextDisabled("(main.dat 0xD100 animationCount == 0)");
            return;
        }

        var clip = _animationPlayer.Clip;

        // Clip picker on top, transport pinned underneath it. The transport keeps a fixed spot
        // rather than flowing after a variable-height list, so the play/scrub controls don't move
        // under the cursor as clips are filtered.
        float transportHeight = ImGui.GetFrameHeightWithSpacing() * 4f + ImGui.GetTextLineHeightWithSpacing() * 2f;
        float listHeight = Math.Max(90f, ImGui.GetContentRegionAvail().Y - transportHeight);

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        ImGui.InputTextWithHint("##anim_clip_search", $"filter {moby.Animations.Count} clips...", ref _animationClipSearch, 128);

        if (ImGui.BeginChild("anim_clip_list", new Vector2(ImGui.GetContentRegionAvail().X, listHeight), ImGuiChildFlags.Borders))
        {
            int shown = 0;
            foreach (var candidate in moby.Animations)
            {
                if (_animationClipSearch.Length > 0 &&
                    candidate.Name.IndexOf(_animationClipSearch, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                shown++;
                bool isSelected = ReferenceEquals(candidate, clip);
                if (ImGui.Selectable($"{candidate.Name}##clip_{candidate.GetHashCode()}", isSelected))
                {
                    _animationPlayer.SetClip(candidate);
                    _animationPlayer.Play();
                    DisposeSkinnedMeshes();
                }

                // Frame count / partial-pose marker on the right, dimmed: useful when scanning a
                // long list for the clip you want, without competing with the name itself.
                ImGui.SameLine();
                ImGui.TextDisabled(candidate.Additive ? $"{candidate.NumFrames}f  partial" : $"{candidate.NumFrames}f");
            }

            if (shown == 0)
                ImGui.TextDisabled("No clip matches the filter.");
        }
        ImGui.EndChild();

        if (clip == null)
        {
            ImGui.TextDisabled("Select a clip to play it.");
            return;
        }

        DrawAnimationTransport(clip);

        if (ImGui.CollapsingHeader("Animation Debug"))
        {
            uint physicalStride = ReLunacy.Engine.Loading.Readers.AnimationReader.PhysicalFrameStride(clip.Header);
            ImGui.BeginGroup();
            ImGui.Text("Animation index"); ImGui.Text("Flags"); ImGui.Text("Frames"); ImGui.Text("FPS");
            ImGui.Text("Header bone count"); ImGui.Text("Skeleton bone count");
            ImGui.Text("FrameStride (payload)"); ImGui.Text("PhysicalFrameStride"); ImGui.Text("16-bit tracks");
            ImGui.Text("8-bit tracks"); ImGui.Text("Reference values"); ImGui.Text("ControlPtr");
            ImGui.Text("FramesPtr"); ImGui.Text("RootMotionPtr");
            ImGui.EndGroup();
            ImGui.SameLine();
            ImGui.BeginGroup();
            ImGui.Text(clip.Header.animIndex.ToString());
            ImGui.Text($"0x{clip.Header.flags:X4} (loop={clip.Looping} additive={clip.Additive} packed={clip.Header.IsPacked})");
            ImGui.Text(clip.NumFrames.ToString());
            ImGui.Text(clip.FrameRate.ToString("0.###"));
            ImGui.Text(clip.NumBones.ToString());
            ImGui.Text(skeleton.Bones.Count.ToString());
            ImGui.Text($"0x{clip.Header.frameStride:X}");
            ImGui.Text($"0x{physicalStride:X}");
            ImGui.Text(clip.Num16BitTracks.ToString());
            ImGui.Text(clip.Num8BitTracks.ToString());
            ImGui.Text(clip.Header.numReferenceValues.ToString());
            ImGui.Text($"0x{clip.Header.controlPtr:X}");
            ImGui.Text($"0x{clip.Header.framesPtr:X}");
            ImGui.Text($"0x{clip.Header.rootMotionPtr:X}");
            ImGui.EndGroup();

            if (clip.NumBones != skeleton.Bones.Count)
                ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f),
                    $"Header bone count ({clip.NumBones}) differs from this skeleton's ({skeleton.Bones.Count}) - " +
                    "bones beyond the clip's own count keep their bind pose (see AnimationPlayer.SamplePose remarks).");
        }
    }

    /// <summary>Playback controls for the loaded clip: a transport row, a frame-quantized scrubber,
    /// and the loop/speed options. Everything that moves the playhead goes through
    /// AnimationPlayer.SeekToFrame rather than Seek(frame / fps) — the float round-trip through
    /// Seek could land just short of a frame boundary and step nowhere.</summary>
    private void DrawAnimationTransport(AnimationClip clip)
    {
        bool isPlaying = _animationPlayer.IsPlaying;
        int lastFrame = Math.Max(clip.NumFrames - 1, 0);
        int currentFrame = _animationPlayer.CurrentFrame;

        // "###" keeps one stable widget id while the visible label flips between Play and Pause -
        // with a plain label the id would change on every toggle, which makes ImGui treat it as a
        // different widget mid-interaction.
        if (TransportButton("|<##anim_first", "Jump to first frame"))
        {
            _animationPlayer.Pause();
            _animationPlayer.SeekToFrame(0);
        }
        ImGui.SameLine();
        if (TransportButton("<##anim_prev", "Previous frame"))
        {
            _animationPlayer.Pause();
            _animationPlayer.SeekToFrame(currentFrame - 1);
        }
        ImGui.SameLine();
        if (TransportButton(isPlaying ? "Pause###anim_play" : "Play###anim_play", isPlaying ? "Pause playback" : "Play from the current frame", width: 56f))
        {
            if (isPlaying) _animationPlayer.Pause();
            else _animationPlayer.Play();
        }
        ImGui.SameLine();
        if (TransportButton(">##anim_next", "Next frame"))
        {
            _animationPlayer.Pause();
            _animationPlayer.SeekToFrame(currentFrame + 1);
        }
        ImGui.SameLine();
        if (TransportButton(">|##anim_last", "Jump to last frame"))
        {
            _animationPlayer.Pause();
            _animationPlayer.SeekToFrame(lastFrame);
        }
        ImGui.SameLine();
        if (TransportButton("Stop##anim_stop", "Stop and return the model to its bind pose", width: 56f))
        {
            _animationPlayer.SetClip(null);
            DisposeSkinnedMeshes();
            return;
        }

        int scrubFrame = currentFrame;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (ImGui.SliderInt("##anim_frame", ref scrubFrame, 0, lastFrame, $"frame %d / {lastFrame}"))
        {
            _animationPlayer.Pause();
            _animationPlayer.SeekToFrame(scrubFrame);
        }

        bool loop = _animationPlayer.Loop;
        if (ImGui.Checkbox("Loop", ref loop)) _animationPlayer.Loop = loop;
        ImGui.SameLine();
        float speed = _animationPlayer.Speed;
        ImGui.SetNextItemWidth(110);
        if (ImGui.SliderFloat("##anim_speed", ref speed, 0f, 3f, "%.2fx")) _animationPlayer.Speed = speed;
        ImGui.SameLine();
        if (ImGui.SmallButton("1x##anim_speed_reset")) _animationPlayer.Speed = 1f;

        ImGui.TextDisabled($"{clip.FrameRate:0.#} fps  -  {clip.DurationSeconds:0.00}s  -  " +
                           $"{(clip.Looping ? "loops natively" : "no native loop")}{(clip.Additive ? "  -  partial pose" : "")}");
    }

    private static bool TransportButton(string label, string tooltip, float width = 32f)
    {
        bool clicked = ImGui.Button(label, new Vector2(width, 0));
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
            ImGui.SetTooltip(tooltip);
        return clicked;
    }

    /// <summary>(Re)builds, if needed, one CPU-skinned Bliss mesh per (bangle, mesh) of the selected
    /// Moby - reusing the SAME Mesh/BasicMeshData objects and cachedRenderables list across frames,
    /// only ever rewriting their vertex buffers - then re-skins every vertex against the current
    /// AnimationPlayer pose and pushes the update to the GPU. Called every frame while a clip is
    /// assigned (see Render); <paramref name="forceRebuild"/> is set on selection/RenderModelMap/
    /// clip changes (mirrors the static path's IsDirty gate) to throw away stale meshes from the
    /// PREVIOUS Moby/bangle-visibility selection.
    ///
    /// Reuses the shared AssetManager Material and every non-position/normal geometry channel
    /// (UVs, tangents) unchanged from the static Model - only positions/normals move.</summary>
    private void UpdateSkinnedVertices(MobyAsset asset, ISkeleton skeleton, bool forceRebuild)
    {
        if (forceRebuild || _skinnedMeshes.Count == 0)
            BuildSkinnedMeshList(asset);

        if (_skinnedMeshes.Count == 0) return;

        var skin = _animationPlayer.SamplePose(skeleton);

        int k = 0;
        var bangles = asset.Moby.Bangles;
        var renderMap = asset.RenderModelMap;
        for (int i = 0; i < bangles.Count; i++)
        {
            if (i >= renderMap.Length || !renderMap[i]) continue;
            foreach (var mesh in bangles[i].Meshes)
            {
                if (k >= _skinnedMeshes.Count) break;
                var (blissMesh, _) = _skinnedMeshes[k++];
                var vertices = BuildSkinnedVertices(mesh.Geometry, skin);
                // BasicMeshData.Vertices has no public setter - each vertex is pushed individually
                // through the mesh's own (public) per-vertex API instead of replacing the backing
                // array wholesale.
                for (int v = 0; v < vertices.Length; v++)
                    blissMesh.SetVertexValueImmediate(v, vertices[v]);
            }
        }
    }

    /// <summary>Disposes any previous animated-preview meshes and builds fresh ones (bind-pose
    /// vertices - UpdateSkinnedVertices immediately overwrites them) for every visible mesh of
    /// <paramref name="asset"/>, replacing cachedRenderables wholesale. One-time per selection/
    /// visibility change, not per frame.</summary>
    private void BuildSkinnedMeshList(MobyAsset asset)
    {
        DisposeSkinnedMeshes();
        cachedRenderables.Clear();

        if (assetManager is null) return;

        var bangles = asset.Moby.Bangles;
        var renderMap = asset.RenderModelMap;
        for (int i = 0; i < bangles.Count; i++)
        {
            if (i >= renderMap.Length || !renderMap[i]) continue;
            foreach (var mesh in bangles[i].Meshes)
            {
                var material = assetManager.GetOrBuildMaterial(mesh.Material);
                var vertices = BuildSkinnedVertices(mesh.Geometry, null);
                var indices = mesh.Geometry.GetIndices();
                var data = new BasicMeshData(vertices, indices);
                var blissMesh = new Mesh<Vertex3D>(graphicsDevice, material, data);

                _skinnedMeshes.Add((blissMesh, data));
                cachedRenderables.Add(new Renderable(blissMesh, new Transform { Rotation = Quaternion.Identity, Scale = Vector3.One, Translation = Vector3.Zero }));
            }
        }
    }

    /// <summary>CPU-skins one mesh's geometry: for every vertex, blends the BIND-pose position/
    /// normal across up to 4 (bone, weight) influences from IGeometry.GetJointIndices/GetJointWeights
    /// (already skeleton-global indices and normalized-ish weights - see MobyReader.ExtractSkinData),
    /// each transformed by that bone's current skin matrix
    /// (<c>skin[bone] = InverseBindPose[bone] * animatedWorld[bone]</c> - strips the bind transform,
    /// reapplies the animated one). A vertex with no valid influence (or <paramref name="skin"/> null,
    /// used only to build the initial bind-pose buffer before the first real pose is sampled) keeps
    /// its raw bind-pose position/normal unchanged.
    ///
    /// UVs/tangents pass through unskinned (tangents are not re-oriented by the skin rotation - a
    /// known simplification: shape/position are exactly skinned, specular/normal-map lighting on a
    /// heavily-rotated bone may look slightly off). Vertex colour is left at opaque white; this
    /// preview path doesn't carry the vertex-alpha candidate the static path optionally uses.</summary>
    private static Vertex3D[] BuildSkinnedVertices(IGeometry geometry, Matrix4x4[]? skin)
    {
        var positions = geometry.GetVertexPositions();
        var normals = geometry.GetNormals();
        var uvs = geometry.GetTextureCoordinates();
        var tangents = geometry.GetTangents();
        var jointIndices = geometry.GetJointIndices();
        var jointWeights = geometry.GetJointWeights();

        int vertexCount = positions.Length / 3;
        var result = new Vertex3D[vertexCount];

        for (int i = 0; i < vertexCount; i++)
        {
            var pos = new Vector3(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2]);
            var normal = normals != null && normals.Length >= i * 3 + 3
                ? new Vector3(normals[i * 3], normals[i * 3 + 1], normals[i * 3 + 2])
                : Vector3.UnitY;

            if (skin != null && jointIndices != null && jointWeights != null)
            {
                Vector3 skinnedPos = Vector3.Zero, skinnedNormal = Vector3.Zero;
                float weightSum = 0f;
                for (int s = 0; s < 4; s++)
                {
                    int bone = jointIndices[i * 4 + s];
                    float w = jointWeights[i * 4 + s];
                    if (bone < 0 || w <= 0f || bone >= skin.Length) continue;
                    var m = skin[bone];
                    skinnedPos += Vector3.Transform(pos, m) * w;
                    skinnedNormal += Vector3.TransformNormal(normal, m) * w;
                    weightSum += w;
                }
                if (weightSum > 1e-6f)
                {
                    pos = skinnedPos / weightSum;
                    normal = skinnedNormal / weightSum;
                }
            }

            var uv = new Vector2(uvs[i * 2], uvs[i * 2 + 1]);
            var tan = tangents != null && tangents.Length >= i * 4 + 4
                ? new Vector4(tangents[i * 4], tangents[i * 4 + 1], tangents[i * 4 + 2], tangents[i * 4 + 3])
                : new Vector4(1f, 0f, 0f, 1f);
            var n = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY;

            result[i] = new Vertex3D(pos, uv, uv, n, tan, Vector4.One);
        }

        return result;
    }

    /// <summary>When <paramref name="animatedWorld"/> is supplied (an active animation clip - see
    /// AnimationPlayer.LastAnimatedWorld), the overlay follows the ANIMATED pose instead of the bind
    /// pose, per-index-matched to skeleton.Bones. Null draws the plain bind pose exactly as before.</summary>
    private static void DrawSkeleton(ISkeleton skeleton, ImmediateRenderer immediateRenderer, Matrix4x4[]? animatedWorld = null)
    {
        for (int i = 0; i < skeleton.Bones.Count; i++)
        {
            var bone = skeleton.Bones[i];
            if (bone.ParentIndex < 0) continue;

            Vector3 childPos, parentPos;
            if (animatedWorld != null && i < animatedWorld.Length && bone.ParentIndex < animatedWorld.Length)
            {
                childPos = animatedWorld[i].Translation;
                parentPos = animatedWorld[bone.ParentIndex].Translation;
            }
            else
            {
                childPos = bone.WorldBindPose.Translation;
                parentPos = skeleton.Bones[bone.ParentIndex].WorldBindPose.Translation;
            }
            immediateRenderer.DrawLine(parentPos, childPos, Bliss.CSharp.Colors.Color.Red);
        }
    }

    public override void RenderAsWindow(double deltaTime)
    {
        ImGui.SetNextWindowPos(DefaultPosition, DockingConditions, new Vector2(0.5f));
        ImGui.SetNextWindowSizeConstraints(new(400, 300), ImGui.GetMainViewport().Size);
        base.RenderAsWindow(deltaTime);
    }

    private void Tick(double deltaTime)
    {
        RenderFramePos = ImGui.GetCursorScreenPos();
        var wcravail = ImGui.GetContentRegionAvail();
        int width = (int)wcravail.X,
            height = (int)wcravail.Y;

        RenderFrameSize = new Rectangle((int)RenderFramePos.X, (int)RenderFramePos.Y, width, height);
        var windowMousePos = Input.GetMousePosition();

        // RenderFrameSize's origin is already RenderFramePos (absolute screen coords, unlike
        // View3D's zero-relative FrameContentRegion) — adding RenderFrameSize.GetOriginF() here
        // on top of RenderFramePos double-subtracted it, shifting every pick by an extra
        // -RenderFramePos and throwing off exactly the click-to-viewport mapping this was for.
        MousePos = windowMousePos - RenderFramePos;

        Point absMousePos = new((int)windowMousePos.X, (int)windowMousePos.Y);
        bool isHoveringWnd = ImGui.IsWindowHovered();
        bool isMouseInCntReg = RenderFrameSize.Contains(absMousePos);
        CheckCameraDragInput(isMouseInCntReg);

        // Scroll-zoom is independent of the RMB rotate-drag and gated purely on hovering the
        // render image, not "anywhere in the window" — otherwise scrolling while reading the
        // asset details panel or browsing the hierarchy would zoom the preview too.
        // MoveToTarget (not Position +=) keeps Target fixed on the asset while dollying Position
        // along the view axis — Position += would drag the orbit pivot off the asset every zoom.
        if (isHoveringWnd && isMouseInCntReg && Input.IsMouseScrolling(out var scrollDelta))
            Camera.MoveToTarget(-scrollDelta.Y * 0.5f);

        // Left click picks a bangle/mesh under the cursor — independent of the RMB orbit-drag
        // above (different button, no gizmo in this viewport to conflict with).
        if (isHoveringWnd && isMouseInCntReg && Input.IsMouseButtonPressed(MouseButton.Left))
            pickRequested = true;
    }

    /// <summary>
    /// GPU color-ID picking scoped to this viewport's own preview model (same PickingRenderer
    /// class View3D uses for whole-entity picking, but the id here is packed straight from local
    /// (bangleIndex, meshIndex) instead of a globally-unique per-mesh id — this viewport only ever
    /// shows one asset at a time, so there's no cross-asset collision risk to design around.
    /// bangleIndex is always 0 for Ties.
    /// </summary>
    private void PickMeshUnderCursor()
    {
        if (RenderFrameSize.Width <= 0 || RenderFrameSize.Height <= 0) return;

        var entries = new List<(Bliss.CSharp.Geometry.Meshes.IMesh mesh, Matrix4x4 world, uint id)>();
        if (selectedMobyAsset != null)
        {
            var models = selectedMobyAsset.Value.Model;
            var renderMap = selectedMobyAsset.Value.RenderModelMap;
            for (int bangleIndex = 0; bangleIndex < models.Length; bangleIndex++)
            {
                if (!renderMap[bangleIndex]) continue;
                var meshes = models[bangleIndex].Meshes;
                for (int meshIndex = 0; meshIndex < meshes.Length; meshIndex++)
                    entries.Add((meshes[meshIndex], Matrix4x4.Identity, (uint)((bangleIndex << 16) | meshIndex)));
            }
        }
        else if (selectedTieAsset != null)
        {
            var meshes = selectedTieAsset.Value.Model.Meshes;
            for (int meshIndex = 0; meshIndex < meshes.Length; meshIndex++)
                entries.Add((meshes[meshIndex], Matrix4x4.Identity, (uint)meshIndex));
        }
        else
        {
            return;
        }

        uint hitId;
        try
        {
            hitId = pickingRenderer.Pick(
                (uint)RenderFrameSize.Width, (uint)RenderFrameSize.Height,
                (int)MousePos.X, (int)MousePos.Y,
                Camera.GetView() * Camera.GetProjection(),
                entries);
        }
        catch (Exception e)
        {
            LunaLog.LogError($"Asset picking failed: {e}");
            return;
        }

        if (hitId == PickingRenderer.NoHit)
        {
            selectedMesh = null;
            return;
        }

        selectedMesh = selectedMobyAsset != null
            ? ((int)(hitId >> 16), (int)(hitId & 0xFFFF))
            : (0, (int)hitId);
    }

    /// <summary>Resolves selectedMesh's (bangleIndex, meshIndex) back to the engine-level IMesh
    /// (not the Bliss Model used by PickMeshUnderCursor/rendering) — shared by the info panel,
    /// vertex-edit-mode picking, and its overlay, since all three need VertexDumper/raw vertex
    /// positions rather than the GPU-side mesh.</summary>
    private IMesh? ResolveSelectedMesh()
    {
        if (selectedMesh == null) return null;
        var (bangleIndex, meshIndex) = selectedMesh.Value;

        if (selectedMobyAsset != null)
        {
            var bangles = selectedMobyAsset.Value.Moby.Bangles;
            return bangleIndex >= 0 && bangleIndex < bangles.Count && meshIndex >= 0 && meshIndex < bangles[bangleIndex].Meshes.Count
                ? bangles[bangleIndex].Meshes[meshIndex]
                : null;
        }
        if (selectedTieAsset != null)
        {
            var meshes = selectedTieAsset.Value.Tie.Meshes;
            return meshIndex >= 0 && meshIndex < meshes.Count ? meshes[meshIndex] : null;
        }
        return null;
    }

    /// <summary>CPU screen-space nearest-vertex picking against the selected mesh's raw vertex
    /// positions, rather than a second GPU picking pass — these preview meshes are small enough
    /// (single asset, not a whole level) that projecting every vertex per click is cheap, and it
    /// sidesteps rasterizing sub-pixel point primitives with a click-tolerant hit radius, which a
    /// GPU ID buffer can't easily give without inflating actual triangle geometry.</summary>
    private void PickVertexUnderCursor()
    {
        if (RenderFrameSize.Width <= 0 || RenderFrameSize.Height <= 0) return;

        var mesh = ResolveSelectedMesh();
        if (mesh == null) return;

        float[] positions = mesh.Geometry.GetVertexPositions();
        Matrix4x4 viewProj = Camera.GetView() * Camera.GetProjection();

        int best = -1;
        float bestDistSq = VertexPickPixelRadius * VertexPickPixelRadius;

        for (int i = 0; i < positions.Length / 3; i++)
        {
            var worldPos = new Vector3(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2]);
            if (!TryProjectToScreen(worldPos, viewProj, out Vector2 screen)) continue;

            float dx = screen.X - MousePos.X, dy = screen.Y - MousePos.Y;
            float distSq = dx * dx + dy * dy;
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                best = i;
            }
        }

        if (best >= 0)
            selectedVertexIndex = best;
    }

    private bool TryProjectToScreen(Vector3 worldPos, Matrix4x4 viewProj, out Vector2 screen)
    {
        Vector4 clip = Vector4.Transform(new Vector4(worldPos, 1f), viewProj);
        if (clip.W <= 0.0001f)
        {
            screen = default;
            return false;
        }

        Vector3 ndc = new(clip.X / clip.W, clip.Y / clip.W, clip.Z / clip.W);
        screen = new Vector2(
            (ndc.X * 0.5f + 0.5f) * RenderFrameSize.Width,
            (1f - (ndc.Y * 0.5f + 0.5f)) * RenderFrameSize.Height);
        return true;
    }

    /// <summary>Vertex-edit-mode overlay: one billboard dot per vertex of the selected mesh,
    /// blended with InvertBlendState so each dot always reads against its background regardless
    /// of the underlying texture/lighting. The vertex currently backing the raw-dump panel
    /// (selectedVertexIndex) is drawn larger so it's unambiguous which one is picked.</summary>
    private void DrawVertexOverlay(IMesh mesh)
    {
        float[] positions = mesh.Geometry.GetVertexPositions();
        int vertexCount = positions.Length / 3;
        if (vertexCount == 0) return;

        immediateRenderer.PushBlendState(InvertBlendState);
        immediateRenderer.PushDepthStencilState(DepthStencilStateDescription.DEPTH_ONLY_LESS_EQUAL_READ);

        for (int i = 0; i < vertexCount; i++)
        {
            var worldPos = new Vector3(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2]);
            float pixelRadius = i == selectedVertexIndex ? SelectedVertexPixelRadius : VertexPointPixelRadius;
            float scale = WorldScaleForPixelRadius(worldPos, pixelRadius);
            immediateRenderer.DrawBillboard(worldPos, new Vector2(scale), Bliss.CSharp.Colors.Color.White);
        }

        immediateRenderer.PopDepthStencilState();
        immediateRenderer.PopBlendState();
    }

    // DrawBillboard sizes its quad off GlobalResource.DefaultImmediateRendererTexture's 1x1
    // source rect (half-size = (Width/100)/2 = 0.005 world units per unit of `scale`, since no
    // texture is pushed before calling it here) — back-solve the `scale` that makes the billboard
    // cover pixelRadius screen pixels at this vertex's current distance from the camera, so every
    // dot stays a roughly constant on-screen size regardless of mesh scale or camera zoom.
    private float WorldScaleForPixelRadius(Vector3 worldPos, float pixelRadius)
    {
        float distance = Vector3.Distance(Camera.Position, worldPos);
        float fovYRad = Camera.Fov * (MathF.PI / 180f);
        float worldHalfSize = 2f * distance * MathF.Tan(fovYRad * 0.5f) * (pixelRadius / Math.Max(1, RenderFrameSize.Height));
        return worldHalfSize / 0.005f;
    }

    /// <summary>Right-hand column of the lower split — metadata + raw vertex data for whatever
    /// PickMeshUnderCursor last selected. Resolves back through the engine-level Moby/Tie mesh
    /// list (not the Bliss Model used for picking/rendering) since that's what still has
    /// IMesh.VertexDumper/VertexFormatName and the real Material.</summary>
    private void RenderSelectedMeshPanel()
    {
        ImGui.Text(LM.Get("GUI_Frame_AssetViewer_SelectedMeshTitle"));
        ImGui.Separator();

        if (selectedMesh == null)
        {
            ImGui.TextDisabled(LM.Get("GUI_Frame_AssetViewer_SelectedMeshHint"));
            return;
        }

        var (bangleIndex, meshIndex) = selectedMesh.Value;
        string location = selectedMobyAsset != null ? $"Bangle_{bangleIndex} / Mesh {meshIndex}" : $"Mesh {meshIndex}";
        ImGui.Text(location);

        IMesh? mesh = ResolveSelectedMesh();
        if (mesh == null)
        {
            ImGui.TextDisabled(LM.Get("GUI_Frame_AssetViewer_SelectedMeshStale"));
            return;
        }

        int vertexCount = mesh.Geometry.GetVertexPositions().Length / 3;
        int indexCount = mesh.Geometry.GetIndices().Length;

        ImGui.Text($"Material: {mesh.Material.Name ?? mesh.Material.Id.ToString("X")} (0x{mesh.Material.Id:X})");
        ImGui.Text(LM.Get("GUI_Frame_AssetViewer_VertexFormat", mesh.VertexFormatName ?? LM.Get("GUI_Frame_ShaderBrowser_Unknown")));
        ImGui.Text(LM.Get("GUI_Frame_AssetViewer_VertexIndexCounts", vertexCount, indexCount, indexCount / 3));

        ImGui.Separator();
        ImGui.Checkbox(LM.Get("GUI_Frame_AssetViewer_VertexEditMode"), ref vertexEditMode);
        ImGui.SameLine();
        ImGuiPlus.HelpMarker(LM.Get("GUI_Frame_AssetViewer_VertexEditModeHelp"));

        ImGui.Text(LM.Get("GUI_Frame_AssetViewer_RawVertexSection"));

        if (mesh.VertexDumper == null)
        {
            ImGui.TextDisabled(LM.Get("GUI_Frame_AssetViewer_RawVertexUnavailable"));
            return;
        }

        ImGui.SetNextItemWidth(120);
        ImGui.InputInt(LM.Get("GUI_Frame_AssetViewer_VertexIndex"), ref selectedVertexIndex);
        selectedVertexIndex = Math.Clamp(selectedVertexIndex, 0, Math.Max(0, vertexCount - 1));

        string? dump = mesh.VertexDumper(selectedVertexIndex);
        ImGui.TextUnformatted(dump ?? LM.Get("GUI_Frame_AssetViewer_VertexOutOfRange"));
    }

    /// <summary>Draggable divider between two side-by-side panes — mutates <paramref name="width"/>
    /// (the pane immediately to its left) by the horizontal mouse delta while dragged. Caller
    /// clamps <paramref name="width"/> before using it; this only applies the raw delta.</summary>
    private static void VerticalSplitter(string id, ref float width, float height)
    {
        ImGui.SameLine(0, 0);
        ImGui.Button(id, new Vector2(SplitterThickness, height));
        if (ImGui.IsItemHovered() || ImGui.IsItemActive())
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
        if (ImGui.IsItemActive())
            width += ImGui.GetIO().MouseDelta.X;
        ImGui.SameLine(0, 0);
    }

    /// <summary>Draggable divider between two stacked panes — mutates <paramref name="height"/>
    /// (the pane immediately above it) by the vertical mouse delta while dragged.</summary>
    private static void HorizontalSplitter(string id, ref float height, float width)
    {
        ImGui.Button(id, new Vector2(width, SplitterThickness));
        if (ImGui.IsItemHovered() || ImGui.IsItemActive())
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNs);
        if (ImGui.IsItemActive())
            height += ImGui.GetIO().MouseDelta.Y;
    }

    // Tracks the previous frame's drag state so the shared NoMouse flag (below) is only touched
    // on a rising/falling edge, not every frame.
    private bool wasDragging;

    /// <summary>RMB drags orbit (rotates Position around the fixed Target); MMB drags pan (moves
    /// Position and Target together, so the orbit origin itself relocates instead of just
    /// spinning around it). Both share one method rather than two independent ones because they
    /// also share the ImGuiConfigFlags.NoMouse relative-mouse-mode flag: two separate methods each
    /// unconditionally setting/clearing that flag would have the second one clobber whatever the
    /// first just set whenever only one of the two buttons is actually held.</summary>
    private void CheckCameraDragInput(bool allowGrab)
    {
        ImGuiIOPtr io = ImGui.GetIO();
        bool rotating = rmbghandler.TryGrabMouse(allowGrab);
        bool panning = mmbghandler.TryGrabMouse(allowGrab);
        bool isDragging = rotating || panning;

        // Edge-triggered, not level-triggered: NoMouse is also written by View3D's own drag
        // handling (same relative-mouse-mode pattern, different viewport). Unconditionally
        // clearing it every frame this viewport has nothing grabbed — what this used to do — would
        // cut off a drag in progress over there if both frames tick within the same pass.
        if (isDragging && !wasDragging)
            io.ConfigFlags |= ImGuiConfigFlags.NoMouse;
        else if (!isDragging && wasDragging)
            io.ConfigFlags &= ~ImGuiConfigFlags.NoMouse;
        wasDragging = isDragging;

        if (!isDragging) return;

        Vector2 delta = Input.GetMouseDelta();

        if (rotating)
        {
            Vector2 rot = delta * Program.Settings.CamSensivity;

            // rotateAroundTarget: true swings Position around the fixed Target (real orbit).
            // false — what this used to pass — keeps Position fixed and swings Target instead,
            // which is FPS-style look, not an orbit; that's why this never actually orbited.
            Camera.SetPitch(Camera.GetPitch() - rot.Y, true);
            Camera.SetYaw(Camera.GetYaw() - rot.X, true);
        }

        if (panning)
        {
            // Screen-pixel delta -> world-space delta at the orbit target's own depth (same
            // perspective back-solve as WorldScaleForPixelRadius, without that method's
            // billboard-specific 0.005 constant), so the point under the cursor at drag-start
            // stays roughly under the cursor while dragging, matching typical middle-click-pan
            // tools.
            float distance = Vector3.Distance(Camera.Position, Camera.Target);
            float fovYRad = Camera.Fov * (MathF.PI / 180f);
            float worldUnitsPerPixel = 2f * distance * MathF.Tan(fovYRad * 0.5f) / Math.Max(1, RenderFrameSize.Height);

            // Built by hand instead of Cam3D.MoveRight/MoveUp: those use GetRight() = Cross(Forward,
            // Up) and the raw Up field directly, neither of which is normalized — Up drifts and
            // isn't guaranteed orthogonal to Forward after SetPitch/SetRoll, so pan speed would
            // vary with pitch (shrinking toward zero looking straight up/down) and drift over time.
            // right/up here are a proper orthonormal basis for the current view.
            Vector3 forward = Camera.GetForward();
            Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Camera.Up));
            Vector3 up = Vector3.Normalize(Vector3.Cross(right, forward));

            // Signs make the dragged point track the cursor (drag right -> content follows right,
            // i.e. camera moves left; drag down -> content follows down, i.e. camera moves up) —
            // not runtime-verified; if the pan feels inverted, flip both signs here.
            Vector3 shift = right * (-delta.X * worldUnitsPerPixel) + up * (delta.Y * worldUnitsPerPixel);
            Camera.Position += shift;
            Camera.Target += shift;
        }
    }

    private void UpdateWindowSize()
    {
        if (RenderFrameSize.Width <= 0 || RenderFrameSize.Height <= 0) return;

        if ((int)renderTexture.Width != RenderFrameSize.Width || (int)renderTexture.Height != RenderFrameSize.Height)
            OnResize();
    }

    protected void OnResize()
    {
        renderTexture.Resize((uint)RenderFrameSize.Width, (uint)RenderFrameSize.Height);
        Camera.Resize((uint)RenderFrameSize.Width, (uint)RenderFrameSize.Height);
    }

    /// <summary>Foliage inspector. Read-only and deliberately raw: every number here is either
    /// straight out of the file or one step from it, because foliage is still being reverse
    /// engineered and a prettied-up view would hide the two things worth watching - whether the UVs
    /// really land on quadrant boundaries, and whether the LOD ranges partition the card set.</summary>
    private void RenderFoliageList()
    {
        var level = LunaWindow.Instance.Level;
        if (level == null || level.Foliages.Count == 0)
        {
            ImGui.TextDisabled(LM.Get("GUI_Frame_AssetViewer_NoFoliage"));
            return;
        }

        foreach (var foliage in level.Foliages)
        {
            if (!string.IsNullOrWhiteSpace(assetSearch) &&
                !(foliage.Name ?? "").Contains(assetSearch, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!ImGui.TreeNode($"{foliage.Name}##foliage{foliage.Id}")) continue;

            var meta = foliage.Metadata;
            ImGui.Text($"Sprites: {foliage.Sprites.Count}   Placements: {foliage.Placements.Count}");
            // TextureIndex is shown raw on purpose - it is 0/1 while the real foliage textures are
            // #1286/#1287, and nothing in the files connects them yet (see FoliageMetadata).
            ImGui.Text($"foliageId: {meta.FoliageId}   textureIndex: {meta.TextureIndex} (unresolved)");
            ImGui.Text($"corner data @0x{meta.SpriteCornerOffset:X}   anchor data @0x{meta.SpriteAnchorOffset:X}   (vertices.dat 0x9000)");

            if (ImGui.TreeNode($"Sprite LODs##foliagelod{foliage.Id}"))
            {
                for (int i = 0; i < meta.SpriteLodRanges.Length; i++)
                {
                    var r = meta.SpriteLodRanges[i];
                    if (r.CornerCount <= 0) continue;
                    ImGui.Text($"LOD {i}: corners [{r.CornerBegin}..{r.CornerEnd})  =  {r.SpriteCount} card(s)   distance {r.Distance:0.###}");
                }
                ImGui.TreePop();
            }

            if (ImGui.TreeNode($"Cards##foliagecards{foliage.Id}"))
            {
                // Capped: a card set can run to hundreds and every one draws eight numbers. The
                // list is for spot-checking the decode, not for browsing all of them.
                int shown = 0;
                foreach (var card in foliage.Sprites)
                {
                    if (shown++ >= 64) { ImGui.TextDisabled($"... {foliage.Sprites.Count - 64} more"); break; }
                    ImGui.Text($"[LOD {card.Lod}] anchor ({card.Anchor.X:0.###}, {card.Anchor.Y:0.###}, {card.Anchor.Z:0.###})  packed {card.Packed.Item1:X2} {card.Packed.Item2:X2}");
                    for (int k = 0; k < card.CornerOffsets.Length; k++)
                        ImGui.Text($"    corner {k}: offset ({card.CornerOffsets[k].X:0.###}, {card.CornerOffsets[k].Y:0.###})   uv ({card.Uvs[k].X:0.###}, {card.Uvs[k].Y:0.###})");
                    ImGui.Separator();
                }
                ImGui.TreePop();
            }

            ImGui.TreePop();
        }
    }

}
