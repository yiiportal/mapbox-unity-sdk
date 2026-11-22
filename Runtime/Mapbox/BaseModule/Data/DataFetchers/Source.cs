using System;
using System.Collections;
using System.Collections.Generic;
using Mapbox.BaseModule.Data.Platform.Cache;
using Mapbox.BaseModule.Data.Tiles;

namespace Mapbox.BaseModule.Data.DataFetchers
{
	public abstract class Source
	{
		public abstract bool IsReady();
	}
	
	public abstract class Source<T> : Source
	{
		public virtual IEnumerator Initialize()
		{
			return null;
		}
		
		public virtual void SetupRasterData(T data)
		{

		}

		public virtual void LoadTile(CanonicalTileId tileId) { }

		public virtual IEnumerator LoadTileCoroutine(CanonicalTileId requestedDataTileId, Action<T> callback = null)
		{
			return null;
		}
		
		public virtual IEnumerator LoadTilesCoroutine(IEnumerable<CanonicalTileId> retainedTiles, Action<List<T>> callback = null)
		{
			callback?.Invoke(new List<T>());
			yield break;
		}

		public virtual void LoadBackgroundTile(CanonicalTileId tileId)
		{
			
		}
    
		public virtual void CancelActiveRequests(CanonicalTileId tileId)
		{

		}

		public virtual bool GetInstantData(CanonicalTileId requestedDataTileId, out T data)
		{
			data = default(T);
			return false;
		}

		public virtual void DisposeData(T data)
		{
			
		}

		public abstract bool RetainTiles(HashSet<CanonicalTileId> retainedTiles);

		public abstract bool CheckInstantData(CanonicalTileId tileIdCanonical);

		public virtual void InvalidateData(CanonicalTileId tileId)
		{
			
		}

		/// <summary>
		/// FORK yiiportal: drops every cached item that is not currently retained, e.g. on a memory
		/// warning. Retained items are untouched.
		/// </summary>
		public virtual void ClearInactiveData() { }

		// FORK yiiportal: suppression telemetry identifies tiles waiting out fetch failures.
		public virtual bool IsBackingOff(CanonicalTileId tileId) => false;

		public virtual bool HasTransientFailure(CanonicalTileId tileId) => false;

		// FORK yiiportal: pressure levels change the number of recently visible tiles retained.
		public virtual void SetInactiveCapacity(int capacity) { }

		// FORK yiiportal: spatial eviction delegates retention order to the tile family.
		public virtual void SetEvictionOrder(TileEvictionOrderSelector order) { }
		
		//move this to imageSource after merging imagery&terrain modules
		public virtual void DownloadAndCacheBaseTiles() { }
		
		// public virtual bool IsZinSupportedRange(int z)
		// {
		// 	return false;
		// }

		public Action<CanonicalTileId> CacheItemDisposed = (t) => { };

		public virtual void Cancel(CanonicalTileId tileId)
		{
			
		}

		public virtual IEnumerator ChangeTilesetId(string tilesetId)
		{
			yield break; // Exit immediately without waiting a frame
		}
		
		public virtual void OnDestroy()
		{
			
		}
	}
}
