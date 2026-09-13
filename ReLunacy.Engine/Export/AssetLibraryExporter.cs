using System.Numerics;
using System.Text.Json;
using ReLunacy.Engine.Assets.Geometry;
using ReLunacy.Engine.Assets.Interfaces;
using ReLunacy.Engine.Scene;

namespace ReLunacy.Engine.Export;

public readonly record struct AssetLibraryExportOptions(
    bool ExportReference,
    bool ExportMobys,
    bool ExportTies,
    bool ExportUFrags);

/// <summary>
/// Exports a loaded level as an external world-building asset library rather than one monolithic
/// scene. The full level is kept under Reference/ for visual context, while reusable asset models
/// are written as one self-contained GLB per asset/chunk so tools such as Promethean can index and
/// place them independently.
///
/// Ties and Mobys are de-duplicated by their source asset ID. UFrags are intentionally exported one
/// file per terrain chunk because they are baked level geometry, not instanced assets. A manifest
/// keeps the original game IDs, source names, usage counts and bounds so an external editor can
/// round-trip placements without having to infer identity from filenames.
/// </summary>
public static class AssetLibraryExporter
{
    private const float UFragQuantisation = 256f;

    public static void Export(
        string outputDirectory,
        string levelName,
        EntityManager entityManager,
        AssetLibraryExportOptions options,
        Action<float>? onProgress = null)
    {
        Directory.CreateDirectory(outputDirectory);

        var ties = options.ExportTies
            ? entityManager.Regions
                .SelectMany(r => r.Zones)
                .SelectMany(z => z.TieInstances.Entities)
                .OfType<EntityTie>()
                .GroupBy(e => e.BaseTie.Id)
                .Select(g => (Asset: g.First().BaseTie, InstanceCount: g.Count()))
                .ToList()
            : [];

        var mobys = options.ExportMobys
            ? entityManager.Regions
                .SelectMany(r => r.MobyInstances.Entities)
                .OfType<EntityMoby>()
                .GroupBy(e => e.BaseMoby.Id)
                .Select(g => (Asset: g.First().BaseMoby, InstanceCount: g.Count()))
                .ToList()
            : [];

        var ufrags = options.ExportUFrags
            ? entityManager.Regions
                .SelectMany(r => r.Zones.SelectMany(z =>
                    z.UFrags.Entities.OfType<EntityUFrag>().Select(e => (ZoneName: z.ZoneName, Entity: e))))
                .ToList()
            : [];

        int totalJobs = (options.ExportReference ? 1 : 0) + ties.Count + mobys.Count + ufrags.Count;
        int completedJobs = 0;
        void ReportProgress()
        {
            completedJobs++;
            onProgress?.Invoke(totalJobs == 0 ? 1f : completedJobs / (float)totalJobs);
        }

        var entries = new List<AssetManifestEntry>();
        string? referenceRelativePath = null;
        string safeLevelName = ExportPaths.SanitizeFileName(levelName);

        if (options.ExportReference)
        {
            string referenceDirectory = Path.Combine(outputDirectory, "Reference");
            Directory.CreateDirectory(referenceDirectory);
            string referencePath = Path.Combine(referenceDirectory, $"{safeLevelName}_FULL.glb");

            LevelExporter.Export(
                referencePath,
                levelName,
                entityManager,
                new LevelExportOptions(options.ExportMobys, options.ExportTies, options.ExportUFrags));

            referenceRelativePath = RelativePath(outputDirectory, referencePath);
            ReportProgress();
        }

        if (options.ExportTies)
        {
            string tieDirectory = Path.Combine(outputDirectory, "TIE");
            Directory.CreateDirectory(tieDirectory);

            foreach (var (tie, instanceCount) in ties)
            {
                string name = StableAssetName("Tie", tie.Id, tie.Name);
                string filePath = Path.Combine(tieDirectory, $"{name}.glb");
                GltfExporter.Export(filePath, name, [new MeshGroup(name, tie.Meshes)]);

                var (center, radius) = tie.GetBoundingSphere();
                entries.Add(new AssetManifestEntry(
                    "TIE",
                    HexId(tie.Id),
                    tie.Name ?? name,
                    RelativePath(outputDirectory, filePath),
                    instanceCount,
                    Vec3(center),
                    radius,
                    null));

                ReportProgress();
            }
        }

        if (options.ExportMobys)
        {
            string mobyDirectory = Path.Combine(outputDirectory, "Moby");
            Directory.CreateDirectory(mobyDirectory);

            foreach (var (moby, instanceCount) in mobys)
            {
                string name = StableAssetName("Moby", moby.Id, moby.Name);
                string filePath = Path.Combine(mobyDirectory, $"{name}.glb");
                var groups = moby.Bangles
                    .Select((bangle, i) => new MeshGroup(
                        string.IsNullOrWhiteSpace(bangle.Name) ? $"Bangle_{i}" : bangle.Name!,
                        bangle.Meshes))
                    .ToList();

                GltfExporter.Export(filePath, name, groups, moby.Skeleton);

                var (center, radius) = moby.GetBoundingSphere();
                entries.Add(new AssetManifestEntry(
                    "Moby",
                    HexId(moby.Id),
                    moby.Name ?? name,
                    RelativePath(outputDirectory, filePath),
                    instanceCount,
                    Vec3(center),
                    radius,
                    null));

                ReportProgress();
            }
        }

        if (options.ExportUFrags)
        {
            string ufragDirectory = Path.Combine(outputDirectory, "UFrag");
            Directory.CreateDirectory(ufragDirectory);

            for (int i = 0; i < ufrags.Count; i++)
            {
                var (zoneName, entity) = ufrags[i];
                IUFrag ufrag = entity.UFrag;

                // UFrags are baked chunks rather than reusable instanced assets, so the entity index
                // is part of the filename even when two chunks happen to expose the same source ID.
                string sourceName = string.IsNullOrWhiteSpace(ufrag.Name) ? entity.Name : ufrag.Name!;
                string name = $"{StableAssetName("UFrag", ufrag.Id, sourceName)}_{i:D4}";
                string filePath = Path.Combine(ufragDirectory, $"{name}.glb");

                var adapter = new StandaloneUFragMeshAdapter(ufrag, name);
                GltfExporter.Export(filePath, name, [new MeshGroup(name, [adapter])]);

                // The standalone model is authored around the chunk's local origin, while the file's
                // bounding centre is world-space. Store the corresponding local centre in the asset
                // record; the original placement anchor is kept separately for reconstruction.
                Vector3 localCenter = ufrag.GetBoundingCenter() - ufrag.GetAnchor();
                entries.Add(new AssetManifestEntry(
                    "UFrag",
                    HexId(ufrag.Id),
                    sourceName,
                    RelativePath(outputDirectory, filePath),
                    1,
                    Vec3(localCenter),
                    ufrag.GetBoundingRadius(),
                    zoneName,
                    Vec3(ufrag.GetAnchor())));

                ReportProgress();
            }
        }

        var manifest = new AssetLibraryManifest(
            1,
            levelName,
            DateTime.UtcNow.ToString("O"),
            referenceRelativePath,
            entries);

        string manifestPath = Path.Combine(outputDirectory, "assets.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true
        }));

        if (totalJobs == 0)
            onProgress?.Invoke(1f);
    }

    private static string StableAssetName(string prefix, ulong id, string? sourceName)
    {
        string candidate = string.IsNullOrWhiteSpace(sourceName)
            ? $"{prefix}_{id:X}"
            : ExportPaths.SanitizeFileName(sourceName);

        if (string.IsNullOrWhiteSpace(candidate))
            candidate = prefix;

        return $"{candidate}_{id:X}";
    }

    private static string RelativePath(string root, string filePath) =>
        Path.GetRelativePath(root, filePath).Replace('\\', '/');

    private static string HexId(ulong id) => $"0x{id:X}";
    private static float[] Vec3(Vector3 value) => [value.X, value.Y, value.Z];

    private sealed record AssetLibraryManifest(
        int SchemaVersion,
        string LevelName,
        string GeneratedAtUtc,
        string? Reference,
        IReadOnlyList<AssetManifestEntry> Assets);

    private sealed record AssetManifestEntry(
        string Type,
        string Id,
        string Name,
        string File,
        int InstanceCount,
        float[] BoundingCenter,
        float BoundingRadius,
        string? Zone,
        float[]? OriginalAnchor = null);

    /// <summary>
    /// IUFrag stores local vertex coordinates in x256 fixed-point space. Whole-level export applies
    /// EntityUFrag's 1/256 node scale, but a standalone library asset has no placement node, so this
    /// adapter performs the exact same conversion in the vertex data and leaves the model at origin.
    /// </summary>
    private sealed class StandaloneUFragMeshAdapter(IUFrag ufrag, string name) : IMesh, IGeometry
    {
        public IGeometry Geometry => this;
        public IMaterial Material => ufrag.Material;
        public string? Name => name;
        public string? VertexFormatName => null;
        public Func<int, string?>? VertexDumper => null;

        public ulong Id => ufrag.Id;
        public bool IsLoaded => ufrag.IsLoaded;

        public float[] GetVertexPositions()
        {
            var source = ufrag.GetVertexPositions();
            var result = new float[source.Length];
            for (int i = 0; i < source.Length; i++)
                result[i] = source[i] / UFragQuantisation;
            return result;
        }

        public float[] GetTextureCoordinates() => ufrag.GetTextureCoordinates();
        public float[]? GetNormals() => ufrag.GetNormals();
        public float[]? GetLightmapUVs() => ufrag.GetLightmapUVs();

        public float[]? GetTangents()
        {
            var positions = GetVertexPositions();
            var indices = ufrag.GetIndices();
            var normals = ufrag.GetNormals() ?? GeometryMath.ComputeNormals(positions, indices);
            return ufrag.GetTangents() ?? GeometryMath.ComputeTangents(
                positions,
                ufrag.GetTextureCoordinates(),
                normals,
                indices,
                null);
        }

        public float[]? GetVertexAlphaCandidates() => ufrag.GetVertexAlphaCandidates();
        public uint[] GetIndices() => ufrag.GetIndices();

        // These bounds are expressed in standalone/local units, matching GetVertexPositions().
        public Vector3 GetBoundingCenter() => ufrag.GetBoundingCenter() - ufrag.GetAnchor();
        public float GetBoundingRadius() => ufrag.GetBoundingRadius();

        public int[]? GetJointIndices() => null;
        public float[]? GetJointWeights() => null;
    }
}
