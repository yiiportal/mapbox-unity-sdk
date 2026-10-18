#if UNITY_EDITOR || YIIPORTAL_LANDMARK_SOURCE_A
using System;
using System.Collections;
using System.Threading.Tasks;
using Mapbox.BaseModule.Data.DataFetchers;
using Mapbox.BaseModule.Data.Tasks;
using Mapbox.BaseModule.Data.Tiles;
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

        public IEnumerator Load(
            double latitude,
            double longitude,
            string landmarkId,
            string accessToken,
            Material material,
            Transform parent,
            Action<GameObject> callback)
        {
            if (string.IsNullOrEmpty(landmarkId) || string.IsNullOrEmpty(accessToken) || material == null || parent == null)
            {
                callback(null);
                yield break;
            }

            var tileId = Conversions.LatitudeLongitudeToTileId(latitude, longitude, LandmarksLayerModule.LandmarkDataZoom).Canonical;
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

            var data = new BuildingData { TileId = tileId, Data = request.downloadHandler.data };
            var loadRoot = new GameObject("LandmarkArLoadRoot");
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
                model = ExtractLandmark(result, landmarkId, tileId, latitude, loadRoot.transform);
            }
            else
            {
                Debug.LogWarning($"[LandmarkArLoader] Tile {tileId} did not produce a landmark ({result?.ResultType}).");
            }

            visualizer.OnDestroy();
            if (model != null)
            {
                model.transform.SetParent(parent, false);
            }

            Destroy(loadRoot);
            callback(model);
        }

        private static GameObject ExtractLandmark(
            GltfGenerationTaskResult result,
            string landmarkId,
            CanonicalTileId tileId,
            double latitude,
            Transform parent)
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
                float heightAtTableScale = bounds.size.y * (DefaultTableFootprintMeters / Mathf.Max(footprint, 0.001f));
                arModel.TargetHeightMeters = Mathf.Clamp(heightAtTableScale, 0.1f, 2f);
                arModel.Import = result.Import;
                result.Import = null;
                return model;
            }

            Debug.LogWarning($"[LandmarkArLoader] Landmark '{landmarkId}' was not present in the loaded tile.");
            return null;
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

        internal MapboxGltfImport Import { get; set; }

        private void OnDestroy()
        {
            Import?.Dispose();
            Import = null;
        }
    }
}
#endif
