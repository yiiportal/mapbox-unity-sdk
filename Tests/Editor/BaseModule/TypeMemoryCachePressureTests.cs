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
    }
}
