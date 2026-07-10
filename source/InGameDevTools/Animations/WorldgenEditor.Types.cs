using ImGuiNET;
using InGameDevTools.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenTK.Graphics.OpenGL4;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.ServerMods;
using Vintagestory.ServerMods.NoObf;

namespace InGameDevTools.Animations;

public sealed partial class DebugWindowManager
{
    private enum WorldgenAssetKind
    {
        Other,
        Deposits,
        BlockPatches,
        Landforms,
        RockStrata
    }

    private sealed class WorldgenAssetEntry
    {
        public WorldgenAssetEntry(IAsset asset)
        {
            Asset = asset;
            Domain = asset.Location.Domain ?? "game";
            AssetPath = asset.Location.Path.Replace('\\', '/');
            Kind = ClassifyWorldgenAssetKind(AssetPath, null);
            SearchText = $"{Domain}:{AssetPath} {KindLabel}";
        }

        public IAsset Asset { get; }
        public string Domain { get; }
        public string AssetPath { get; }
        public WorldgenAssetKind Kind { get; private set; }
        public bool IsContentClassified { get; private set; }
        public string Key => Asset.Location.ToString();
        public string SortKey => $"{KindLabel}:{Domain}:{AssetPath}";
        public string SearchText { get; private set; }
        public string KindLabel => Kind switch
        {
            WorldgenAssetKind.Deposits => "Deposits",
            WorldgenAssetKind.BlockPatches => "Block patches",
            WorldgenAssetKind.Landforms => "Landforms",
            WorldgenAssetKind.RockStrata => "Rock strata",
            _ => "Other"
        };

        public void UpdateKind(JToken? root)
        {
            if (root == null) return;

            Kind = ClassifyWorldgenAssetKind(AssetPath, root);
            IsContentClassified = true;
            SearchText = $"{Domain}:{AssetPath} {KindLabel}";
        }
    }

    private sealed record WorldgenDraftState(string Text, int RowIndex, bool IsValid, string ValidationStatus);

    private readonly record struct WorldgenSurfaceCell(int X, int Z, float Depth);

    private readonly record struct WorldgenVoxelFace(NVector2 A, NVector2 B, NVector2 C, NVector2 D, float Depth, uint Color);

    private enum WorldgenPeekFaceSide
    {
        West,
        East,
        North,
        South
    }

    private readonly record struct WorldgenPeekRegionCacheKey(
        long Seed,
        int OriginChunkX,
        int OriginChunkZ,
        int RegionSizeChunks,
        EnumWorldGenPass UntilPass,
        string DraftFingerprint);

    private readonly record struct WorldgenOreMapRegionCacheKey(string Code, int RegionX, int RegionZ, int NoiseSize);

    private sealed record WorldgenPeekRegionProfile(
        int OriginChunkX,
        int OriginChunkZ,
        int RegionSizeChunks,
        EnumWorldGenPass UntilPass,
        string PassLabel,
        int ColumnsReturned,
        int ChunksReturned,
        int MinHeight,
        int MaxHeight,
        float AverageHeight,
        string SampleSummary,
        int ChunkSize,
        int Width,
        int Depth,
        int MapHeight,
        int[] Heights,
        int[] TopBlockIds,
        int[] ColumnBlockIds)
    {
        public string CleanupSummary { get; init; } = "";
    }

    private sealed record WorldgenLoadedWorldOracleProfile(
        int OriginChunkX,
        int OriginChunkZ,
        int RegionSizeChunks,
        int ChunkSize,
        int Width,
        int Depth,
        int LoadedColumns,
        int MissingColumns,
        int PartialColumns,
        int ComparedCells,
        int MissingCells,
        int HeightMismatchCells,
        int TopBlockMismatchCells,
        int MaxAbsHeightDelta,
        float AverageAbsHeightDelta,
        string Summary,
        string SampleSummary,
        int[] LoadedHeights,
        int[] LoadedTopBlockIds,
        int[] HeightDeltas,
        bool[] Compared,
        bool[] TopBlockMatches);

    private readonly record struct WorldgenPeekCleanupResult(
        int UnloadedColumns,
        int KeptLoadedColumns,
        int FailedColumns,
        string Summary,
        string Details)
    {
        public static WorldgenPeekCleanupResult Empty { get; } = new(0, 0, 0, "Cleanup not run.", "");
    }

    private sealed class WorldgenActivePeek
    {
        private readonly object _gate = new();
        private readonly IWorldManagerAPI _worldManager;
        private readonly WorldgenPeekDraftScope _draftScope;
        private readonly bool _autoGenerateChanged;
        private readonly bool _restoreAutoGenerate;
        private readonly bool _sendChunksChanged;
        private readonly bool _restoreSendChunks;
        private readonly IReadOnlyList<Vec2i> _requestedColumns;
        private readonly IReadOnlyDictionary<Vec2i, bool> _initiallyLoadedColumns;
        private bool _restored;
        private bool _cleanupRun;

        public WorldgenActivePeek(
            long id,
            IWorldManagerAPI worldManager,
            WorldgenPeekDraftScope draftScope,
            string draftFallbackStatus,
            bool autoGenerateChanged,
            bool restoreAutoGenerate,
            bool sendChunksChanged,
            bool restoreSendChunks,
            IReadOnlyList<Vec2i> requestedColumns,
            IReadOnlyDictionary<Vec2i, bool> initiallyLoadedColumns,
            string label)
        {
            Id = id;
            _worldManager = worldManager;
            _draftScope = draftScope;
            DraftFallbackStatus = draftFallbackStatus;
            _autoGenerateChanged = autoGenerateChanged;
            _restoreAutoGenerate = restoreAutoGenerate;
            _sendChunksChanged = sendChunksChanged;
            _restoreSendChunks = restoreSendChunks;
            _requestedColumns = requestedColumns.ToArray();
            _initiallyLoadedColumns = new Dictionary<Vec2i, bool>(initiallyLoadedColumns);
            Label = label;
            StartedUtc = DateTime.UtcNow;
        }

        public long Id { get; }
        public DateTime StartedUtc { get; }
        public string Label { get; }
        public IWorldManagerAPI WorldManager => _worldManager;
        public bool AutoGenerateChanged => _autoGenerateChanged;
        public bool ExpectedAutoGenerate => _restoreAutoGenerate;
        public bool SendChunksChanged => _sendChunksChanged;
        public bool ExpectedSendChunks => _restoreSendChunks;
        public bool DraftApplied => _draftScope.Applied;
        public string DraftStatus => _draftScope.Status;
        public string DraftFallbackStatus { get; }

        public string RestoreLiveState()
        {
            lock (_gate)
            {
                if (_restored)
                {
                    return "Live worldgen state was already restored.";
                }

                _restored = true;
            }

            List<string> failures = [];
            try
            {
                _draftScope.Dispose();
            }
            catch (Exception exception)
            {
                failures.Add($"draft restore failed: {exception.Message}");
            }

            if (_autoGenerateChanged && !TrySetWorldgenAutoGenerateChunks(_worldManager, _restoreAutoGenerate, out string? autoGenerateError))
            {
                failures.Add($"AutoGenerateChunks restore failed: {autoGenerateError}");
            }

            if (_sendChunksChanged && !TrySetWorldgenSendChunks(_worldManager, _restoreSendChunks, out string? sendChunksError))
            {
                failures.Add($"SendChunks restore failed: {sendChunksError}");
            }

            return failures.Count == 0
                ? "Restored live worldgen state."
                : "Restored live worldgen state with failures: " + string.Join("; ", failures);
        }

        public WorldgenPeekCleanupResult CleanupPreviewColumns()
        {
            lock (_gate)
            {
                if (_cleanupRun)
                {
                    return WorldgenPeekCleanupResult.Empty;
                }

                _cleanupRun = true;
            }

            return CleanupWorldgenPeekColumns(_worldManager, _requestedColumns, _initiallyLoadedColumns);
        }

        public WorldgenPeekCleanupResult CleanupReturnedColumns(Dictionary<Vec2i, IServerChunk[]> columns)
        {
            if (columns.Count == 0) return WorldgenPeekCleanupResult.Empty;

            List<Vec2i> returnedColumns = [];
            foreach (Vec2i key in columns.Keys)
            {
                if (_initiallyLoadedColumns.ContainsKey(key))
                {
                    returnedColumns.Add(key);
                }
            }

            return returnedColumns.Count == 0
                ? WorldgenPeekCleanupResult.Empty
                : CleanupWorldgenPeekColumns(_worldManager, returnedColumns, _initiallyLoadedColumns);
        }
    }

    private sealed class WorldgenPeekDraftScope : IDisposable
    {
        private readonly Action? _restore;
        private bool _disposed;

        public WorldgenPeekDraftScope(bool applied, string status, Action? restore)
        {
            Applied = applied;
            Status = status;
            _restore = restore;
        }

        public static WorldgenPeekDraftScope Empty { get; } = new(false, "live engine config", null);

        public bool Applied { get; }
        public string Status { get; }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            _restore?.Invoke();
        }
    }

    private sealed class WorldgenTerrainShapeSampler
    {
        private readonly WorldgenLandformDraft _fallbackDraft;
        private readonly NewNormalizedSimplexFractalNoise? _terrainNoise;
        private readonly SimplexNoise? _distortX;
        private readonly SimplexNoise? _distortZ;
        private readonly double[] _terrainOctaves;
        private readonly double[] _terrainOctaveThresholds;
        private readonly float[] _terrainYThresholds;
        private readonly int _mapSizeY;
        private readonly double _verticalFrequency;

        public WorldgenTerrainShapeSampler(WorldgenLandformDraft fallbackDraft, string status)
        {
            _fallbackDraft = fallbackDraft;
            _terrainOctaves = [];
            _terrainOctaveThresholds = [];
            _terrainYThresholds = [];
            Status = status;
        }

        public WorldgenTerrainShapeSampler(
            WorldgenLandformDraft fallbackDraft,
            NewNormalizedSimplexFractalNoise terrainNoise,
            SimplexNoise distortX,
            SimplexNoise distortZ,
            double[] terrainOctaves,
            double[] terrainOctaveThresholds,
            float[] terrainYThresholds,
            int mapSizeY,
            double verticalFrequency,
            string status)
        {
            _fallbackDraft = fallbackDraft;
            _terrainNoise = terrainNoise;
            _distortX = distortX;
            _distortZ = distortZ;
            _terrainOctaves = terrainOctaves;
            _terrainOctaveThresholds = terrainOctaveThresholds;
            _terrainYThresholds = terrainYThresholds;
            _mapSizeY = mapSizeY;
            _verticalFrequency = verticalFrequency;
            Status = status;
        }

        public string Status { get; }

        public float SampleHeight(long seed, float worldX, float worldZ)
        {
            if (_terrainNoise == null || _distortX == null || _distortZ == null || _terrainYThresholds.Length < _mapSizeY)
            {
                return _fallbackDraft.SampleHeight(seed, worldX, worldZ);
            }

            try
            {
                return SampleEngineHeight(worldX, worldZ);
            }
            catch
            {
                return _fallbackDraft.SampleHeight(seed, worldX, worldZ);
            }
        }

        private float SampleEngineHeight(float worldX, float worldZ)
        {
            int mapSizeYm2 = Math.Max(1, _mapSizeY - 2);
            WorldgenVectorXZ distortion = NewDistortionNoise(worldX, worldZ);
            WorldgenVectorXZ terrainDistortion = ApplyIsotropicDistortionThreshold(distortion * 4.0, 40.0, 763.6753236814714);
            NewNormalizedSimplexFractalNoise.ColumnNoise column = _terrainNoise!.ForColumn(
                _verticalFrequency,
                _terrainOctaves,
                _terrainOctaveThresholds,
                worldX + terrainDistortion.X,
                worldZ + terrainDistortion.Z);

            double boundMin = column.BoundMin;
            double boundMax = column.BoundMax;
            int topSolid = 0;
            for (int y = 1; y <= mapSizeYm2; y++)
            {
                double threshold = _terrainYThresholds[y];
                if (threshold <= boundMin)
                {
                    topSolid = y;
                    continue;
                }

                if (threshold >= boundMax)
                {
                    break;
                }

                double inverseThreshold = 0.0 - NormalizedSimplexNoise.NoiseValueCurveInverse(threshold);
                if (column.NoiseSign(y, inverseThreshold) > 0.0)
                {
                    topSolid = y;
                }
            }

            return Math.Clamp(topSolid / (float)Math.Max(1, mapSizeYm2), 0f, 1f);
        }

        private WorldgenVectorXZ NewDistortionNoise(double worldX, double worldZ)
        {
            double x = 0.0;
            double z = 0.0;
            SimplexNoise.NoiseFairWarpVector(_distortX!, _distortZ!, worldX / 400.0, worldZ / 400.0, out x, out z);
            return new WorldgenVectorXZ(x, z);
        }

        private static WorldgenVectorXZ ApplyIsotropicDistortionThreshold(WorldgenVectorXZ dist, double threshold, double maximum)
        {
            double squaredDistance = dist.X * dist.X + dist.Z * dist.Z;
            double squaredThreshold = threshold * threshold;
            if (squaredDistance <= squaredThreshold)
            {
                return new WorldgenVectorXZ(0.0, 0.0);
            }

            double distanceFactor = (squaredDistance - squaredThreshold) / squaredDistance;
            double squaredMaximum = maximum * maximum;
            double maximumFactor = squaredMaximum / (squaredMaximum - squaredThreshold);
            double factor = distanceFactor * maximumFactor;
            double factorSquared = factor * factor;
            double range = maximum - threshold;
            return dist * (factorSquared * (range / maximum));
        }
    }

    private readonly record struct WorldgenVectorXZ(double X, double Z)
    {
        public static WorldgenVectorXZ operator *(WorldgenVectorXZ vector, double multiplier)
        {
            return new WorldgenVectorXZ(vector.X * multiplier, vector.Z * multiplier);
        }
    }

    private readonly record struct WorldgenClimateSample(float TemperatureCelsius, float Rain, float Forest, float Shrub, bool HasShrub, float Fertility, bool HasFertility = false);

    private readonly record struct WorldgenValueRange(float? Min, float? Max)
    {
        public bool IsSet => Min.HasValue || Max.HasValue;

        public bool Contains(float value)
        {
            return (!Min.HasValue || value >= Min.Value) &&
                (!Max.HasValue || value <= Max.Value);
        }

        public float RejectionDistance(float value)
        {
            if (Min.HasValue && value < Min.Value) return Min.Value - value;
            if (Max.HasValue && value > Max.Value) return value - Max.Value;
            return 0f;
        }
    }

    private readonly record struct WorldgenBlockPatchDraft(
        WorldgenValueRange Temperature,
        WorldgenValueRange Rain,
        WorldgenValueRange Forest,
        WorldgenValueRange Shrub,
        WorldgenValueRange Fertility,
        float Chance)
    {
        public static WorldgenBlockPatchDraft FromJson(JObject row)
        {
            return new WorldgenBlockPatchDraft(
                ReadRange(row, "minTemp", "maxTemp"),
                ReadRange(row, "minRain", "maxRain"),
                ReadRange(row, "minForest", "maxForest"),
                ReadRange(row, "minShrub", "maxShrub"),
                ReadRange(row, "minFertility", "maxFertility"),
                TryReadJsonFloat(row["chance"], out float chance) ? chance : 1f);
        }

        public bool IsSuitable(WorldgenClimateSample sample)
        {
            if (!Temperature.Contains(sample.TemperatureCelsius)) return false;
            if (!Rain.Contains(sample.Rain)) return false;
            if (!Forest.Contains(sample.Forest)) return false;
            if (sample.HasShrub && !Shrub.Contains(sample.Shrub)) return false;
            if (sample.HasFertility && !Fertility.Contains(sample.Fertility)) return false;
            return true;
        }

        public float RejectionStrength(WorldgenClimateSample sample)
        {
            float temp = Temperature.RejectionDistance(sample.TemperatureCelsius) / 60f;
            float rain = Rain.RejectionDistance(sample.Rain);
            float forest = Forest.RejectionDistance(sample.Forest);
            float shrub = sample.HasShrub ? Shrub.RejectionDistance(sample.Shrub) : 0f;
            float fertility = sample.HasFertility ? Fertility.RejectionDistance(sample.Fertility) : 0f;
            return Math.Clamp(temp + rain + forest + shrub + fertility, 0f, 1f);
        }

        private static WorldgenValueRange ReadRange(JObject row, string minName, string maxName)
        {
            float? min = TryReadJsonFloat(row[minName], out float parsedMin) ? parsedMin : null;
            float? max = TryReadJsonFloat(row[maxName], out float parsedMax) ? parsedMax : null;
            return new WorldgenValueRange(min, max);
        }
    }

    private sealed class WorldgenRockStrataSampler
    {
        private readonly long _seed;
        private readonly int _mapSizeY;
        private readonly WorldgenRockStrataDraft _draft;
        private readonly MapLayerCustomPerlin? _engineLayer;

        private WorldgenRockStrataSampler(long seed, int mapSizeY, WorldgenRockStrataDraft draft, MapLayerCustomPerlin? engineLayer)
        {
            _seed = seed;
            _mapSizeY = mapSizeY;
            _draft = draft;
            _engineLayer = engineLayer;
        }

        public static WorldgenRockStrataSampler CreateEngine(long seed, int rowIndex, int mapSizeY, WorldgenRockStrataDraft draft)
        {
            if (!draft.IsUsable)
            {
                throw new InvalidOperationException("rock-strata draft arrays are not usable");
            }

            double[] amplitudes = draft.Amplitudes.Select(value => value * mapSizeY).ToArray();
            double[] frequencies = draft.Frequencies.Select(value => value / Math.Max(1, TerraGenConfig.rockStrataOctaveScale)).ToArray();
            double[] thresholds = draft.Thresholds.Select(value => value * mapSizeY).ToArray();
            return new WorldgenRockStrataSampler(
                seed,
                mapSizeY,
                draft,
                new MapLayerCustomPerlin(seed + 23423 + Math.Max(0, rowIndex), amplitudes, frequencies, thresholds));
        }

        public static WorldgenRockStrataSampler CreateFallback(long seed, int mapSizeY, WorldgenRockStrataDraft draft)
        {
            return new WorldgenRockStrataSampler(seed, mapSizeY, draft, null);
        }

        public float SampleThickness(float worldX, float worldZ)
        {
            int scale = Math.Max(1, TerraGenConfig.rockStrataScale);
            int sampleX = (int)MathF.Floor(worldX / scale);
            int sampleZ = (int)MathF.Floor(worldZ / scale);
            if (_engineLayer != null)
            {
                return Math.Max(0f, _engineLayer.GenLayer(sampleX, sampleZ, 1, 1)[0]);
            }

            return SampleFallbackThickness(worldX, worldZ);
        }

        private float SampleFallbackThickness(float worldX, float worldZ)
        {
            double total = 0.0;
            int count = Math.Min(_draft.Amplitudes.Length, Math.Min(_draft.Frequencies.Length, _draft.Thresholds.Length));
            if (count <= 0) return 0f;

            for (int index = 0; index < count; index++)
            {
                double amplitude = _draft.Amplitudes[index] * _mapSizeY;
                double frequency = _draft.Frequencies[index] / Math.Max(1, TerraGenConfig.rockStrataOctaveScale);
                double threshold = _draft.Thresholds[index] * _mapSizeY;
                double value = ValueNoise01(_seed, worldX * frequency, worldZ * frequency, index) * amplitude;
                total += Math.Max(0.0, value - threshold);
            }

            return (float)Math.Max(0.0, total);
        }

        private static float ValueNoise01(long seed, double x, double z, int octave)
        {
            int x0 = (int)Math.Floor(x);
            int z0 = (int)Math.Floor(z);
            double fx = x - x0;
            double fz = z - z0;
            double sx = fx * fx * (3.0 - 2.0 * fx);
            double sz = fz * fz * (3.0 - 2.0 * fz);

            double a = Lerp(Hash01(seed, x0, z0, octave), Hash01(seed, x0 + 1, z0, octave), sx);
            double b = Lerp(Hash01(seed, x0, z0 + 1, octave), Hash01(seed, x0 + 1, z0 + 1, octave), sx);
            return (float)Lerp(a, b, sz);
        }

        private static double Lerp(double a, double b, double t) => a + (b - a) * t;

        private static double Hash01(long seed, int x, int z, int octave)
        {
            unchecked
            {
                ulong hash = (ulong)seed;
                hash ^= (ulong)(uint)x * 0x9E3779B185EBCA87UL;
                hash ^= (ulong)(uint)z * 0xC2B2AE3D27D4EB4FUL;
                hash ^= (ulong)(uint)octave * 0x165667B19E3779F9UL;
                hash ^= hash >> 33;
                hash *= 0xff51afd7ed558ccdUL;
                hash ^= hash >> 33;
                hash *= 0xc4ceb9fe1a85ec53UL;
                hash ^= hash >> 33;
                return (hash & 0x00FFFFFFUL) / (double)0x01000000UL;
            }
        }
    }

    private readonly record struct WorldgenRockStrataDraft(
        string? BlockCode,
        string? HexColor,
        string? RockGroup,
        string? GenDir,
        double[] Amplitudes,
        double[] Frequencies,
        double[] Thresholds)
    {
        public bool IsUsable => Amplitudes.Length > 0 &&
            Amplitudes.Length == Frequencies.Length &&
            Amplitudes.Length == Thresholds.Length;

        public static WorldgenRockStrataDraft FromJson(JObject row)
        {
            return new WorldgenRockStrataDraft(
                row["blockcode"]?.ToString(),
                row["hexcolor"]?.ToString(),
                row["rockGroup"]?.ToString(),
                row["genDir"]?.ToString(),
                ReadDoubleArray(row["amplitudes"] as JArray),
                ReadDoubleArray(row["frequencies"] as JArray),
                ReadDoubleArray(row["thresholds"] as JArray));
        }

        private static double[] ReadDoubleArray(JArray? array)
        {
            if (array == null || array.Count == 0) return [];

            List<double> values = new(array.Count);
            foreach (JToken token in array)
            {
                if (TryReadJsonFloat(token, out float value))
                {
                    values.Add(value);
                }
            }

            return values.ToArray();
        }
    }

    private readonly record struct WorldgenLandformDraft(
        string? Code,
        string? HexColor,
        float[] Octaves,
        float[] OctaveThresholds,
        float[] YKeyPositions,
        float[] YKeyThresholds)
    {
        public bool IsUsable => Octaves.Length > 0 && YKeyPositions.Length > 0 && YKeyThresholds.Length > 0;

        public static WorldgenLandformDraft FromJson(JObject row)
        {
            return new WorldgenLandformDraft(
                row["code"]?.ToString(),
                row["hexcolor"]?.ToString(),
                ReadFloatArray(row["terrainOctaves"] as JArray),
                ReadFloatArray(row["terrainOctaveThresholds"] as JArray),
                ReadFloatArray(row["terrainYKeyPositions"] as JArray),
                ReadFloatArray(row["terrainYKeyThresholds"] as JArray));
        }

        public float SampleHeight(long seed, float worldX, float worldZ)
        {
            float terrainNoise = SampleTerrainNoise(seed, worldX, worldZ);
            return Math.Clamp(ResolveYPosition(terrainNoise), 0f, 1f);
        }

        private float SampleTerrainNoise(long seed, float worldX, float worldZ)
        {
            float total = 0f;
            float totalWeight = 0f;
            for (int i = 0; i < Octaves.Length; i++)
            {
                float weight = Octaves[i];
                if (Math.Abs(weight) < 0.0001f) continue;

                float threshold = i < OctaveThresholds.Length ? OctaveThresholds[i] : 0f;
                double frequency = Math.Pow(2.0, i) / 4096.0;
                float value = ValueNoise01(seed, worldX * frequency, worldZ * frequency, i);
                if (threshold > 0f)
                {
                    value = Math.Clamp((value - threshold) / Math.Max(0.0001f, 1f - threshold), 0f, 1f);
                }

                total += value * weight;
                totalWeight += Math.Abs(weight);
            }

            if (totalWeight <= 0.0001f)
            {
                return YKeyThresholds.Length > 0 ? Math.Clamp(YKeyThresholds[0], 0f, 1f) : 0.5f;
            }

            return Math.Clamp(total / totalWeight, 0f, 1f);
        }

        private float ResolveYPosition(float terrainNoise)
        {
            int count = Math.Min(YKeyPositions.Length, YKeyThresholds.Length);
            if (count <= 0) return terrainNoise;
            if (count == 1) return YKeyPositions[0];

            for (int i = 0; i < count - 1; i++)
            {
                float thresholdA = YKeyThresholds[i];
                float thresholdB = YKeyThresholds[i + 1];
                float min = Math.Min(thresholdA, thresholdB);
                float max = Math.Max(thresholdA, thresholdB);
                if (terrainNoise < min || terrainNoise > max) continue;

                float denominator = thresholdB - thresholdA;
                float t = Math.Abs(denominator) < 0.0001f
                    ? 0f
                    : (terrainNoise - thresholdA) / denominator;
                return YKeyPositions[i] + (YKeyPositions[i + 1] - YKeyPositions[i]) * Math.Clamp(t, 0f, 1f);
            }

            int nearestIndex = 0;
            float nearestDistance = Math.Abs(terrainNoise - YKeyThresholds[0]);
            for (int i = 1; i < count; i++)
            {
                float distance = Math.Abs(terrainNoise - YKeyThresholds[i]);
                if (distance >= nearestDistance) continue;
                nearestDistance = distance;
                nearestIndex = i;
            }

            return YKeyPositions[nearestIndex];
        }

        private static float[] ReadFloatArray(JArray? array)
        {
            if (array == null || array.Count == 0) return [];

            List<float> values = new(array.Count);
            foreach (JToken token in array)
            {
                if (TryReadJsonFloat(token, out float value))
                {
                    values.Add(value);
                }
            }

            return values.ToArray();
        }

        private static float ValueNoise01(long seed, double x, double z, int octave)
        {
            int x0 = (int)Math.Floor(x);
            int z0 = (int)Math.Floor(z);
            double fx = x - x0;
            double fz = z - z0;
            double sx = fx * fx * (3.0 - 2.0 * fx);
            double sz = fz * fz * (3.0 - 2.0 * fz);

            double a = Lerp(Hash01(seed, x0, z0, octave), Hash01(seed, x0 + 1, z0, octave), sx);
            double b = Lerp(Hash01(seed, x0, z0 + 1, octave), Hash01(seed, x0 + 1, z0 + 1, octave), sx);
            return (float)Lerp(a, b, sz);
        }

        private static double Lerp(double a, double b, double t) => a + (b - a) * t;

        private static double Hash01(long seed, int x, int z, int octave)
        {
            unchecked
            {
                ulong hash = (ulong)seed;
                hash ^= (ulong)(uint)x * 0x9E3779B185EBCA87UL;
                hash ^= (ulong)(uint)z * 0xC2B2AE3D27D4EB4FUL;
                hash ^= (ulong)(uint)octave * 0x165667B19E3779F9UL;
                hash ^= hash >> 33;
                hash *= 0xff51afd7ed558ccdUL;
                hash ^= hash >> 33;
                hash *= 0xc4ceb9fe1a85ec53UL;
                hash ^= hash >> 33;
                return (hash & 0x00FFFFFFUL) / (double)0x01000000UL;
            }
        }
    }

    private readonly record struct WorldgenPreviewRasterCacheKey(
        int Mode,
        string Context,
        long Seed,
        int StartX,
        int StartZ,
        int EndX,
        int EndZ,
        int CellsX,
        int CellsZ);
}
