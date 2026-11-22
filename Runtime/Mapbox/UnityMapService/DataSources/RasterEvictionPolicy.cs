using System;
using System.Collections.Generic;
using System.Linq;
using Mapbox.BaseModule.Data.Tiles;

namespace Mapbox.UnityMapService.DataSources
{
    /// <summary>
    /// FORK yiiportal: inactive raster eviction order; the first key is evicted first. Tiles in
    /// the farthest z10 area go first, least recently used within an area (inactive keys arrive
    /// newest first), and the z0-z4 fallback pyramid goes last, so a pan back finds its
    /// neighbourhood intact.
    /// </summary>
    public static class RasterEvictionPolicy
    {
        public const int EvictionAreaZoom = 10;
        private const int FallbackPyramidMaximumZoom = 4;

        public static IEnumerable<CanonicalTileId> Order(
            IReadOnlyCollection<CanonicalTileId> inactiveKeysNewestFirst,
            CanonicalTileId centerArea)
        {
            return inactiveKeysNewestFirst
                .Select((tileId, recencyIndex) => (tileId, recencyIndex))
                .OrderBy(entry => entry.tileId.Z <= FallbackPyramidMaximumZoom ? 1 : 0)
                .ThenByDescending(entry => GetAreaDistance(entry.tileId, centerArea))
                .ThenByDescending(entry => entry.recencyIndex)
                .Select(entry => entry.tileId);
        }

        private static double GetAreaDistance(CanonicalTileId tileId, CanonicalTileId centerArea)
        {
            var area = tileId.Z >= EvictionAreaZoom
                ? tileId.ParentAt(EvictionAreaZoom)
                : new CanonicalTileId(
                    EvictionAreaZoom,
                    tileId.X << (EvictionAreaZoom - tileId.Z),
                    tileId.Y << (EvictionAreaZoom - tileId.Z));
            int tileCount = 1 << EvictionAreaZoom;
            int deltaX = Math.Abs(area.X - centerArea.X);
            int deltaY = Math.Abs(area.Y - centerArea.Y);
            deltaX = Math.Min(deltaX, tileCount - deltaX);
            return Math.Sqrt((double)deltaX * deltaX + (double)deltaY * deltaY);
        }
    }
}
