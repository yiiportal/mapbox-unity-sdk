using System;
using System.Collections.Generic;
using System.Linq;
using Mapbox.BaseModule.Data.Tiles;
using Mapbox.LandmarksModule;
using NUnit.Framework;

namespace Mapbox.LandmarksModuleTests
{
    public sealed class SpatialRingPolicyTests
    {
        private static readonly CanonicalTileId Center = new(14, 1000, 1000);
        private static readonly CanonicalTileId Floor = new(14, 1000, 1000);
        private static readonly CanonicalTileId Ahead = new(14, 1000, 999);
        private static readonly CanonicalTileId Behind = new(14, 1000, 1001);
        private static readonly CanonicalTileId Side = new(14, 1001, 1000);

        [Test]
        public void HeadingBiasEvictsBehindAndSideBeforeAhead()
        {
            var policy = CreatePolicy();
            policy.BeginRetainPass(Center, bearingDegrees: 0f, 100f, new[] { Floor });

            var order = policy.GetRetentionOrder()(new[] { Behind, Side, Ahead, Floor }).ToArray();

            CollectionAssert.AreEqual(new[] { Behind, Side, Ahead, Floor }, order);
        }

        [Test]
        public void MinimumResidencyKeepsRecentlySeenTileInsideExitWindow()
        {
            var farRecent = new CanonicalTileId(14, 1004, 1000);
            var farStale = new CanonicalTileId(14, 997, 1000);
            var policy = CreatePolicy();
            policy.BeginRetainPass(Center, bearingDegrees: 0f, 10f, Array.Empty<CanonicalTileId>());
            policy.Touch(farStale, -10f);
            policy.Touch(farRecent, 4.9f);

            var order = policy.GetRetentionOrder()(new[] { farRecent, farStale }).ToArray();

            CollectionAssert.AreEqual(new[] { farStale, farRecent }, order);
        }

        [Test]
        public void ExpiredBehindTileEvictsBeforeSideTile()
        {
            var policy = CreatePolicy();
            policy.BeginRetainPass(Center, bearingDegrees: 0f, 0f, Array.Empty<CanonicalTileId>());
            policy.Touch(Behind, 0f);
            policy.BeginRetainPass(Center, bearingDegrees: 0f, 20f, Array.Empty<CanonicalTileId>());

            var order = policy.GetRetentionOrder()(new[] { Behind, Side }).ToArray();

            CollectionAssert.AreEqual(new[] { Behind, Side }, order);
        }

        [Test]
        public void ProtectedFloorSortsLastForEviction()
        {
            var policy = CreatePolicy();
            policy.BeginRetainPass(Center, bearingDegrees: 0f, 100f, new[] { Floor });

            var order = policy.GetRetentionOrder()(new[] { Floor, Ahead, Behind }).ToArray();

            Assert.AreEqual(Floor, order.Last());
        }

        private static SpatialRingPolicy CreatePolicy() => new();
    }
}
