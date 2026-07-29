using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Mapbox.BaseModule.Data.DataFetchers;
using Mapbox.BaseModule.Data.Platform;
using Mapbox.BaseModule.Data.Platform.Cache;
using Mapbox.BaseModule.Data.Tiles;
using Mapbox.BaseModule.Data.Vector2d;
using Mapbox.BaseModule.Map;
using Mapbox.BaseModule.Utilities;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Profiling;

namespace Mapbox.UnityMapService.DataSources
{
    public abstract class ImageSource<T> : UnitySource<T> where T : RasterData, new()
    {
        protected Dictionary<RasterRequestKey, RasterTile> _waitingList;
        protected TypeMemoryCache<T> _memoryCache;
        private HashSet<CanonicalTileId> _activeRequestsToCancel;
        private ImageSourceSettings _settings;
        private List<T> _preparedTilesetData;
        private string _preparedTilesetId;
        private readonly HashSet<CanonicalTileId> _retainedTileIds = new HashSet<CanonicalTileId>();
        private readonly Dictionary<RasterRequestKey, int> _asyncRequestVersions =
            new Dictionary<RasterRequestKey, int>();
        private readonly List<PrioritizedTileId> _requestOrder = new List<PrioritizedTileId>();
        private readonly HashSet<RasterTile> _preparedTileRequests = new HashSet<RasterTile>();
        private int _nextAsyncRequestVersion;
        private int _preparationVersion;
        private LatitudeLongitude _requestPriorityCenter;
        private bool _hasRequestPriorityCenter;

        public int WaitingRequestCount => _waitingList.Count;
        public int PreparedTileCount => _preparedTilesetData?.Count ?? 0;
        public int ActiveCacheCount => _memoryCache.ActiveCount;
        public int InactiveCacheCount => _memoryCache.InactiveCount;
        public int FallbackCacheCount => _memoryCache.FallbackCount;
        public long EstimatedResidentTextureBytes
        {
            get
            {
                long byteCount = 0;
                foreach (var data in _memoryCache.GetAllDatas())
                {
                    if (data?.Texture != null)
                    {
                        byteCount += EstimateTextureBytes(data.Texture);
                    }
                }

                if (_preparedTilesetData != null)
                {
                    foreach (var data in _preparedTilesetData)
                    {
                        if (data?.Texture != null)
                        {
                            byteCount += EstimateTextureBytes(data.Texture);
                        }
                    }
                }

                return byteCount;
            }
        }

        private static long EstimateTextureBytes(Texture2D texture)
        {
            long profilerBytes = Profiler.GetRuntimeMemorySizeLong(texture);
            uint pixelBytes = GraphicsFormatUtility.ComputeMipChainSize(
                Math.Max(1, texture.width),
                Math.Max(1, texture.height),
                texture.graphicsFormat,
                Math.Max(1, texture.mipmapCount));
            return Math.Max(profilerBytes, pixelBytes);
        }

        protected ImageSource(DataFetchingManager dataFetchingManager, MapboxCacheManager cacheManager, ImageSourceSettings settings) : base(dataFetchingManager, cacheManager, settings.TilesetId)
        {
            _settings = settings;
            _waitingList = new Dictionary<RasterRequestKey, RasterTile>();
            _activeRequestsToCancel = new HashSet<CanonicalTileId>();

            _memoryCache = RegisterTypeToMemoryCache<T>(this.GetHashCode(), _settings.CacheSize);
            _memoryCache.CacheItemDisposed += (t) =>
            {
                CacheItemDisposed(t);
            };
        }
        
        public override void LoadTile(CanonicalTileId requestedDataTileId)
        {
            LoadTileCore(requestedDataTileId);
        }
        
        public override bool CheckInstantData(CanonicalTileId tileId)
        {
            return GetInstantData(tileId, out _);
        }

        public override bool GetInstantData(CanonicalTileId tileId, out T data)
        {
            var result = _memoryCache.Get(tileId, out data);
            // FORK yiiportal: the memory cache is keyed by tile id only, so after a
            // ChangeTilesetId (day/night style switch) it can still hold entries fetched
            // for the previous style. Treat those as misses and evict so callers refetch
            // with the current style instead of rendering stale imagery.
            if (result && data != null && IsStaleStyle(data.TilesetId))
            {
                data = null;
                return false;
            }
            if (data != null)
            {
                data.CacheType = CacheType.MemoryCache;
            }
            return result;
        }

        private bool IsStaleStyle(string dataTilesetId)
        {
            return !string.IsNullOrEmpty(dataTilesetId) && dataTilesetId != _tilesetId;
        }

        public override bool RetainTiles(HashSet<CanonicalTileId> retainedTiles)
        {
            _retainedTileIds.Clear();
            _retainedTileIds.UnionWith(retainedTiles);

            _activeRequestsToCancel.Clear();
            foreach (var activeTile in _waitingList)
            {
                if (string.Equals(activeTile.Key.TilesetId, _tilesetId, StringComparison.Ordinal) &&
                    !retainedTiles.Contains(activeTile.Key.TileId) &&
                    activeTile.Value != null &&
                    !activeTile.Value.IsBackgroundData)
                {
                    _activeRequestsToCancel.Add(activeTile.Key.TileId);
                }
            }
            
            foreach (var id in _activeRequestsToCancel)
            {
                CancelActiveRequests(id);
            }

            // Distance is computed once per tile rather than inside the comparer, which
            // would re-run two LatitudeLongitudeToTileId conversions per comparison.
            _requestOrder.Clear();
            foreach (var id in retainedTiles)
            {
                _requestOrder.Add(new PrioritizedTileId(
                    id,
                    _hasRequestPriorityCenter ? GetNormalizedTileDistanceSquared(id) : 0d));
            }

            if (_hasRequestPriorityCenter)
            {
                _requestOrder.Sort(ComparePrioritizedTileId);
            }

            foreach (var entry in _requestOrder)
            {
                if (!CheckInstantData(entry.TileId))
                {
                    LoadTile(entry.TileId);
                }
            }

            _memoryCache.RetainTiles(retainedTiles);

            return true;
        }

        public void SetRequestPriorityCenter(LatitudeLongitude center)
        {
            _requestPriorityCenter = center;
            _hasRequestPriorityCenter = true;
            foreach (var request in _waitingList)
            {
                if (request.Value != null)
                {
                    SetWebRequestPriority(
                        request.Key.TileId,
                        request.Key.TilesetId,
                        GetNormalizedTileDistanceSquared(request.Key.TileId));
                }
            }
        }

        private static int ComparePrioritizedTileId(PrioritizedTileId left, PrioritizedTileId right)
        {
            int distanceComparison = left.Distance.CompareTo(right.Distance);
            return distanceComparison != 0
                ? distanceComparison
                : left.TileId.Z.CompareTo(right.TileId.Z);
        }

        private readonly struct PrioritizedTileId
        {
            public readonly CanonicalTileId TileId;
            public readonly double Distance;

            public PrioritizedTileId(CanonicalTileId tileId, double distance)
            {
                TileId = tileId;
                Distance = distance;
            }
        }

        private double GetNormalizedTileDistanceSquared(CanonicalTileId tileId)
        {
            var centerTile = Conversions.LatitudeLongitudeToTileId(
                _requestPriorityCenter,
                tileId.Z).Canonical;
            int tileCount = 1 << tileId.Z;
            int deltaX = Math.Abs(tileId.X - centerTile.X);
            deltaX = Math.Min(deltaX, tileCount - deltaX);
            int deltaY = Math.Abs(tileId.Y - centerTile.Y);
            double normalizedX = (double)deltaX / tileCount;
            double normalizedY = (double)deltaY / tileCount;
            return normalizedX * normalizedX + normalizedY * normalizedY;
        }
        
        public override void CancelActiveRequests(CanonicalTileId unityTileId)
        {
            CancelActiveRequests(new RasterRequestKey(unityTileId, _tilesetId));
        }

        private void CancelActiveRequests(RasterRequestKey requestKey)
        {
            if (_waitingList.TryGetValue(requestKey, out var tile))
            {
                if (tile != null)
                {
                    CancelFetching(tile, requestKey.TilesetId);
                }

                _waitingList.Remove(requestKey);
                _asyncRequestVersions.Remove(requestKey);
            }
        }
        
        public override void DownloadAndCacheBaseTiles()
        {
            var backgroundTiles = new HashSet<CanonicalTileId>();
            for (int i = 0; i < 4; i++)
            {
                for (int j = 0; j < 4; j++)
                {
                    backgroundTiles.Add(new CanonicalTileId(2, i, j));
                }
            }

            for (int i = 0; i < 2; i++)
            {
                for (int j = 0; j < 2; j++)
                {
                    backgroundTiles.Add(new CanonicalTileId(1, i, j));
                }
            }

            backgroundTiles.Add(new CanonicalTileId(0, 0, 0));

            foreach (var tileId in backgroundTiles)
            {
                BackgroundLoad(tileId, _tilesetId);
            }
        }

        public virtual void ClearMemoryCache()
        {
            _memoryCache.OnDestroy();
        }

        public virtual void ClearInactiveMemoryCache()
        {
            _memoryCache.ClearInactive();
        }

        public IEnumerator ReloadTiles()
        {
            // IMPORTANT: Materialize keys BEFORE starting any coroutines to avoid
            // InvalidOperationException: Collection was modified during enumeration.
            // RefreshData modifies the cache (Remove/Add) while WaitForAll() would be
            // lazily enumerating the live dictionaries if we didn't snapshot them first.
            var activeKeys = _memoryCache.GetActiveData.Keys.ToList();
            var fallbackKeys = _memoryCache.GetFallbackData.Keys.ToList();

            var coroutines = activeKeys.Select(key => RefreshData(key));
            coroutines = coroutines.Concat(fallbackKeys.Select(key => RefreshData(key, data =>
            {
                _memoryCache.MarkFallback(key);
            })));

            yield return coroutines.WaitForAll();
            _memoryCache.ClearInactive();
        }

        public bool HasPreparedTileset(string tilesetId)
        {
            return _preparedTilesetData != null &&
                   _preparedTilesetData.Count > 0 &&
                   string.Equals(_preparedTilesetId, tilesetId, StringComparison.Ordinal);
        }

        public IEnumerator PrepareTileset(string tilesetId, IEnumerable<CanonicalTileId> tileIds)
        {
            DiscardPreparedTileset();
            int preparationVersion = _preparationVersion;

            var ids = new HashSet<CanonicalTileId>(tileIds);
            if (ids.Count == 0 || string.IsNullOrEmpty(tilesetId))
            {
                yield break;
            }

            // Keep the visible tileset's requests alive while staging this one. UnitySource
            // keys active requests by tile and tileset, so both covers can fetch in parallel
            // until CommitPreparedTileset atomically replaces the visible imagery.
            var preparedByTileId = new Dictionary<CanonicalTileId, T>(ids.Count);
            var coroutines = ids.Select(tileId => PrepareTile(
                tileId,
                tilesetId,
                preparationVersion,
                data =>
                {
                    if (data != null)
                    {
                        preparedByTileId[tileId] = data;
                    }
                }));
            yield return coroutines.WaitForAll();

            if (preparationVersion != _preparationVersion ||
                preparedByTileId.Count != ids.Count)
            {
                foreach (var data in preparedByTileId.Values)
                {
                    data.Dispose();
                }

                yield break;
            }

            _preparedTilesetId = tilesetId;
            _preparedTilesetData = preparedByTileId.Values.ToList();
        }

        public bool CommitPreparedTileset(string tilesetId)
        {
            if (!HasPreparedTileset(tilesetId))
            {
                return false;
            }

            CancelOutstandingTileRequests();
            _settings.TilesetId = tilesetId;
            _tilesetId = tilesetId;

            foreach (var data in _preparedTilesetData)
            {
                while (_memoryCache.Exists(data.TileId))
                {
                    _memoryCache.Remove(data.TileId);
                }

                _memoryCache.Add(data);
                CheckExpiration(data);
            }

            _preparedTilesetData = null;
            _preparedTilesetId = null;
            return true;
        }

        public void DiscardPreparedTileset()
        {
            unchecked
            {
                _preparationVersion++;
            }

            var preparingTiles = new List<RasterTile>(_preparedTileRequests);
            foreach (var tile in preparingTiles)
            {
                tile.Cancel();
            }
            _preparedTileRequests.Clear();

            if (_preparedTilesetData != null)
            {
                foreach (var data in _preparedTilesetData)
                {
                    data.Dispose();
                }
            }

            _preparedTilesetData = null;
            _preparedTilesetId = null;
        }

        public override IEnumerator ChangeTilesetId(string tilesetId)
        {
            // FORK yiiportal: cancel fetches still in flight for the previous tileset id
            // BEFORE switching (CancelFetching cancels under the id the fetch was keyed
            // with). Without this, ReloadTiles' in-flight coalescing adopts old-style
            // results and late responses repopulate the cache with stale imagery.
            CancelOutstandingTileRequests();

            _settings.TilesetId = tilesetId;
            _tilesetId = _settings.TilesetId;
            yield return Initialize();
            yield return ReloadTiles();
        }

        public override void OnDestroy()
        {
            base.OnDestroy();
            DiscardPreparedTileset();
            foreach (var tile in _waitingList)
            {
                tile.Value?.Cancel();
            }
            _asyncRequestVersions.Clear();
            _retainedTileIds.Clear();
            _memoryCache.OnDestroy();
        }

        //COROUTINE METHODS only used in initialization so far
        #region coroutines

        /// <summary>
        /// Shared fetch logic for tile data following the cache-check → file-cache → web-request fallback chain.
        /// </summary>
        /// <param name="requestedDataTileId">Tile ID to fetch</param>
        /// <param name="checkMemoryCacheFirst">If true, checks memory cache before starting fetch chain</param>
        /// <param name="clearExistingCache">If true, removes existing cache entry before adding new data</param>
        /// <param name="callback">Callback invoked with result (can be null)</param>
        private IEnumerator FetchTileDataCoroutine(
            CanonicalTileId requestedDataTileId,
            bool checkMemoryCacheFirst,
            bool clearExistingCache,
            Action<T> callback = null)
        {
            T resultData = null;
            string tilesetId = _tilesetId;
            var requestKey = new RasterRequestKey(requestedDataTileId, tilesetId);

            // STEP 1: Check memory cache if requested (LoadTileCoroutine does this, RefreshData doesn't)
            if (checkMemoryCacheFirst && GetInstantData(requestedDataTileId, out resultData))
            {
                callback?.Invoke(resultData);
                yield break;
            }

            // STEP 2: If already being fetched, wait for completion
            if (_waitingList.ContainsKey(requestKey))
            {
                while (_waitingList.ContainsKey(requestKey))
                {
                    yield return null;
                }
                GetInstantData(requestedDataTileId, out resultData);
                // FORK yiiportal: only short-circuit when the coalesced fetch produced
                // usable data; a fetch for a previous tileset id (cancelled or evicted
                // as stale by GetInstantData) must fall through to a fresh fetch chain.
                if (resultData != null)
                {
                    callback?.Invoke(resultData);
                    yield break;
                }
            }

            // STEP 3: Try file cache
            _waitingList[requestKey] = null;
            yield return GetImageCoroutine<T>(requestedDataTileId, tilesetId, _settings.UseNonReadableTextures,
                (data) =>
                {
                    resultData = data;
                    _waitingList.Remove(requestKey);

                    if (resultData != null)
                    {
                        // FORK yiiportal: a file-cache read started before a tileset
                        // switch returns previous-style data; drop it and fall through
                        // to the web fetch, which uses the current tileset id.
                        if (IsStaleStyle(data.TilesetId))
                        {
                            data.Dispose();
                            resultData = null;
                            return;
                        }

                        data.CacheType = CacheType.FileCache;

                        // Clear existing cache if requested (RefreshData does this)
                        if (clearExistingCache && _memoryCache.Exists(requestedDataTileId))
                            _memoryCache.Remove(requestedDataTileId);

                        _memoryCache.Add(data);
                        CheckExpiration(data);
                    }
                });

            // STEP 4: If not in file cache, fetch from web
            if (resultData == null)
            {
                var dataTile = CreateTile(requestedDataTileId, tilesetId);
                _waitingList[requestKey] = dataTile;
                var working = true;

                WebRequestData(dataTile, (fetchingResult) =>
                {
                    _waitingList.Remove(requestKey);

                    if (dataTile.CurrentTileState == TileState.Loaded)
                    {
                        // Clear existing cache entry if requested (for RefreshData)
                        if (clearExistingCache && _memoryCache.Exists(requestedDataTileId))
                            _memoryCache.Remove(requestedDataTileId);

                        // Process web response using shared method
                        resultData = TextureFromWebForCoroutine(dataTile);
                    }

                    working = false;
                }, GetRequestPriority(requestedDataTileId));

                while (working)
                {
                    yield return null;
                }

                // Canceled is the expected outcome of CancelOutstandingTileRequests (style
                // switch prepare/commit, ChangeTilesetId) — not a fetch failure, and warning
                // on it here would drown the genuine-failure signal this exists to surface.
                if (resultData == null && dataTile.CurrentTileState != TileState.Canceled)
                {
                    Debug.LogWarning($"[ImageSource] Coroutine fetch produced no data for {requestedDataTileId} ({tilesetId}) state={dataTile.CurrentTileState}");
                }
            }

            callback?.Invoke(resultData);
        }

        private IEnumerator PrepareTile(
            CanonicalTileId tileId,
            string tilesetId,
            int preparationVersion,
            Action<T> callback)
        {
            T resultData = null;
            yield return GetImageCoroutine<T>(tileId, tilesetId, _settings.UseNonReadableTextures, data =>
            {
                if (preparationVersion == _preparationVersion &&
                    data != null &&
                    string.Equals(data.TilesetId, tilesetId, StringComparison.Ordinal))
                {
                    data.CacheType = CacheType.FileCache;
                    resultData = data;
                }
                else
                {
                    data?.Dispose();
                }
            });

            if (resultData == null &&
                preparationVersion == _preparationVersion)
            {
                var dataTile = CreateTile(tileId, tilesetId);
                _preparedTileRequests.Add(dataTile);
                var isWorking = true;
                WebRequestData(dataTile, result =>
                {
                    _preparedTileRequests.Remove(dataTile);
                    if (preparationVersion == _preparationVersion &&
                        dataTile.CurrentTileState == TileState.Loaded &&
                        dataTile.Data != null)
                    {
                        dataTile.ExtractTextureFromRequest();
                        resultData = CreateRasterDataWrapper(dataTile);
                        SaveImage(resultData, true);
                    }

                    isWorking = false;
                }, GetRequestPriority(tileId));

                while (isWorking &&
                       preparationVersion == _preparationVersion)
                {
                    yield return null;
                }
            }

            callback?.Invoke(resultData);
        }

        private void CancelOutstandingTileRequests()
        {
            CancelAllActiveRequests();

            var inFlightRequestKeys = new List<RasterRequestKey>(_waitingList.Keys);
            foreach (var inFlightRequestKey in inFlightRequestKeys)
            {
                CancelActiveRequests(inFlightRequestKey);
            }

            _waitingList.Clear();
            _asyncRequestVersions.Clear();
        }

        public override IEnumerator LoadTileCoroutine(CanonicalTileId requestedDataTileId, Action<T> callback = null)
        {
            yield return FetchTileDataCoroutine(
                requestedDataTileId,
                checkMemoryCacheFirst: true,
                clearExistingCache: false,
                callback
            );
        }

        private IEnumerator RefreshData(CanonicalTileId requestedDataTileId, Action<T> callback = null)
        {
            yield return FetchTileDataCoroutine(
                requestedDataTileId,
                checkMemoryCacheFirst: false,
                clearExistingCache: true,
                callback
            );
        }
        
        public override IEnumerator LoadTilesCoroutine(IEnumerable<CanonicalTileId> retainedTiles, Action<List<T>> callback = null)
        {
            if(callback != null)
            {
                var results = new List<T>();
                var coroutines = retainedTiles.Select(x => LoadTileCoroutine(x, (data) => results.Add(data)));
                yield return coroutines.WaitForAll();
                callback?.Invoke(results);
            }
            else
            {
                var coroutines = retainedTiles.Select(x => LoadTileCoroutine(x));
                yield return coroutines.WaitForAll();
            }
        }
        #endregion
        
        
        
        
        protected abstract RasterTile CreateTile(CanonicalTileId tileId, string tilesetId);
        protected abstract T CreateRasterDataWrapper(RasterTile tile);
        
        private void LoadTileCore(CanonicalTileId requestedDataTileId, Action<T> callback = null)
        {
            string tilesetId = _tilesetId;
            var requestKey = new RasterRequestKey(requestedDataTileId, tilesetId);
            if (IsInProgress(requestKey))
            {
                callback?.Invoke(null);
                return;
            }
            int requestVersion = BeginAsyncRequest(requestKey);
            _waitingList[requestKey] = null;

            GetImageAsync<T>(requestedDataTileId, tilesetId, _settings.UseNonReadableTextures, (cacheItem) =>
            {
                bool isCurrentRequest =
                    IsCurrentAsyncRequest(requestKey, requestVersion) &&
                    _retainedTileIds.Contains(requestedDataTileId) &&
                    string.Equals(tilesetId, _tilesetId, StringComparison.Ordinal);
                bool isCurrentStyleData = cacheItem != null &&
                                          string.Equals(cacheItem.TilesetId, tilesetId, StringComparison.Ordinal);
                if (isCurrentRequest && isCurrentStyleData)
                {
                    TextureReceivedFromFile(cacheItem);
                    CheckExpiration(cacheItem);
                    CompleteAsyncRequest(requestKey, requestVersion);
                    callback?.Invoke(cacheItem);
                }
                else
                {
                    cacheItem?.Dispose();

                    if (!isCurrentRequest)
                    {
                        CompleteAsyncRequest(requestKey, requestVersion);
                        callback?.Invoke(null);
                        return;
                    }
                    
                    var dataTile = CreateTile(requestedDataTileId, tilesetId);
                    _waitingList[requestKey] = dataTile;
                    WebRequestData(dataTile, (fetchingResult) =>
                    {
                        T resultDataItem = null;
                        bool isCurrentWebRequest =
                            IsCurrentAsyncRequest(requestKey, requestVersion) &&
                            _retainedTileIds.Contains(requestedDataTileId) &&
                            string.Equals(tilesetId, _tilesetId, StringComparison.Ordinal);
                        if (isCurrentWebRequest &&
                            dataTile.CurrentTileState == TileState.Loaded)
                        {
                            resultDataItem = TextureReceivedFromWeb(dataTile);
                        }
                        else if (dataTile.CurrentTileState == TileState.Loaded)
                        {
                            // The tile left the cover (or the style changed) while it was
                            // downloading. Skipping the memory cache is the point, but the
                            // bytes are already paid for — persist them so panning back
                            // hits the file cache instead of refetching.
                            PersistWithoutDecoding(dataTile);
                        }
                        else if (isCurrentWebRequest &&
                                 dataTile.CurrentTileState != TileState.Canceled)
                        {
                            // Canceled is the expected outcome of a style switch's
                            // CancelOutstandingTileRequests, not a fetch failure.
                            Debug.LogWarning($"[ImageSource] Async fetch failed for {requestedDataTileId} ({tilesetId}) state={dataTile.CurrentTileState}");
                        }
                        CompleteAsyncRequest(requestKey, requestVersion);
                        callback?.Invoke(resultDataItem);
                    }, GetRequestPriority(requestedDataTileId));
                }
            });
        }

        private double? GetRequestPriority(CanonicalTileId tileId)
        {
            return _hasRequestPriorityCenter
                ? GetNormalizedTileDistanceSquared(tileId)
                : null;
        }

        // File-cache write only: no ExtractTextureFromRequest, so this never allocates a
        // Texture2D and never enters the memory cache. FileCache.SaveInfo persists
        // RasterData.Data, so the texture field is deliberately left null.
        private void PersistWithoutDecoding(RasterTile dataTile)
        {
            if (dataTile?.Data == null || dataTile.Data.Length == 0)
            {
                return;
            }

            SaveImage(
                new RasterData
                {
                    TileId = dataTile.Id,
                    TilesetId = dataTile.TilesetId,
                    Data = dataTile.Data,
                    ETag = dataTile.ETag,
                    ExpirationDate = dataTile.ExpirationDate,
                    CacheType = dataTile.FromCache
                },
                true);
        }
        
        protected virtual void TextureReceivedFromFile(T textureCacheItem)
        {
            // FORK yiiportal: never cache data fetched for a previous tileset id.
            if (IsStaleStyle(textureCacheItem.TilesetId))
            {
                textureCacheItem.Dispose();
                return;
            }

            //var tile = (RasterTile) textureCacheItem.Tile;
            //textureCacheItem.Tile = tile;
            //tile.SetTextureFromCache(textureCacheItem.Texture2D);
            //tile.FromCache = CacheType.FileCache;
            textureCacheItem.CacheType = CacheType.FileCache;

            //IMPORTANT file is read from file cache and it's not automatically
            //moved to memory cache. we have to do it here.
            _memoryCache.Add(textureCacheItem);
        }

        protected virtual T TextureReceivedFromWeb(RasterTile tile)
        {
            // FORK yiiportal: a web response that raced a tileset switch carries
            // previous-style imagery; drop it instead of caching it under a tile id
            // the new style will read.
            if (IsStaleStyle(tile.TilesetId))
            {
                Debug.Log($"[ImageSource] Dropped late {tile.TilesetId} response for {tile.Id} after switch to {_tilesetId}");
                return null;
            }

            tile.AddLog(string.Format("{0} - {1}", Time.unscaledTime, " TextureReceivedHandler"));
            if (tile.Texture2D != null)
            {
                tile.AddLog("updated and old texture is destroyed");
                GameObject.Destroy(tile.Texture2D);
            }

            if (tile.CurrentTileState == TileState.Loaded && tile.Data != null)
            {
                //IMPORTANT This is where we create a Texture2D
                tile.AddLog("extracting texture ", tile.Id);
                tile.ExtractTextureFromRequest();

                var newTextureCacheItem = CreateRasterDataWrapper(tile);

                _memoryCache.Add(newTextureCacheItem);
                SaveImage(newTextureCacheItem, true);

                return newTextureCacheItem;
            }

            return null;
        }

        //this is a clone of method above for terrain coroutine process
        //terrain source overrides method above and calls extract elevation data there
        //this one doesn't call extract data anywhere and it's handles separately during
        //coroutine stuff
        //this should be the one to stay in the future
        private T TextureFromWebForCoroutine(RasterTile tile)
        {
            // FORK yiiportal: see TextureReceivedFromWeb — drop previous-style responses.
            if (IsStaleStyle(tile.TilesetId))
            {
                Debug.Log($"[ImageSource] Dropped late {tile.TilesetId} response for {tile.Id} after switch to {_tilesetId}");
                return null;
            }

            tile.AddLog(string.Format("{0} - {1}", Time.unscaledTime, " TextureReceivedHandler"));
            if (tile.Texture2D != null)
            {
                tile.AddLog("updated and old texture is destroyed");
                GameObject.Destroy(tile.Texture2D);
            }

            if (tile.CurrentTileState == TileState.Loaded && tile.Data != null)
            {
                //IMPORTANT This is where we create a Texture2D
                tile.AddLog("extracting texture ", tile.Id);
                tile.ExtractTextureFromRequest();

                var newTextureCacheItem = CreateRasterDataWrapper(tile);

                _memoryCache.Add(newTextureCacheItem);
                SaveImage(newTextureCacheItem, true);

                return newTextureCacheItem;
            }

            return null;
        }
        
        
        protected void BackgroundLoad(CanonicalTileId tileId, string tilesetId)
        {
            GetImageAsync<T>(tileId, tilesetId, SystemInfo.supportsAsyncGPUReadback, (cacheItem) =>
            {
                if (cacheItem != null)
                {
                    TextureReceivedFromFile(cacheItem);
                    _memoryCache.MarkFallback(cacheItem.TileId);
                    CheckExpiration(cacheItem);
                }
                else
                {
                    var dataTile = CreateTile(tileId, tilesetId);
                    dataTile.IsBackgroundData = true;
                    WebRequestData(dataTile, (fetchingResult) =>
                    {
                        if (dataTile.CurrentTileState != TileState.Canceled)
                        {
                            TextureReceivedFromWeb(dataTile);
                            _memoryCache.MarkFallback(dataTile.Id);
                        }
                    });
                }
            });
        }

        protected virtual void CheckExpiration(T cacheItem)
        {
            var dataTask = ReadEtagExpiration(cacheItem, 4);
            if (dataTask != null) //can be null if sqlite cache isn't available
            {
                if (dataTask.IsCompleted) //supporting instant calls
                    OnDataTaskDataContinueWith(dataTask.DataResult);
                else
                    dataTask.DataCompleted += (task, data) => OnDataTaskDataContinueWith(data);
            }
        }

        private void OnDataTaskDataContinueWith(T data)
        {
            if (data.ExpirationDate == null || DateTime.Compare((DateTime)data.ExpirationDate, DateTime.Now) < 0)
            {
                TileExpired(data.TilesetId, data.TileId);
                var dataTile = CreateTile(data.TileId, data.TilesetId);
                dataTile.ETag = data.ETag;
                WebRequestUpdate(dataTile, (result) =>
                {
                    if (result.State == WebResponseResult.Failed)
                    {
                        Debug.LogError(result.ExceptionsAsString);
                        return;
                    }

                    if (result.State == WebResponseResult.Cancelled) return;
                    
                    var tile = result.Tile as RasterTile;
                    if (tile == null)
                        return;
                    
                    if (tile.StatusCode == 200)
                    {
                        //Debug.Log("expired and returned 200");
                        TextureReceivedFromWeb(tile);
                    }
                    else if (tile.StatusCode == 304)
                    {
                        //not changed, just update meta?
                        //Debug.Log("expired but not changed, just update meta?");
                        UpdateExpiration(tile.Id, tile.TilesetId, tile.ExpirationDate);
                    }

                    TileUpdated(tile.TilesetId, tile.Id);
                });
                //Debug.Log("tile needs an update");
            }
            else
            {
                //Debug.Log("doesnt needs an update");
            }
        }
        
        protected bool IsInProgress(RasterRequestKey requestKey)
        {
            return _waitingList.ContainsKey(requestKey);
        }

        private int BeginAsyncRequest(RasterRequestKey requestKey)
        {
            unchecked
            {
                _nextAsyncRequestVersion++;
            }

            if (_nextAsyncRequestVersion == 0)
            {
                _nextAsyncRequestVersion = 1;
            }

            _asyncRequestVersions[requestKey] = _nextAsyncRequestVersion;
            return _nextAsyncRequestVersion;
        }

        private bool IsCurrentAsyncRequest(RasterRequestKey requestKey, int requestVersion)
        {
            return _asyncRequestVersions.TryGetValue(requestKey, out int currentVersion) &&
                   currentVersion == requestVersion;
        }

        private void CompleteAsyncRequest(RasterRequestKey requestKey, int requestVersion)
        {
            if (!IsCurrentAsyncRequest(requestKey, requestVersion))
            {
                return;
            }

            _asyncRequestVersions.Remove(requestKey);
            _waitingList.Remove(requestKey);
        }

        protected readonly struct RasterRequestKey : IEquatable<RasterRequestKey>
        {
            public CanonicalTileId TileId { get; }
            public string TilesetId { get; }

            public RasterRequestKey(CanonicalTileId tileId, string tilesetId)
            {
                TileId = tileId;
                TilesetId = tilesetId;
            }

            public bool Equals(RasterRequestKey other)
            {
                return TileId.Equals(other.TileId) &&
                       string.Equals(TilesetId, other.TilesetId, StringComparison.Ordinal);
            }

            public override bool Equals(object obj)
            {
                return obj is RasterRequestKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (TileId.GetHashCode() * 397) ^ (TilesetId?.GetHashCode() ?? 0);
                }
            }
        }
    }
    
}
