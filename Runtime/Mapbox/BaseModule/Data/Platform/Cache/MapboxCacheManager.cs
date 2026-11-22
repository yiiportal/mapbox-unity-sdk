using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Mapbox.BaseModule.Data.DataFetchers;
using Mapbox.BaseModule.Data.Platform.Cache.SQLiteCache;
using Mapbox.BaseModule.Data.Tasks;
using Mapbox.BaseModule.Data.Tiles;
using Mapbox.BaseModule.Unity;
using UnityEditor;
using UnityEngine;

namespace Mapbox.BaseModule.Data.Platform.Cache
{
    public interface IMapboxCacheManager
    {
        void SaveBlob(MapboxTileData vectorCacheItem, bool forceInsert);
        void SaveImage(RasterData textureCacheItem, bool forceInsert);
        void GetImageAsync<T>(CanonicalTileId tileId, string tilesetId, bool isTextureNonreadable, Action<T> callback) where T : RasterData, new();

        IEnumerator GetImageCoroutine<T>(CanonicalTileId tileId, string tilesetId, bool isTextureNonreadable, Action<T> callback) where T : RasterData, new();
        DataTaskWrapper<T> GetTileInfoTask<T>(CanonicalTileId tileId, string tilesetid, int priority = 1, T data = null) where T : MapboxTileData, new();
        IEnumerator GetBlobCoroutine<T>(CanonicalTileId tileId, string tilesetid, int priority = 1, T data = null, Action<T> callback = null, Action<TaskWrapper> onTaskCreated = null) where T : MapboxTileData, new();
        
        void UpdateExpiration(CanonicalTileId tileId, string tilesetId, DateTime date);
    }

    public class MapboxCacheManager : IMapboxCacheManager
    {
        public Action<TaskWrapper> CacheTaskFinished = (t) => { };

        public static MapboxCacheManager Shared { get; private set; }
        public static Func<MapboxCacheManager> SharedFactory { get; set; }

        public static void SetShared(MapboxCacheManager cacheManager)
        {
            Shared = cacheManager;
        }
        public MapboxCacheManager PinnedCacheManager { get; set; }
        
        protected IMemoryCache _memoryCache;
        protected IFileCache _textureFileCache;
        protected ISqliteCache _sqLiteCache;
        protected TaskManager _taskManager;

        public MapboxCacheManager(UnityContext unityContext, MemoryCache memoryCache, FileCache fileCache = null, ISqliteCache cache = null)
        {
            _taskManager = unityContext.TaskManager;
            _memoryCache = memoryCache;
            _textureFileCache = fileCache;
            _sqLiteCache = cache;

            if (_textureFileCache != null)
            {
                if (_textureFileCache.TestAvailability() == false)
                    _textureFileCache = null;
            }

            if (_sqLiteCache != null && _textureFileCache != null)
            {
                _sqLiteCache.DataPrunedForFile += path => _textureFileCache.DeleteByFileRelativePath(path);
                _sqLiteCache.DatabaseCleared += _textureFileCache.ClearAll;
            }
            
            if (_sqLiteCache != null)
            {
                if (!_sqLiteCache.IsUpToDate())
                {
                    Debug.Log("renewing sqlite cache file");
                    var sqliteDeleteSuccess = _sqLiteCache.ClearDatabase();
                    if (sqliteDeleteSuccess && _textureFileCache != null)
                    {
                        _textureFileCache.ClearAll();
                    }
                    _sqLiteCache.ReadySqliteDatabase();
                }

                CheckSqlAndFileIntegrity();
            }

        }

        public void ClearCachedData()
        {
            _sqLiteCache.ClearDatabase();
            _sqLiteCache.ReadySqliteDatabase();
        }
        
        public virtual void SaveImage(RasterData textureCacheItem, bool forceInsert)
        {
            _textureFileCache?.Add(textureCacheItem, forceInsert, (path) =>
            {
                SaveTileIndex(textureCacheItem, path, _textureFileCache?.GetFileSize(textureCacheItem.TileId, textureCacheItem.TilesetId) ?? 0);
            });
        }
        
        public virtual void SaveBlob(MapboxTileData vectorCacheItem, bool forceInsert)
        {
            _sqLiteCache?.Add(vectorCacheItem, forceInsert);
        }

        // FORK yiiportal: Source A AR landmarks reuse the map disk cache after map teardown.
        public virtual async Task<T> GetBlobAsync<T>(
            CanonicalTileId tileId,
            string tilesetId,
            T data = null)
            where T : MapboxTileData, new()
        {
            if (_sqLiteCache == null)
            {
                return null;
            }

            var cached = await Task.Run(() => _sqLiteCache.Get(tilesetId, tileId, data));
            if ((cached == null || cached.Data == null || cached.Data.Length == 0) &&
                PinnedCacheManager != null)
            {
                cached = await PinnedCacheManager.GetBlobAsync(tileId, tilesetId, data);
            }

            if (cached == null || cached.Data == null || cached.Data.Length == 0)
            {
                return null;
            }

            return cached.ExpirationDate.HasValue && cached.ExpirationDate.Value < DateTime.UtcNow ? null : cached;
        }

        public virtual Task<bool> SaveBlobAsync(MapboxTileData tileData)
        {
            if (_sqLiteCache == null || tileData?.Data == null || tileData.Data.Length == 0)
            {
                return Task.FromResult(false);
            }

            return Task.Run(() =>
            {
                if (_sqLiteCache is SqliteCache sqliteCache)
                {
                    sqliteCache.SyncAdd(
                        tileData.TilesetId,
                        tileData.TileId,
                        tileData.Data,
                        string.Empty,
                        tileData.ETag,
                        tileData.ExpirationDate ?? DateTime.UtcNow,
                        true,
                        tileData.Data.Length);
                }
                else
                {
                    _sqLiteCache.SyncAdd(
                        tileData.TilesetId,
                        tileData.TileId,
                        tileData.Data,
                        string.Empty,
                        tileData.ETag,
                        tileData.ExpirationDate ?? DateTime.UtcNow,
                        true);
                }

                return true;
            });
        }
        
        public virtual void GetImageAsync<T>(CanonicalTileId tileId, string tilesetId, bool isTextureNonreadable, Action<T> callback) where T : RasterData, new()
        {
            bool found = false;
            void Complete(T item)
            {
                if (!found)
                {
                    found = true;
                    callback(item);
                }
            }

            if (_textureFileCache != null)
            {
                var fileExists = _textureFileCache.GetAsync<T>(
                    tileId,
                    tilesetId,
                    isTextureNonreadable,
                    item => Complete(item != null && item.HasError ? null : item));

                if (!fileExists && PinnedCacheManager != null)
                {
                    PinnedCacheManager.GetImageAsync<T>(tileId, tilesetId, isTextureNonreadable, Complete);
                }
                else if (!fileExists)
                {
                    callback(null);
                }
            }
            else
            {
                callback(null);
            }
        }
        
        public virtual DataTaskWrapper<T> GetTileInfoTask<T>(CanonicalTileId tileId, string tilesetid, int priority = 1, T data = null)
            where T : MapboxTileData, new()
        {
            if (_sqLiteCache == null) return null;
            
            var task = new DataTaskWrapper<T>();
            task.TileId = tileId;
            task.DataAction = () => _sqLiteCache.Get<T>(tilesetid, tileId, data);
            _taskManager.AddTask(task, priority);
            return task;
        }
        
        
        
        public virtual IEnumerator SaveImageCoroutine(RasterData textureCacheItem, bool forceInsert)
        {
            string finalPath = ""; 
            yield return _textureFileCache?.AddCoroutine(textureCacheItem, (path) =>
            {
                finalPath = path;
            });
            if (!string.IsNullOrEmpty(finalPath))
            {
                SaveTileIndex(textureCacheItem, finalPath, _textureFileCache?.GetFileSize(textureCacheItem.TileId, textureCacheItem.TilesetId) ?? 0);
            }
        }

        // FORK yiiportal: the SQLite index records physical bytes for family-wise eviction.
        private void SaveTileIndex(MapboxTileData tileData, string path, long tileSizeBytes)
        {
            if (_sqLiteCache == null)
            {
                return;
            }

            if (_sqLiteCache is SqliteCache sqliteCache)
            {
                sqliteCache.SyncAdd(
                    tileData.TilesetId,
                    tileData.TileId,
                    tileData.Data,
                    path,
                    tileData.ETag,
                    tileData.ExpirationDate,
                    true,
                    tileSizeBytes);
            }
            else
            {
                _sqLiteCache.SyncAdd(
                    tileData.TilesetId,
                    tileData.TileId,
                    tileData.Data,
                    path,
                    tileData.ETag,
                    tileData.ExpirationDate,
                    true);
            }
        }

        public virtual IEnumerator SaveBlobCoroutine(MapboxTileData vectorCacheItem, bool forceInsert)
        {
            if (_sqLiteCache != null)
            {
                var isWorking = true;
                _sqLiteCache.Add(vectorCacheItem, forceInsert, (success) => { isWorking = false; });
                while (isWorking) yield return null;
            }
        }
        
        public virtual IEnumerator GetImageCoroutine<T>(CanonicalTileId tileId, string tilesetId, bool isTextureNonreadable, Action<T> callback) where T : RasterData, new()
        {
            if (_textureFileCache != null)
            {
                T normalItem = null;
                yield return _textureFileCache.GetCoroutine<T>(tileId, tilesetId, isTextureNonreadable,
                    (item) => normalItem = item != null && item.HasError ? null : item);
                if (normalItem != null)
                {
                    callback(normalItem);
                    yield break;
                }

                if (PinnedCacheManager != null)
                {
                    yield return PinnedCacheManager.GetImageCoroutine<T>(
                        tileId,
                        tilesetId,
                        isTextureNonreadable,
                        item => callback(item != null && item.HasError ? null : item));
                    yield break;
                }

                callback(null);
            }
            else
            {
                callback(null);
            }
        }

        /// <summary>
        /// <paramref name="onTaskCreated"/> hands the caller the wrapper so it can be tracked and
        /// cancelled. Without it the task was created, queued and dropped on the floor here: nothing
        /// upstream held a reference, so a tile leaving the cover could not cancel its own read, and
        /// the dispatch delay those reads waited through protected a cancellation that could never
        /// arrive.
        /// </summary>
        public virtual IEnumerator GetBlobCoroutine<T>(CanonicalTileId tileId, string tilesetid, int priority = 1, T data = null, Action<T> callback = null, Action<TaskWrapper> onTaskCreated = null) where T : MapboxTileData, new()
        {
            if (_sqLiteCache == null) yield break;
            
            var task = new DataTaskWrapper<T>
            {
                TileId = tileId,
                DataAction = () => _sqLiteCache.Get<T>(tilesetid, tileId, data)
            };
            onTaskCreated?.Invoke(task);
            _taskManager.AddTask(task, priority);
            while(task.IsCompleted == false) yield return null;
            
            if (task.DataResult == null && PinnedCacheManager != null)
            {
                yield return PinnedCacheManager.GetBlobCoroutine(
                    tileId,
                    tilesetid,
                    priority,
                    data,
                    result => callback?.Invoke(result));
                yield break;
            }

            callback?.Invoke(task.DataResult);
        }

        
        
        public virtual void UpdateExpiration(CanonicalTileId tileId, string tilesetId, DateTime date)
        {
            _sqLiteCache.UpdateExpiration(tilesetId, tileId, date);
        }
        
        public void RemoveData(string tilesetId, int zoom, int x, int y)
        {
            _sqLiteCache.RemoveData(tilesetId, zoom, x, y);
        }

        public void SetPinned(string tilesetId, CanonicalTileId tileId, bool pinned)
        {
            if (_sqLiteCache is SqliteCache sqliteCache)
            {
                sqliteCache.SetPinned(tilesetId, tileId, pinned);
            }
        }

        public bool HasUnexpiredTile(string tilesetId, CanonicalTileId tileId, bool requireFile = true)
        {
            bool hasIndexRow = _sqLiteCache is SqliteCache sqliteCache &&
                               sqliteCache.HasUnexpiredTile(tilesetId, tileId);
            bool hasRasterFile = !requireFile || _textureFileCache == null || _textureFileCache.Exists(tileId, tilesetId);
            return hasIndexRow && hasRasterFile;
        }

        public TypeMemoryCache<T> RegisterMemoryCache<T>(int owner, int cacheSize = 100) where T : MapboxTileData
        {
            return _memoryCache.RegisterType<T>(owner, cacheSize);
        }

        /// <summary>
        /// We check for files that exists but not tracked in sqlite file and delete them all
        /// If we don't do that, those files will pile up (assuming systems loses track due to a bug somehow) and fill all the disk
        /// Vice versa (file doesn't exists, sqlite entry does) isn't important as entry will be cycled out soon anyway
        /// </summary>
        private void CheckSqlAndFileIntegrity(bool firstRun = true)
        {
            if (_sqLiteCache == null || _textureFileCache == null) return;
            
            var sqlTileList = _sqLiteCache.GetAllTiles();
            var fileList = _textureFileCache.GetFileList();

            // Debug.Log("sqlite " + string.Join(Environment.NewLine, sqlTileList.Select(x => x.tile_path)));
            // Debug.Log("file " + string.Join(Environment.NewLine, fileList));
            
            foreach (var tile in sqlTileList)
            {
                if (fileList.Contains(tile.tile_path))
                {
                    fileList.Remove(tile.tile_path);
                }
            }
            
            if (fileList.Count > 0)
            {
                Debug.Log(string.Format("{0} files will be deleted to sync sqlite and file cache", fileList.Count));
                foreach (var fileRelativePath in fileList)
                {
                    _textureFileCache.DeleteByFileRelativePath(fileRelativePath);
                }

                if (firstRun)
                {
                    CheckSqlAndFileIntegrity(false);
                }
            }
            else
            { 
                //Debug.Log("Sqlite and File Caches are in sync");
            }
        }
        
        public void OnDestroy()
        {
            //close sqlite&file caches here?
            _memoryCache.OnDestroy();
        }

        public void CancelFetching(CanonicalTileId tileId, string tilesetId)
        {
            // var key = tileId.GenerateKey(tilesetId, "GetTileInfoAsync");
            // _taskManager.CancelTask(key);
            // key = tileId.GenerateKey(tilesetId, "ReadEtagExpiration");
            // _taskManager.CancelTask(key);
        }
        
        public static void DeleteAllCache()
        {
            var sqliteDeleted = SqliteCache.DeleteSqliteFolder();
            var fileCacheDeleted = FileCache.ClearAllFiles();
            if (sqliteDeleted && fileCacheDeleted)
            {
                Debug.Log("Mapbox cache cleared");
            }
        }

#if UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetShared()
        {
            Shared = null;
        }
#endif
    }
}
