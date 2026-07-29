using System;
using System.Collections;
using System.Collections.Generic;
using Mapbox.BaseModule.Data.Platform;
using Mapbox.BaseModule.Data.Platform.Cache;
using Mapbox.BaseModule.Data.Platform.TileJSON;
using Mapbox.BaseModule.Data.Tiles;
using Mapbox.BaseModule.Utilities;
using UnityEngine;

namespace Mapbox.BaseModule.Data.DataFetchers
{
	public class DataFetchingManager : IFileSource
	{
		/// <summary>
		/// A fetch command in queue is about the get started.
		/// </summary>
		public Action<FetchInfo> FetchInitialized = (t)=> {};
		
		/// <summary>
		/// This doesn't mean success or failure, it shows a fetch command fired from queue
		/// has finalized. Can be success, error, cancellation.
		/// Tile object inside the attached FetchInfo contains the details.
		/// </summary>
		public event Action<FetchInfo> FetchFinished = (t)=> {};
		
		/// <summary>
		/// This is an event for when data fetching command is removed from queue.
		/// Command may have been cancelled much earlier but getting removed from queue (this event)
		/// much later. This shouldn't be used for attaching logic to data fetching cancellation
		/// </summary>
		public event Action<FetchInfo> FetchCancelled = (t)=> {};
		
		protected float _requestDelay = 0.2f;
		protected IFileSource _fileSource;
		protected FetchRequestQueue _fetchQueue;
		protected HashSet<FetchInfo> _globalActiveRequests;
		protected int _activeRequestLimit = 30;
		private bool _isDestroying = false;
		private long _cancelledRequestCount;
		private long _completedRequestCount;
		private long _nextQueueSequence;
		private float _nextCancellationSweepTime;

		private const float CancellationSweepIntervalSeconds = 0.5f;

		public int QueuedRequestCount => _fetchQueue?.Count ?? 0;
		public int ActiveRequestCount => _globalActiveRequests?.Count ?? 0;
		public int ActiveRequestLimit => _activeRequestLimit;
		public long CancelledRequestCount => _cancelledRequestCount;
		public long CompletedRequestCount => _completedRequestCount;

		public DataFetchingManager(string getAccessToken, Func<string> getSkuToken)
		{
			_fileSource = new ResilientWebRequestFileSource(getAccessToken, getSkuToken);
			_fetchQueue = new FetchRequestQueue();
			_globalActiveRequests = new HashSet<FetchInfo>();
			Runnable.Run(UpdateTick());
		}

		public void SetRequestLimits(int concurrency, float delay)
		{
			_activeRequestLimit = Mathf.Max(1, concurrency);
			_requestDelay = Mathf.Max(0f, delay);
		}

		public virtual void EnqueueForFetching(FetchInfo info)
		{
			info.QueueTime = Time.time;
			info.QueueSequence = _nextQueueSequence++;
			_fetchQueue.Enqueue(info);
		}

		/// <summary>
		/// Re-prioritizes a request. Safe to call whether the request is still queued
		/// or already in flight; only a queued request is repositioned.
		/// </summary>
		public void SetRequestPriority(FetchInfo info, double priority)
		{
			if (info == null)
			{
				return;
			}

			info.Priority = priority;
			info.HasPriority = true;
			_fetchQueue?.OnPriorityChanged(info);
		}

		public virtual TileJSON GetTileJSON(int timeout = 10)
		{
			return new TileJSON(_fileSource, timeout);
		}
		
		public void OnDestroy()
		{
			_isDestroying = true;
			var activeRequests = new List<FetchInfo>(_globalActiveRequests);
			foreach (var activeRequest in activeRequests)
			{
				activeRequest.Tile.Cancel();
			}
			_globalActiveRequests.Clear();
			_fetchQueue.Clear();
			_fetchQueue = null;

			_fileSource.OnDestroy();
			_fileSource = null;
		}
		
		private IEnumerator UpdateTick()
		{
			while (!_isDestroying)
			{
				PurgeCancelledQueuedRequests();
				while (!_isDestroying && _fetchQueue.Count > 0 &&
				       _globalActiveRequests.Count < _activeRequestLimit)
				{
					// Only the head is maturity-checked. It is the highest-priority
					// request, so issuing a lower-priority mature one ahead of it would
					// defeat viewport-center prioritization; the delay is a short global
					// debounce, so waiting for the head is the intended pacing.
					var next = _fetchQueue.Peek();
					if (Application.isPlaying &&
					    !QueueTimeHasMatured(next.QueueTime, _requestDelay))
					{
						break;
					}

					var info = _fetchQueue.Dequeue();
					if (info.Tile.CurrentTileState == TileState.Canceled)
					{
						NotifyCancelled(info);
						continue;
					}

					_globalActiveRequests.Add(info);
					FetchInitialized(info);
					info.Tile.Initialize(
						_fileSource,
						(dataFetchingResult) =>
						{
							_globalActiveRequests.Remove(info);
							if (dataFetchingResult.State == WebResponseResult.Cancelled)
							{
								_cancelledRequestCount++;
							}
							else
							{
								_completedRequestCount++;
							}
							info.Callback(dataFetchingResult);
							FetchFinished?.Invoke(info);
						});
					yield return null;
				}
				yield return null;
			}
		}

		private void NotifyCancelled(FetchInfo info)
		{
			FetchCancelled(info);
			_cancelledRequestCount++;
			info.Callback(new DataFetchingResult { State = WebResponseResult.Cancelled });
		}

		// Cancelled entries are also caught at the head during dequeue; this sweep only
		// exists so a request cancelled deep in the queue still gets its callback
		// promptly. It is O(n), so it runs on an interval rather than every frame.
		private void PurgeCancelledQueuedRequests()
		{
			if (Time.unscaledTime < _nextCancellationSweepTime || _fetchQueue.Count == 0)
			{
				return;
			}

			_nextCancellationSweepTime = Time.unscaledTime + CancellationSweepIntervalSeconds;
			_fetchQueue.RemoveCancelled(NotifyCancelled);
		}

		private static bool QueueTimeHasMatured(float queueTime, float maturationAge)
		{
			return Time.time - queueTime >= maturationAge;
		}

		/// <summary>
		/// Binary min-heap ordered by (Priority, QueueSequence). Replaces the previous
		/// FIFO plus linear best-of scan, which was O(n) per selection and O(n) per frame
		/// to purge — pathological once an iOS-sized cover queues hundreds of tiles.
		/// </summary>
		protected sealed class FetchRequestQueue
		{
			private readonly List<FetchInfo> _heap = new List<FetchInfo>();

			public int Count => _heap.Count;

			public void Enqueue(FetchInfo info)
			{
				_heap.Add(info);
				info.QueueIndex = _heap.Count - 1;
				SiftUp(_heap.Count - 1);
			}

			public FetchInfo Peek() => _heap[0];

			public FetchInfo Dequeue()
			{
				var head = _heap[0];
				head.QueueIndex = -1;
				int lastIndex = _heap.Count - 1;
				if (lastIndex == 0)
				{
					_heap.Clear();
					return head;
				}

				Assign(0, _heap[lastIndex]);
				_heap.RemoveAt(lastIndex);
				SiftDown(0);
				return head;
			}

			public void OnPriorityChanged(FetchInfo info)
			{
				int index = info.QueueIndex;
				if (index < 0 || index >= _heap.Count || !ReferenceEquals(_heap[index], info))
				{
					return;
				}

				SiftDown(SiftUp(index));
			}

			public void RemoveCancelled(Action<FetchInfo> onCancelled)
			{
				var cancelled = new List<FetchInfo>();
				int surviving = 0;
				for (int i = 0; i < _heap.Count; i++)
				{
					var info = _heap[i];
					if (info.Tile.CurrentTileState == TileState.Canceled)
					{
						info.QueueIndex = -1;
						cancelled.Add(info);
						continue;
					}

					Assign(surviving++, info);
				}

				if (cancelled.Count == 0)
				{
					return;
				}

				_heap.RemoveRange(surviving, _heap.Count - surviving);
				for (int i = _heap.Count / 2 - 1; i >= 0; i--)
				{
					SiftDown(i);
				}

				// Callbacks can enqueue replacements, so they run after the heap is
				// consistent again.
				foreach (var info in cancelled)
				{
					onCancelled(info);
				}
			}

			public void Clear()
			{
				foreach (var info in _heap)
				{
					info.QueueIndex = -1;
				}

				_heap.Clear();
			}

			/// <summary>
			/// Queued entries in dequeue order. Diagnostics only — it copies and sorts.
			/// </summary>
			public List<FetchInfo> Snapshot()
			{
				var snapshot = new List<FetchInfo>(_heap);
				snapshot.Sort(Compare);
				return snapshot;
			}

			private int SiftUp(int index)
			{
				var info = _heap[index];
				while (index > 0)
				{
					int parent = (index - 1) / 2;
					if (Compare(_heap[parent], info) <= 0)
					{
						break;
					}

					Assign(index, _heap[parent]);
					index = parent;
				}

				Assign(index, info);
				return index;
			}

			private void SiftDown(int index)
			{
				var info = _heap[index];
				int half = _heap.Count / 2;
				while (index < half)
				{
					int child = index * 2 + 1;
					int right = child + 1;
					if (right < _heap.Count && Compare(_heap[right], _heap[child]) < 0)
					{
						child = right;
					}

					if (Compare(_heap[child], info) >= 0)
					{
						break;
					}

					Assign(index, _heap[child]);
					index = child;
				}

				Assign(index, info);
			}

			private void Assign(int index, FetchInfo info)
			{
				_heap[index] = info;
				info.QueueIndex = index;
			}

			// Unprioritized requests (vector, terrain, tile JSON) sort as priority 0 so
			// they stay FIFO-competitive instead of starving behind prioritized raster
			// work. Comparing on a single key also keeps the ordering transitive, which
			// a "both must have priority" comparison is not.
			private static int Compare(FetchInfo left, FetchInfo right)
			{
				int priorityComparison = left.Priority.CompareTo(right.Priority);
				return priorityComparison != 0
					? priorityComparison
					: left.QueueSequence.CompareTo(right.QueueSequence);
			}
		}


		#region IFileSource interface for direct access without queue
		public IAsyncRequest Request(string uri, Action<Response> callback, int timeout = 10)
		{
			return _fileSource.Request(uri, callback, timeout);
		}

		public IWebRequest MapboxImageRequest(string uri, Action<WebRequestResponse> callback, string etag = "", int timeout = 10,
			bool isNonReadable = true)
		{
			return _fileSource.MapboxImageRequest(uri, callback, etag, timeout, isNonReadable);	
		}

		public IWebRequest CustomImageRequest(string uri, Action<WebRequestResponse> callback, string etag = null, int timeout = 10,
			bool isNonReadable = true)
		{
			return _fileSource.CustomImageRequest(uri, callback, etag, timeout, isNonReadable);
		}

		public IWebRequest MapboxDataRequest(string uri, Action<WebRequestResponse> callback, string etag = "", int timeout = 10)
		{
			return _fileSource.MapboxDataRequest(uri, callback, etag, timeout);
		}
		#endregion
	}
}
