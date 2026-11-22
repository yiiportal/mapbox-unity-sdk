using System;
using System.Collections.Generic;
using System.Linq;
using Mapbox.BaseModule.Data.Platform.Cache;
using Mapbox.BaseModule.Data.Tiles;
using UnityEngine;

namespace Mapbox.LandmarksModule
{
    /// <summary>
    /// FORK yiiportal: landmark eviction is cold/opposite-heading first; protected and warm
    /// tiles sort last. Minimum residency is an eviction-only delay; admission is immediate.
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
            _ringDistances.Clear();
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
            if (!_hasContext)
                return inactiveKeys.Reverse();

            // First key is evicted first: cold before warm before protected; within a band the
            // farthest (heading-adjusted) tile goes first, then the least recently seen.
            return inactiveKeys
                .OrderBy(GetRetentionBand)
                .ThenByDescending(GetRingDistance)
                .ThenBy(GetLastSeen)
                .ThenBy(tileId => tileId.GetHashCode());
        }

        private int GetRetentionBand(CanonicalTileId tileId)
        {
            if (_protectedTiles.Contains(tileId))
                return 2;

            bool isInsideExitRing = GetRingDistance(tileId) <= ExitRingRadius;
            bool isInsideMinimumResidency =
                _lastSeenTimes.TryGetValue(tileId, out float lastSeen) &&
                _retainPassTime - lastSeen < MinimumResidencySeconds;
            return isInsideExitRing || isInsideMinimumResidency ? 1 : 0;
        }

        /// <summary>
        /// Chebyshev tile distance from the centre, shortened ahead of the camera heading and
        /// lengthened behind it, so tiles the camera is moving away from are evicted first.
        /// </summary>
        private double GetRingDistance(CanonicalTileId tileId)
        {
            if (tileId.Z != _center.Z)
                return double.MaxValue;

            if (_ringDistances.TryGetValue(tileId, out double cached))
                return cached;

            var (deltaX, deltaY) = GetWrappedTileDelta(tileId);
            double chebyshevDistance = Math.Max(Math.Abs(deltaX), Math.Abs(deltaY));
            double distance = Math.Max(
                0d,
                chebyshevDistance - GetSignedForwardDistance(deltaX, deltaY) * HeadingBiasTiles);
            _ringDistances[tileId] = distance;
            return distance;
        }

        private (int DeltaX, int DeltaY) GetWrappedTileDelta(CanonicalTileId tileId)
        {
            if (tileId.Z != _center.Z)
            {
                return (0, 0);
            }

            int tileCount = 1 << tileId.Z;
            int deltaX = tileId.X - _center.X;
            int deltaY = tileId.Y - _center.Y;
            if (deltaX > tileCount / 2) deltaX -= tileCount;
            if (deltaX < -tileCount / 2) deltaX += tileCount;
            return (deltaX, deltaY);
        }

        private double GetSignedForwardDistance(int deltaX, int deltaY)
        {
            double forwardX = Math.Sin(_bearingRadians);
            double forwardY = -Math.Cos(_bearingRadians);
            return deltaX * forwardX + deltaY * forwardY;
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
