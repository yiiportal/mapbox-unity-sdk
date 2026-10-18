using System;
using Mapbox.BaseModule.Data.Tiles;
using Mapbox.BaseModule.Data.Vector2d;
using Mapbox.BaseModule.Utilities;
using Mapbox.BaseModule.Utilities.Attributes;
using UnityEngine;

namespace Mapbox.BaseModule.Map
{
    [Serializable]
    public class DynamicScalingMapInformation : MapInformation
    {
        private float _initialScale;
        [SerializeField] private bool _useDynamicScaling = false;
        [SerializeField] private AnimationCurveContainer ScaleCurve;
        private bool _loggedInvalidScale;

        /// <summary>
        /// Number of times <see cref="EvaluateScale"/> rejected a non-finite or
        /// non-positive curve result. A wrapped or invalid scale is what let the tile
        /// cover explode to hundreds of tiles, so this stays observable rather than
        /// being silently corrected.
        /// </summary>
        public int InvalidScaleRejectionCount { get; private set; }

        public override float Scale
        {
            get => EvaluateScale(Zoom);
            protected set => _scale = value;
        }
        
        public DynamicScalingMapInformation(string latitudeLongitudeString, float initialScale, bool useDynamicScaling, AnimationCurveContainer scaleCurve) : base(latitudeLongitudeString)
        {
            _initialScale = initialScale;
            _useDynamicScaling = useDynamicScaling;
            ScaleCurve = scaleCurve;
        }

        public override void Initialize()
        {
            if(_isInitialized) return;
            Initialize(Conversions.StringToLatLon(_latitudeLongitudeString));
        }
        
        public override void Initialize(LatitudeLongitude latitudeLongitude)
        {
            if(_isInitialized) return;
            
            SetLatitudeLongitude(latitudeLongitude);
            _initialScale = _scale;
            if (_useDynamicScaling)
            {
                Scale = EvaluateScale(Zoom);
            }
            _isInitialized = true;
        }

        public override void SetInformation(LatitudeLongitude? latlng, float? zoom = null, float? pitch = null, float? bearing = null,
            float? scale = null)
        {
            var worldScaleChanged = zoom.HasValue && !Mathf.Approximately(zoom.Value, Zoom);
            base.SetInformation(latlng, zoom, pitch, bearing, scale);

            if (worldScaleChanged)
            {
                OnWorldScaleChanged();
            }
        }

        public override float GetScaleFor(float zoomValue) => Scale = EvaluateScale(zoomValue);

        private float EvaluateScale(float zoomValue)
        {
            if (!_useDynamicScaling || ScaleCurve == null)
            {
                return _scale;
            }

            float scale = _initialScale * ScaleCurve.EvaluateClamped(zoomValue);
            if (float.IsNaN(scale) || float.IsInfinity(scale) || scale <= 0f)
            {
                InvalidScaleRejectionCount++;
                if (!_loggedInvalidScale)
                {
                    _loggedInvalidScale = true;
                    Debug.LogWarning(
                        $"DynamicScalingMapInformation: rejected scale {scale} for zoom {zoomValue} (initialScale={_initialScale}); keeping the last valid scale. Further rejections are counted in InvalidScaleRejectionCount.");
                }

                return _scale > 0f ? _scale : Mathf.Max(_initialScale, 0.0001f);
            }

            return scale;
        }
    }
}
