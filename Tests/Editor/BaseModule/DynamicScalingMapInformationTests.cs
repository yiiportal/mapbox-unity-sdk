using Mapbox.BaseModule.Map;
using NUnit.Framework;
using UnityEngine;

namespace Mapbox.BaseModuleTests
{
    /// <summary>
    /// Zoom-boundary coverage for the dynamic scale curve. A wrapped evaluation above the
    /// authored range produced a discontinuously large scale, which shrank tile bounds and
    /// let the frustum admit hundreds of tiles.
    /// </summary>
    public class DynamicScalingMapInformationTests
    {
        private const float MinimumAuthoredZoom = 3.5f;
        private const float MaximumAuthoredZoom = 18f;
        private const float InitialScale = 1000f;

        private AnimationCurveContainer _curveContainer;

        [TearDown]
        public void TearDown()
        {
            if (_curveContainer != null)
            {
                Object.DestroyImmediate(_curveContainer);
            }
        }

        [Test]
        public void GetScaleFor_AboveAuthoredRange_MatchesUpperBoundary()
        {
            var mapInformation = CreateMapInformation(CreateLoopingCurve());

            float boundaryScale = mapInformation.GetScaleFor(MaximumAuthoredZoom);
            float aboveRangeScale = mapInformation.GetScaleFor(MaximumAuthoredZoom + 6f);

            Assert.AreEqual(boundaryScale, aboveRangeScale, 0.001f);
        }

        [Test]
        public void GetScaleFor_BelowAuthoredRange_MatchesLowerBoundary()
        {
            var mapInformation = CreateMapInformation(CreateLoopingCurve());

            float boundaryScale = mapInformation.GetScaleFor(MinimumAuthoredZoom);
            float belowRangeScale = mapInformation.GetScaleFor(MinimumAuthoredZoom - 3f);

            Assert.AreEqual(boundaryScale, belowRangeScale, 0.001f);
        }

        [Test]
        public void GetScaleFor_AcrossAuthoredRange_StaysFiniteAndPositive()
        {
            var mapInformation = CreateMapInformation(CreateLoopingCurve());

            for (float zoom = MinimumAuthoredZoom - 5f; zoom <= MaximumAuthoredZoom + 10f; zoom += 0.25f)
            {
                float scale = mapInformation.GetScaleFor(zoom);
                Assert.IsFalse(float.IsNaN(scale), $"NaN scale at zoom {zoom}");
                Assert.IsFalse(float.IsInfinity(scale), $"Infinite scale at zoom {zoom}");
                Assert.Greater(scale, 0f, $"Non-positive scale at zoom {zoom}");
            }

            Assert.AreEqual(0, mapInformation.InvalidScaleRejectionCount);
        }

        [Test]
        public void GetScaleFor_NonPositiveCurveResult_KeepsLastValidScaleAndCounts()
        {
            var mapInformation = CreateMapInformation(CreateZeroCurve());
            int rejectionsBefore = mapInformation.InvalidScaleRejectionCount;

            float scale = mapInformation.GetScaleFor(MaximumAuthoredZoom);

            Assert.Greater(scale, 0f);
            Assert.AreEqual(rejectionsBefore + 1, mapInformation.InvalidScaleRejectionCount);
        }

        [Test]
        public void EvaluateClamped_EmptyCurve_ReturnsNeutralMultiplier()
        {
            _curveContainer = ScriptableObject.CreateInstance<AnimationCurveContainer>();
            _curveContainer.Curve = new AnimationCurve();

            Assert.AreEqual(1f, _curveContainer.EvaluateClamped(MaximumAuthoredZoom + 20f));
        }

        private DynamicScalingMapInformation CreateMapInformation(AnimationCurveContainer curve)
        {
            var mapInformation = new DynamicScalingMapInformation(
                "40.7128,-74.0060",
                InitialScale,
                true,
                curve);
            mapInformation.Initialize();
            return mapInformation;
        }

        // Mirrors the shipped MapScaleCurve asset: authored only between 3.5 and 18, with
        // loop pre/post wrap modes that would otherwise wrap an out-of-range zoom back to
        // the high-scale start of the curve.
        private AnimationCurveContainer CreateLoopingCurve()
        {
            var curve = new AnimationCurve(
                new Keyframe(MinimumAuthoredZoom, 8f),
                new Keyframe(MaximumAuthoredZoom, 0.5f))
            {
                preWrapMode = WrapMode.Loop,
                postWrapMode = WrapMode.Loop
            };
            _curveContainer = ScriptableObject.CreateInstance<AnimationCurveContainer>();
            _curveContainer.Curve = curve;
            return _curveContainer;
        }

        private AnimationCurveContainer CreateZeroCurve()
        {
            _curveContainer = ScriptableObject.CreateInstance<AnimationCurveContainer>();
            _curveContainer.Curve = new AnimationCurve(
                new Keyframe(MinimumAuthoredZoom, 0f),
                new Keyframe(MaximumAuthoredZoom, 0f));
            return _curveContainer;
        }
    }
}
