using Mapbox.UnityMapService.TileProviders;
using NUnit.Framework;
using UnityEngine;

namespace Mapbox.BaseModuleTests
{
    public sealed class MapSpatialPrefetchTests
    {
        [Test]
        public void FrustumBufferExpandsOnlyCameraFacingSidePlanes()
        {
            var cameraPosition = Vector3.zero;
            var forward = Vector3.forward;
            var aheadPlane = new Plane(Vector3.down, new Vector3(0f, 0f, 2f));
            var behindPlane = new Plane(Vector3.up, new Vector3(0f, 0f, -2f));
            var sidePlane = new Plane(Vector3.right, Vector3.zero);

            Assert.IsTrue(UnityTileProvider.ShouldExpandAhead(aheadPlane, cameraPosition, forward, 1f));
            Assert.IsFalse(UnityTileProvider.ShouldExpandAhead(behindPlane, cameraPosition, forward, 1f));
            Assert.IsFalse(UnityTileProvider.ShouldExpandAhead(sidePlane, cameraPosition, forward, 1f));
        }
    }
}
