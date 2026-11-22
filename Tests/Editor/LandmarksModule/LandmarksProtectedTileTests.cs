using System.Collections.Generic;
using Mapbox.BaseModule.Data.Tiles;
using Mapbox.LandmarksModule;
using NUnit.Framework;

namespace Mapbox.LandmarksModuleTests
{
    public sealed class LandmarksProtectedTileTests
    {
        private static readonly CanonicalTileId FloorTile = new(14, 8000, 5000);
        private static readonly CanonicalTileId OtherTile = new(14, 8001, 5000);

        [Test]
        public void SuspensionRetainsProtectedTilesButDropsUnresolvedOthers()
        {
            var protectedTiles = new HashSet<CanonicalTileId> { FloorTile };

            Assert.IsTrue(LandmarksLayerModule.ShouldRetainUnderSuspension(
                FloorTile,
                protectedTiles,
                _ => true));
            Assert.IsFalse(LandmarksLayerModule.ShouldRetainUnderSuspension(
                OtherTile,
                protectedTiles,
                _ => true));
            Assert.IsTrue(LandmarksLayerModule.ShouldRetainUnderSuspension(
                OtherTile,
                protectedTiles,
                _ => false));
        }

        [Test]
        public void ProtectedTilesAreStoredForRetention()
        {
            var module = (LandmarksLayerModule)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(
                typeof(LandmarksLayerModule));
            module.SetProtectedTiles(new[] { FloorTile });

            CollectionAssert.AreEquivalent(new[] { FloorTile }, module.ProtectedTiles);
        }
    }
}
