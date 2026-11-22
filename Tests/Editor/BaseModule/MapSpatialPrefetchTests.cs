using System.Linq;
using Mapbox.BaseModule.Data.Tiles;
using Mapbox.UnityMapService.DataSources;
using Mapbox.UnityMapService.TileProviders;
using NUnit.Framework;
using UnityEngine;

namespace Mapbox.BaseModuleTests
{
    public sealed class MapSpatialPrefetchTests
    {
        private const int LeftPlane = 0;
        private const int RightPlane = 1;
        private const int BottomPlane = 2;
        private const int TopPlane = 3;

        [Test]
        public void PitchedCameraLeavesOnlyThePlaneBehindTheHeadingUnexpanded()
        {
            var planes = CalculateFrustumPlanes(Quaternion.Euler(45f, 30f, 0f), out var forward);

            Assert.IsTrue(UnityTileProvider.ShouldExpandSidePlane(planes[TopPlane], forward, Vector3.up));
            Assert.IsTrue(UnityTileProvider.ShouldExpandSidePlane(planes[LeftPlane], forward, Vector3.up));
            Assert.IsTrue(UnityTileProvider.ShouldExpandSidePlane(planes[RightPlane], forward, Vector3.up));
            Assert.IsFalse(UnityTileProvider.ShouldExpandSidePlane(planes[BottomPlane], forward, Vector3.up));
        }

        [Test]
        public void TopDownCameraExpandsEverySidePlane()
        {
            var planes = CalculateFrustumPlanes(Quaternion.Euler(90f, 0f, 0f), out var forward);

            for (int i = LeftPlane; i <= TopPlane; i++)
            {
                Assert.IsTrue(UnityTileProvider.ShouldExpandSidePlane(planes[i], forward, Vector3.up));
            }
        }

        [Test]
        public void RasterEvictionTakesFarthestAreaFirstThenLeastRecentAndFallbackPyramidLast()
        {
            var centerArea = new CanonicalTileId(10, 500, 500);
            var fallback = new CanonicalTileId(3, 3, 3);
            var nearOld = new CanonicalTileId(14, 500 * 16, 500 * 16);
            var nearNew = new CanonicalTileId(14, 500 * 16 + 1, 500 * 16);
            var far = new CanonicalTileId(14, 510 * 16, 500 * 16);

            // Inactive keys arrive newest first.
            var order = RasterEvictionPolicy.Order(
                new[] { nearNew, fallback, far, nearOld },
                centerArea).ToArray();

            CollectionAssert.AreEqual(new[] { far, nearOld, nearNew, fallback }, order);
        }

        private static Plane[] CalculateFrustumPlanes(Quaternion rotation, out Vector3 forward)
        {
            var cameraObject = new GameObject("PrefetchTestCamera");
            try
            {
                var camera = cameraObject.AddComponent<Camera>();
                camera.fieldOfView = 60f;
                camera.aspect = 9f / 16f;
                cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 100f, 0f), rotation);
                forward = cameraObject.transform.forward;
                return GeometryUtility.CalculateFrustumPlanes(camera);
            }
            finally
            {
                Object.DestroyImmediate(cameraObject);
            }
        }
    }
}
