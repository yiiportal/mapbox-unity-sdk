using System;
using System.Collections.Generic;
using System.Linq;
using Mapbox.BaseModule.Data.Platform.Cache;
using Mapbox.BaseModule.Data.Tiles;
using UnityEngine;

namespace Mapbox.LandmarksModule
{
    /// <summary>
    /// FORK yiiportal: landmark inactive retention is nearest/heading-biased first, with a warm
    /// exit radius and minimum residency. Hysteresis only delays eviction; admission is immediate.
    /// </summary>
    public sealed class SpatialRingPolicy
    {
        public const float MinimumResidencySeconds = 15f;
        public const float WarmRingRadius = 1f;
        public const float ExitRingRadius = 2f;
        private const float HeadingBiasTiles = 0.5f;

        private CanonicalTileId _center;
        private float _bearingRadians;
        private float _retainPassTime;
        private bool _hasContext;
        private readonly Dictionary<CanonicalTileId, double> _ringDistances = new();
        private readonly Dictionary<CanonicalTileId, float> _lastSeenTimes = new();
        private readonly HashSet<CanonicalTileId> _protectedTiles = new();

        public void BeginRetainPass(
            CanonicalTileId center,
            float bearingDegrees,
            float timeSeconds,
            IEnumerable<CanonicalTileId> protectedTiles)
        {
            _center = center;
            _bearingRadians = bearingDegrees * Mathf.Deg2Rad;
            _retainPassTime = timeSeconds;
            _hasContext = true;
            _protectedTiles.Clear();
            foreach (var tileId in protectedTiles ?? Enumerable.Empty<CanonicalTileId>())
            {
                _protectedTiles.Add(tileId);
                _lastSeenTimes[tileId] = timeSeconds;
            }
        }

        public void Touch(CanonicalTileId tileId, float timeSeconds)
        {
            _lastSeenTimes[tileId] = timeSeconds;
        }

        public TileEvictionOrderSelector GetRetentionOrder()
        {
            return SortRetentionOrder;
        }

        private IEnumerable<CanonicalTileId> SortRetentionOrder(
            IReadOnlyCollection<CanonicalTileId> inactiveKeys)
        {
            PruneUnknownTiles(inactiveKeys);
            return inactiveKeys
                .OrderBy(GetRetentionRank)
                .ThenByDescending(GetLastSeen)
                .ThenBy(tileId => tileId.GetHashCode());
        }

        private int GetRetentionRank(CanonicalTileId tileId)
        {
            if (_protectedTiles.Contains(tileId))
                return 0;

            if (!_hasContext)
                return 1;

            double ringDistance = GetRingDistance(tileId);
            _ringDistances[tileId] = ringDistance;
            bool isInsideExitRing = ringDistance <= ExitRingRadius;
            bool isInsideMinimumResidency =
                _lastSeenTimes.TryGetValue(tileId, out float lastSeen) &&
                _retainPassTime - lastSeen < MinimumResidencySeconds;
            return isInsideExitRing || isInsideMinimumResidency ? 1 : 2;
        }

        private double GetRingDistance(CanonicalTileId tileId)
        {
            if (tileId.Z != _center.Z)
                return double.MaxValue;

            int tileCount = 1 << tileId.Z;
            int deltaX = tileId.X - _center.X;
            int deltaY = tileId.Y - _center.Y;
            if (deltaX > tileCount / 2) deltaX -= tileCount;
            if (deltaX < -tileCount / 2) deltaX += tileCount;

            double forwardX = Math.Sin(_bearingRadians);
            double forwardY = -Math.Cos(_bearingRadians);
            double signedForward = deltaX * forwardX + deltaY * forwardY;
            double chebyshevDistance = Math.Max(Math.Abs(deltaX), Math.Abs(deltaY));
            return Math.Max(0d, chebyshevDistance - Math.Max(0d, signedForward) * HeadingBiasTiles);
        }

        private float GetLastSeen(CanonicalTileId tileId) =>
            _lastSeenTimes.TryGetValue(tileId, out float lastSeen) ? lastSeen : float.NegativeInfinity;

        private void PruneUnknownTiles(IReadOnlyCollection<CanonicalTileId> inactiveKeys)
        {
            var activeKeys = new HashSet<CanonicalTileId>(inactiveKeys);
            foreach (var tileId in _lastSeenTimes.Keys.ToArray())
            {
                if (!activeKeys.Contains(tileId) && !_protectedTiles.Contains(tileId))
                {
                    _lastSeenTimes.Remove(tileId);
                    _ringDistances.Remove(tileId);
                }
            }
        }
    }
}
