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
            Assert.IsTrue(UnityTileProvider.ShouldExpandAhead(Vector3.forward, Vector3.forward));
            Assert.IsFalse(UnityTileProvider.ShouldExpandAhead(Vector3.back, Vector3.forward));
            Assert.IsTrue(UnityTileProvider.ShouldExpandAhead(Vector3.right, new Vector3(1f, 0f, 1f).normalized));
            Assert.IsFalse(UnityTileProvider.ShouldExpandAhead(Vector3.left, new Vector3(1f, 0f, 1f).normalized));
        }
    }
}
