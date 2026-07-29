using System;
using Mapbox.BaseModule.Data.Tiles;

namespace Mapbox.BaseModule.Data.DataFetchers
{
    public class FetchInfo
    {
        public Action<DataFetchingResult> Callback;
        public Tile Tile;
        public float QueueTime;
        public bool IsUpdate = false;
        public bool HasPriority;
        public double Priority;
        internal long QueueSequence;

        // Maintained by DataFetchingManager's queue so a priority change can reposition
        // an entry in place. -1 means "not queued" (in flight, or already dequeued).
        internal int QueueIndex = -1;

        public FetchInfo(Tile tile, Action<DataFetchingResult> callback = null)
        {
            Tile = tile;
            Callback = callback;
        }

    }
}
