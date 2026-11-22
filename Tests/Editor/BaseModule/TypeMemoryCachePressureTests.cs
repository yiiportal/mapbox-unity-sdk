using System;
using System.Collections.Generic;
using Mapbox.BaseModule.Data.DataFetchers;
using Mapbox.BaseModule.Data.Platform.Cache;
using Mapbox.BaseModule.Data.Tiles;
using NUnit.Framework;

namespace Mapbox.BaseModuleTests
{
    public sealed class TypeMemoryCachePressureTests
    {
        private static VectorData CreateTile(int id)
        {
            return new VectorData { TileId = new CanonicalTileId(14, id, id), TilesetId = "test" };
        }

        [Test]
        public void SetInactiveCapacityTrimsOldestDemotedTiles()
        {
            var cache = new TypeMemoryCache<VectorData>(3);
            var disposed = new List<CanonicalTileId>();
            cache.CacheItemDisposed += disposed.Add;

            foreach (int id in new[] { 1, 2, 3 })
            {
                cache.Add(CreateTile(id));
            }

            cache.RetainTiles(new HashSet<CanonicalTileId>());
            Assert.AreEqual(3, cache.InactiveCount);

            cache.SetInactiveCapacity(1);

            Assert.AreEqual(1, cache.InactiveCount);
            CollectionAssert.AreEquivalent(new[] { new CanonicalTileId(14, 2, 2), new CanonicalTileId(14, 1, 1) }, disposed);
        }

        [Test]
        public void CustomEvictionOrderControlsVictim()
        {
            var cache = new TypeMemoryCache<VectorData>(2);
            cache.Add(CreateTile(1));
            cache.Add(CreateTile(2));
            cache.Add(CreateTile(3));
            cache.RetainTiles(new HashSet<CanonicalTileId>());

            // Keep tile3 and tile1; therefore tile2 is the ring policy's victim despite LRU age.
            cache.SetEvictionOrder(keys => new[]
            {
                new CanonicalTileId(14, 3, 3),
                new CanonicalTileId(14, 1, 1),
                new CanonicalTileId(14, 2, 2)
            });

            Assert.AreEqual(2, cache.InactiveCount);
            Assert.IsFalse(cache.Exists(new CanonicalTileId(14, 2, 2)));
            Assert.IsTrue(cache.Exists(new CanonicalTileId(14, 1, 1)));
            Assert.IsTrue(cache.Exists(new CanonicalTileId(14, 3, 3)));
        }

        [Test]
        public void MarkFallbackProtectsTileFromInactivePressure()
        {
            var cache = new TypeMemoryCache<VectorData>(2);
            var inactiveTile = CreateTile(8);
            var fallbackTile = CreateTile(9);
            cache.Add(inactiveTile);
            cache.Add(fallbackTile);
            cache.MarkFallback(fallbackTile.TileId);
            cache.RetainTiles(new HashSet<CanonicalTileId>());
            cache.SetInactiveCapacity(0);

            Assert.AreEqual(0, cache.InactiveCount);
            Assert.AreEqual(1, cache.FallbackCount);
            Assert.IsFalse(cache.Exists(inactiveTile.TileId));
            Assert.IsTrue(cache.Exists(fallbackTile.TileId));
        }
    }
}
