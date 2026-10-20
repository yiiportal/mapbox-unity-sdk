using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Mapbox.BaseModule.Data.DataFetchers;
using Mapbox.BaseModule.Data.Tasks;
using Mapbox.BaseModule.Data.Tiles;
using Mapbox.BaseModule.Data.Vector2d;
using Mapbox.BaseModule.Map;
using Mapbox.BaseModule.Unity;
using Mapbox.BaseModule.Utilities;
using UnityEngine;
using UnityEngine.Networking;

namespace Mapbox.LandmarksModule
{
    public sealed class LandmarkArLoader : MonoBehaviour
    {
        private const int RequestTimeoutSeconds = 20;
        private const float DefaultTableFootprintMeters = 0.75f;

        public sealed class LandmarkArTileCache
        {
            internal readonly Dictionary<CanonicalTileId, byte[]> Tiles = new();

            public void Clear() => Tiles.Clear();
        }

        public sealed class LandmarkArResolution
        {
            internal GltfGenerationTaskResult Result;
            internal GameObject LoadRoot;
            internal GltfVisualizer Visualizer;
            internal CanonicalTileId TileId;
            internal double Latitude;

            public IReadOnlyList<LandmarkArCandidate> GetCandidates(double latitude, double longitude)
            {
                var candidates = new List<LandmarkArCandidate>();
                if (Result?.Anchors == null)
                {
                    return candidates;
                }

                var target = Conversions.LatitudeLongitudeToInTile01(
                    new LatitudeLongitude(latitude, longitude), TileId);
                float tileEdgeMeters = TileEdgeMeters(latitude);
                foreach (var anchor in Result.Anchors)
                {
                    if (anchor.Transform == null || string.IsNullOrEmpty(anchor.LandmarkId))
                    {
                        continue;
                    }

                    Bounds bounds = CalculateBounds(anchor.Transform.gameObject);
                    candidates.Add(new LandmarkArCandidate(
                        anchor.LandmarkId,
                        anchor.GroundUv,
                        anchor.FootprintRings,
                        bounds.size.y,
                        Mathf.Max(bounds.size.x, bounds.size.z) * tileEdgeMeters,
                        anchor.Transform.GetComponentsInChildren<Renderer>().Length,
                        target,
                        tileEdgeMeters));
                }

                return candidates;
            }

            public GameObject ExtractForPrecheck(string landmarkId, Transform parent) =>
                ExtractLandmark(Result, landmarkId, TileId, Latitude, parent, transferImport: false);

            public float TileEdgeMeters(double latitude) =>
                Conversions.TileEdgeSizeInMercator(TileId) * Mathf.Cos((float)latitude * Mathf.Deg2Rad);
        }

        public IEnumerator Resolve(
            double latitude,
            double longitude,
            string accessToken,
            Material material,
            LandmarkArTileCache tileCache,
            Action<LandmarkArResolution> callback)
        {
            if (string.IsNullOrEmpty(accessToken) || material == null || callback == null)
            {
                callback?.Invoke(null);
                yield break;
            }

            var tileId = Conversions.LatitudeLongitudeToTileId(latitude, longitude, LandmarksLayerModule.LandmarkDataZoom).Canonical;
            byte[] tileData = null;
            yield return RequestTile(tileId, accessToken, tileCache, downloadedData => tileData = downloadedData);
            if (tileData == null || tileData.Length == 0)
            {
                callback(null);
                yield break;
            }

            var data = new BuildingData { TileId = tileId, Data = tileData };
            var loadRoot = new GameObject("LandmarkResolveRoot");
            var context = new UnityContext { RuntimeGenerationRoot = loadRoot.transform };
            var visualizer = new GltfVisualizer(context, null, material);
            GltfGenerationTaskResult result = null;
            Task generation = visualizer.Generate(data, loadedResult => result = loadedResult);
            while (!generation.IsCompleted)
            {
                yield return null;
            }

            var resolution = new LandmarkArResolution
            {
                Result = result,
                LoadRoot = loadRoot,
                Visualizer = visualizer,
                TileId = tileId,
                Latitude = latitude
            };
            if (result?.ResultType != TaskResultType.Success)
            {
                Debug.LogWarning($"[LandmarkArLoader] Tile {tileId} did not produce candidates ({result?.ResultType}).");
            }

            callback(resolution);
            yield return ReleaseResolution(resolution);
        }

        public IEnumerator Load(
            double latitude,
            double longitude,
            string landmarkId,
            string accessToken,
            Material material,
            Transform parent,
            Action<GameObject> callback)
        {
            yield return Load(latitude, longitude, landmarkId, accessToken, material, parent, null, callback);
        }

        public IEnumerator Load(
            double latitude,
            double longitude,
            string landmarkId,
            string accessToken,
            Material material,
            Transform parent,
            LandmarkArTileCache tileCache,
            Action<GameObject> callback)
        {
            if (string.IsNullOrEmpty(landmarkId) || string.IsNullOrEmpty(accessToken) || material == null || parent == null)
            {
                callback(null);
                yield break;
            }

            var tileId = Conversions.LatitudeLongitudeToTileId(latitude, longitude, LandmarksLayerModule.LandmarkDataZoom).Canonical;
            byte[] tileData = null;
            yield return RequestTile(tileId, accessToken, tileCache, downloadedData => tileData = downloadedData);
            if (tileData == null || tileData.Length == 0)
            {
                callback(null);
                yield break;
            }

            var data = new BuildingData { TileId = tileId, Data = tileData };
            var loadRoot = new GameObject("LandmarkARLoadRoot");
            var context = new UnityContext { RuntimeGenerationRoot = loadRoot.transform };
            var visualizer = new GltfVisualizer(context, null, material);
            GltfGenerationTaskResult result = null;
            Task generation = visualizer.Generate(data, loadedResult => result = loadedResult);
            while (!generation.IsCompleted)
            {
                yield return null;
            }

            GameObject model = null;
            if (result?.ResultType == TaskResultType.Success)
            {
                model = ExtractLandmark(result, landmarkId, tileId, latitude, loadRoot.transform, transferImport: true);
            }
            else
            {
                Debug.LogWarning($"[LandmarkArLoader] Tile {tileId} did not produce a landmark ({result?.ResultType}).");
            }

            visualizer.OnDestroy();
            Destroy(loadRoot);
            if (model == null)
            {
                yield return null;
                if (result != null)
                {
                    result.Import?.Dispose();
                    result.Import = null;
                }
            }

            if (model != null)
            {
                model.transform.SetParent(parent, false);
            }

            callback(model);
        }

        private static IEnumerator ReleaseResolution(LandmarkArResolution resolution)
        {
            resolution.Visualizer?.OnDestroy();
            if (resolution.LoadRoot != null)
            {
                Destroy(resolution.LoadRoot);
            }

            yield return null;
            if (resolution.Result != null)
            {
                resolution.Result.Import?.Dispose();
                resolution.Result.Import = null;
            }
        }

        private IEnumerator RequestTile(
            CanonicalTileId tileId,
            string accessToken,
            LandmarkArTileCache tileCache,
            Action<byte[]> callback)
        {
            if (tileCache != null && tileCache.Tiles.TryGetValue(tileId, out byte[] cachedData))
            {
                callback(cachedData);
                yield break;
            }

            string url = MapboxApiUrls.AppendCredentials(
                $"https://api.tiles.mapbox.com/3dtiles/v1/{Constants.Map.LandmarksTilesetId}/{tileId}.glb",
                accessToken,
                () => string.Empty);
            using var request = new UnityWebRequest(url);
            request.timeout = RequestTimeoutSeconds;
            request.downloadHandler = new DownloadHandlerBuffer();
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[LandmarkArLoader] Tile {tileId} request failed: {request.error} ({request.responseCode}).");
                callback(null);
                yield break;
            }

            byte[] data = request.downloadHandler.data;
            if (tileCache != null)
            {
                tileCache.Tiles[tileId] = data;
            }

            callback(data);
        }

        private static GameObject ExtractLandmark(
            GltfGenerationTaskResult result,
            string landmarkId,
            CanonicalTileId tileId,
            double latitude,
            Transform parent,
            bool transferImport)
        {
            foreach (var anchor in result.Anchors)
            {
                if (anchor.Transform == null || anchor.LandmarkId != landmarkId)
                {
                    continue;
                }

                var model = new GameObject("LandmarkArModel");
                model.transform.SetParent(parent, false);
                anchor.Transform.SetParent(model.transform, false);
                anchor.Transform.localPosition = Vector3.zero;
                anchor.Transform.localRotation = Quaternion.identity;
                anchor.Transform.localScale = Vector3.one;

                var tileSpaceBounds = CalculateBounds(model);
                anchor.Transform.localPosition = new Vector3(
                    -tileSpaceBounds.center.x,
                    -tileSpaceBounds.min.y,
                    -tileSpaceBounds.center.z);
                var arModel = model.AddComponent<LandmarkArModel>();
                float tileEdgeMeters = Conversions.TileEdgeSizeInMercator(tileId) *
                    Mathf.Cos((float)latitude * Mathf.Deg2Rad);
                model.transform.localScale = new Vector3(tileEdgeMeters, 1f, tileEdgeMeters);
                var bounds = CalculateBounds(model);
                float footprint = Mathf.Max(bounds.size.x, bounds.size.z);
                arModel.HeightMeters = bounds.size.y;
                arModel.FootprintMeters = footprint;
                arModel.RendererCount = model.GetComponentsInChildren<Renderer>().Length;
                float heightAtTableScale = bounds.size.y * (DefaultTableFootprintMeters / Mathf.Max(footprint, 0.001f));
                arModel.TargetHeightMeters = Mathf.Clamp(heightAtTableScale, 0.1f, 2f);
                if (transferImport)
                {
                    arModel.Import = result.Import;
                    result.Import = null;
                }

                return model;
            }

            Debug.LogWarning($"[LandmarkArLoader] Landmark '{landmarkId}' was not present in the loaded tile.");
            return null;
        }

        private static bool ContainsPoint(Vector2[][] rings, Vector2 point)
        {
            if (rings == null)
            {
                return false;
            }

            foreach (Vector2[] ring in rings)
            {
                if (RingContains(ring, point))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool RingContains(Vector2[] ring, Vector2 point)
        {
            if (ring == null || ring.Length < 3)
            {
                return false;
            }

            bool inside = false;
            for (int current = 0, previous = ring.Length - 1; current < ring.Length; previous = current++)
            {
                if ((ring[current].y > point.y) == (ring[previous].y > point.y))
                {
                    continue;
                }

                float crossingX = (ring[previous].x - ring[current].x) *
                    (point.y - ring[current].y) / (ring[previous].y - ring[current].y) + ring[current].x;
                if (point.x < crossingX)
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        private static Bounds CalculateBounds(GameObject target)
        {
            var renderers = target.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
            {
                return new Bounds(Vector3.zero, Vector3.one);
            }

            var bounds = renderers[0].bounds;
            for (var index = 1; index < renderers.Length; index++)
            {
                bounds.Encapsulate(renderers[index].bounds);
            }

            return bounds;
        }
    }

    public sealed class LandmarkArModel : MonoBehaviour
    {
        public float TargetHeightMeters { get; set; }
        public float HeightMeters { get; set; }
        public float FootprintMeters { get; set; }
        public int RendererCount { get; set; }

        internal MapboxGltfImport Import { get; set; }

        private void OnDestroy()
        {
            Import?.Dispose();
            Import = null;
        }
    }

    public sealed class LandmarkArCandidate
    {
        public LandmarkArCandidate(
            string landmarkId,
            float distanceMeters,
            bool footprintContainsCoordinate,
            float heightMeters,
            float footprintMeters,
            int rendererCount,
            Vector2 groundUv = default,
            Vector2[][] footprintRings = null,
            float arrivalBearing = 0f)
        {
            LandmarkId = landmarkId;
            DistanceMeters = distanceMeters;
            FootprintContainsCoordinate = footprintContainsCoordinate;
            HeightMeters = heightMeters;
            FootprintMeters = footprintMeters;
            RendererCount = rendererCount;
            GroundUv = groundUv;
            FootprintRings = footprintRings;
            ArrivalBearing = arrivalBearing;
        }

        internal LandmarkArCandidate(
            string landmarkId,
            Vector2 groundUv,
            Vector2[][] footprintRings,
            float heightMeters,
            float footprintMeters,
            int rendererCount,
            Vector2 target,
            float tileEdgeMeters)
            : this(
                landmarkId,
                0f,
                false,
                heightMeters,
                footprintMeters,
                rendererCount,
                groundUv,
                footprintRings,
                CalculateArrivalBearing(footprintRings))
        {
            float deltaEast = (target.x - groundUv.x) * tileEdgeMeters;
            float deltaNorth = (target.y - groundUv.y) * tileEdgeMeters;
            DistanceMeters = Mathf.Sqrt(deltaEast * deltaEast + deltaNorth * deltaNorth);
            FootprintContainsCoordinate = ContainsPoint(footprintRings, target);
        }

        public string LandmarkId { get; }
        public float DistanceMeters { get; private set; }
        public bool FootprintContainsCoordinate { get; private set; }
        public float HeightMeters { get; }
        public float FootprintMeters { get; }
        public int RendererCount { get; }
        public Vector2 GroundUv { get; }
        public Vector2[][] FootprintRings { get; }
        public float ArrivalBearing { get; }
        public float ArrivalZoom => HeightMeters >= 250f ? 16.5f : HeightMeters >= 80f ? 17f : 17.5f;

        private static bool ContainsPoint(Vector2[][] rings, Vector2 point)
        {
            if (rings == null)
            {
                return false;
            }

            foreach (Vector2[] ring in rings)
            {
                if (RingContains(ring, point))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool RingContains(Vector2[] ring, Vector2 point)
        {
            if (ring == null || ring.Length < 3)
            {
                return false;
            }

            bool inside = false;
            for (int current = 0, previous = ring.Length - 1; current < ring.Length; previous = current++)
            {
                if ((ring[current].y > point.y) == (ring[previous].y > point.y))
                {
                    continue;
                }

                float crossingX = (ring[previous].x - ring[current].x) *
                    (point.y - ring[current].y) / (ring[previous].y - ring[current].y) + ring[current].x;
                if (point.x < crossingX)
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        private static float CalculateArrivalBearing(Vector2[][] rings)
        {
            Vector2? longestEdge = null;
            var centroid = Vector2.zero;
            int vertexCount = 0;
            if (rings != null)
            {
                foreach (Vector2[] ring in rings)
                {
                    if (ring == null || ring.Length < 3)
                    {
                        continue;
                    }

                    for (int current = 0, previous = ring.Length - 1; current < ring.Length; previous = current++)
                    {
                        centroid += ring[current];
                        vertexCount++;
                        var currentEdge = ring[current] - ring[previous];
                        if (!longestEdge.HasValue || currentEdge.sqrMagnitude > longestEdge.Value.sqrMagnitude)
                        {
                            longestEdge = currentEdge;
                        }
                    }
                }
            }

            if (!longestEdge.HasValue)
            {
                return 0f;
            }

            if (vertexCount > 0)
            {
                centroid /= vertexCount;
            }

            var edge = longestEdge.Value;
            var normal = new Vector2(-edge.y, edge.x).normalized;
            if (ContainsPoint(rings, centroid + normal * 0.001f))
            {
                normal = -normal;
            }

            float bearing = Mathf.Atan2(normal.x, normal.y) * Mathf.Rad2Deg;
            return (bearing + 360f) % 360f;
        }
    }
}
