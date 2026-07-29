using Mapbox.BaseModule.Map;
using Mapbox.UnityMapService.TileProviders;
using NUnit.Framework;
using UnityEngine;

namespace Mapbox.BaseModuleTests
{
    public class UnityTileProviderTests
    {
        private GameObject _cameraObject;

        [TearDown]
        public void TearDown()
        {
            if (_cameraObject != null)
            {
                Object.DestroyImmediate(_cameraObject);
            }
        }

        [Test]
        public void GetTileCover_TiltedCameraNearMapPlane_ReturnsMultipleBudgetedTiles()
        {
            const int tileBudget = 16;
            var camera = CreateTiltedCamera();
            var mapInformation = new MapInformation(
                "40.72419,-74.33631",
                28.5f,
                8f,
                15f);
            mapInformation.Initialize();
            var settings = new UnityTileProviderSettings(camera)
            {
                MinimumZoomLevel = 2f,
                MaximumZoomLevel = 22f,
                MaximumTileCount = tileBudget,
                SubdivisionBias = 0.6f
            };
            var provider = new UnityTileProvider(settings);
            var tileCover = new TileCover();

            bool succeeded = provider.GetTileCover(mapInformation, tileCover);

            Assert.IsTrue(succeeded);
            Assert.Greater(tileCover.Tiles.Count, 1);
            Assert.LessOrEqual(tileCover.Tiles.Count, tileBudget);
            Assert.IsTrue(provider.WasTileBudgetApplied);
            Assert.Less(
                provider.LastAppliedMaximumZoom,
                provider.LastRequestedMaximumZoom);
        }

        private Camera CreateTiltedCamera()
        {
            _cameraObject = new GameObject("UnityTileProviderTests.Camera");
            var camera = _cameraObject.AddComponent<Camera>();
            camera.aspect = 390f / 844f;
            camera.fieldOfView = 77f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 5000f;
            camera.transform.SetPositionAndRotation(
                new Vector3(0f, 100f, -100f),
                Quaternion.Euler(28.5f, 0f, 0f));
            return camera;
        }
    }
}
