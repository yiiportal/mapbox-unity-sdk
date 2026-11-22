using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Mapbox.BaseModule.Data.Platform.Cache;
using Mapbox.BaseModule.Data.Platform.Cache.SQLiteCache;
using Mapbox.BaseModule.Data.Platform.SQLite;
using Mapbox.BaseModule.Data.Tasks;
using Mapbox.BaseModule.Data.Tiles;
using Mapbox.BaseModule.Unity;
using NUnit.Framework;
using UnityEngine;

namespace Mapbox.BaseModuleTests
{
    [TestFixture]
    internal class SqliteCacheBudgetTests
    {
        private const long BudgetBytes = 1000;
        private TestSqliteCache _cache;
        private string _sqliteRoot;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            _sqliteRoot = Path.Combine(Application.temporaryCachePath, "MapboxCacheBudgetTests", Guid.NewGuid().ToString("N"));
            _cache = new TestSqliteCache(CreateTaskManager(), _sqliteRoot, CreateBudget());
        }

        [SetUp]
        public void SetUp()
        {
            _cache.ClearDatabase();
            _cache.ReadySqliteDatabase();
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            _cache.Dispose();
            if (Directory.Exists(_sqliteRoot))
            {
                Directory.Delete(_sqliteRoot, true);
            }
        }

        [Test]
        public void InjectedRootIsUsed()
        {
            StringAssert.StartsWith(_sqliteRoot, _cache.CacheRoot);
            StringAssert.DoesNotContain(Application.persistentDataPath, _cache.CacheRoot);
        }

        [Test]
        public void LandmarkFamilyIsNotPrunedWhenRasterFamilyIsOverBudget()
        {
            SyncAdd("mapbox.raster", 16, 1, 1, BudgetBytes - 300, forceInsert: true);
            SyncAdd("mapbox.landmarks", 14, 2, 2, BudgetBytes - 300, forceInsert: true);
            Thread.Sleep(1100);
            SyncAdd("mapbox.raster", 16, 3, 3, BudgetBytes - 300, forceInsert: true);
            ReadThenTouchExistingTile("mapbox.raster", 16, 1, 1);
            Thread.Sleep(1100);
            ReadThenTouchExistingTile("mapbox.raster", 16, 3, 3);

            Assert.IsFalse(TileExists("mapbox.raster", 16, 1, 1));
            Assert.IsTrue(TileExists("mapbox.raster", 16, 3, 3));
            Assert.AreEqual(1, _cache.TileCount("mapbox.landmarks"));
        }

        [Test]
        public void LandmarkBudgetDoesNotPruneRaster()
        {
            SyncAdd("mapbox.landmarks", 14, 1, 1, BudgetBytes - 300, forceInsert: true);
            SyncAdd("mapbox.raster", 16, 2, 2, BudgetBytes - 300, forceInsert: true);
            Thread.Sleep(1100);
            SyncAdd("mapbox.landmarks", 14, 3, 3, BudgetBytes - 300, forceInsert: true);
            ReadThenTouchExistingTile("mapbox.landmarks", 14, 1, 1);
            Thread.Sleep(1100);
            ReadThenTouchExistingTile("mapbox.landmarks", 14, 3, 3);

            Assert.IsFalse(TileExists("mapbox.landmarks", 14, 1, 1));
            Assert.IsTrue(TileExists("mapbox.landmarks", 14, 3, 3));
            Assert.AreEqual(1, _cache.TileCount("mapbox.raster"));
        }

        [Test]
        public void SchemaVersionMismatchRecreatesDatabase()
        {
            SyncAdd("mapbox.raster", 16, 1, 1, 100, forceInsert: true);
            using (var versionConnection = new SQLiteConnection(_cache.DatabasePath))
            {
                versionConnection.Execute("PRAGMA user_version=2");
            }

            var taskManager = CreateTaskManager();
            var fileRoot = Path.Combine(Application.temporaryCachePath, "MapboxCacheBudgetFileTests", Guid.NewGuid().ToString("N"));
            var fileCache = new FileCache(taskManager, fileRoot, string.Empty);
            var context = new UnityContext { TaskManager = taskManager };
            new MapboxCacheManager(context, new MemoryCache(), fileCache, _cache);

            Assert.IsTrue(_cache.IsUpToDate());
            Assert.AreEqual(0, _cache.TileCount("mapbox.raster"));
        }

        [Test]
        public void PinnedRowsSurviveByteBudgetPrune()
        {
            SyncPinnedAdd("mapbox.raster", 16, 1, 1, BudgetBytes - 300);
            SyncAdd("mapbox.raster", 16, 2, 2, BudgetBytes - 300, forceInsert: true);
            TriggerPruneWithZeroByteTiles("mapbox.raster", 21);

            Assert.IsTrue(TileExists("mapbox.raster", 16, 1, 1), "pinned tile was evicted");
            Assert.IsFalse(TileExists("mapbox.raster", 16, 2, 2), "over-budget non-pinned tile was retained");
        }

        [Test]
        public void OrdinaryOverwritePreservesPinnedStatus()
        {
            SyncPinnedAdd("mapbox.raster", 16, 5, 5, BudgetBytes - 300);

            // Normal maintenance rewrites tile bytes and expiration, but must not clear pinning.
            _cache.SyncAdd(
                "mapbox.raster",
                new CanonicalTileId(16, 5, 5),
                Array.Empty<byte>(),
                string.Empty,
                "etag",
                DateTime.UtcNow.AddHours(2),
                true);

            Assert.IsTrue(TileExists("mapbox.raster", 16, 5, 5));
            Assert.AreEqual(1, GetPinnedValue("mapbox.raster", 16, 5, 5));
        }

        private static TaskManager CreateTaskManager()
        {
            var taskManager = new TaskManager();
            taskManager.Initialize();
            return taskManager;
        }

        private static SqliteCacheBudget CreateBudget()
        {
            var budgets = new Dictionary<string, long>
            {
                ["landmarks"] = BudgetBytes,
                ["raster"] = BudgetBytes,
                ["vector"] = BudgetBytes
            };
            return new SqliteCacheBudget(budgets, BudgetBytes);
        }

        private void SyncAdd(string tilesetName, int zoom, int x, int y, long sizeBytes, bool forceInsert)
        {
            _cache.SyncAdd(
                tilesetName,
                new CanonicalTileId(zoom, x, y),
                Array.Empty<byte>(),
                string.Empty,
                "etag",
                DateTime.UtcNow.AddHours(1),
                forceInsert,
                sizeBytes);
        }

        private void SyncPinnedAdd(string tilesetName, int zoom, int x, int y, long sizeBytes)
        {
            _cache.SyncAdd(
                tilesetName,
                new CanonicalTileId(zoom, x, y),
                Array.Empty<byte>(),
                string.Empty,
                "etag",
                DateTime.UtcNow.AddHours(1),
                true,
                sizeBytes,
                true);
        }

        private void TriggerPruneWithZeroByteTiles(string tilesetName, int tileCount)
        {
            for (int i = 0; i < tileCount; i++)
            {
                _cache.SyncAdd(
                    tilesetName,
                    new CanonicalTileId(16, 20 + i, 20 + i),
                    Array.Empty<byte>(),
                    string.Empty,
                    "etag",
                    DateTime.UtcNow.AddHours(1),
                    false);
            }
        }

        private void ReadThenTouchExistingTile(string tilesetName, int zoom, int x, int y)
        {
            var tileId = new CanonicalTileId(zoom, x, y);
            _cache.Get<Mapbox.BaseModule.Data.DataFetchers.VectorData>(tilesetName, tileId);
            _cache.SyncAdd(tilesetName, tileId, Array.Empty<byte>(), string.Empty, "etag", DateTime.UtcNow.AddHours(1), false);
            _cache.SyncAdd(tilesetName, tileId, Array.Empty<byte>(), string.Empty, "etag", DateTime.UtcNow.AddHours(1), false);
        }

        private bool TileExists(string tilesetName, int zoom, int x, int y)
        {
            using (var connection = new SQLiteConnection(_cache.DatabasePath))
            {
                return connection.ExecuteScalar<int>(
                    "SELECT EXISTS(SELECT 1 FROM tiles t JOIN tilesets ts ON ts.id = t.tile_set " +
                    "WHERE ts.name = ?1 AND t.zoom_level = ?2 AND t.tile_column = ?3 AND t.tile_row = ?4)",
                    tilesetName,
                    zoom,
                    x,
                    y) == 1;
            }
        }

        private int GetPinnedValue(string tilesetName, int zoom, int x, int y)
        {
            using (var connection = new SQLiteConnection(_cache.DatabasePath))
            {
                return connection.ExecuteScalar<int>(
                    "SELECT t.pinned FROM tiles t JOIN tilesets ts ON ts.id = t.tile_set " +
                    "WHERE ts.name = ?1 AND t.zoom_level = ?2 AND t.tile_column = ?3 AND t.tile_row = ?4",
                    tilesetName,
                    zoom,
                    x,
                    y);
            }
        }

        private sealed class TestSqliteCache : SqliteCache
        {
            public TestSqliteCache(TaskManager taskManager, string cacheRootPath, SqliteCacheBudget budget)
                : base(taskManager, cacheRootPath, budget, name => name.Contains("landmarks") ? "landmarks" : "raster")
            {
            }

            public string CacheRoot => PersistantCacheRootFolderPath;

            public string DatabasePath => Path.Combine(CacheRoot, "cache.db");
        }
    }
}
