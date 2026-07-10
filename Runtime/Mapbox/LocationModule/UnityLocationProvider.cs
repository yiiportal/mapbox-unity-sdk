using System;
using Mapbox.BaseModule.Data.Vector2d;
using Mapbox.BaseModule.Utilities;
using Mapbox.LocationModule.AngleSmoothing;
using Mapbox.LocationModule.UnityLocationWrappers;
using UnityEngine;
using UnityEngine.Android;
using UnityEngine.Scripting;

namespace Mapbox.LocationModule
{
    [Serializable]
    public class UnityLocationProviderSettings
    {
        /// <summary>
        /// Using higher value like 500 usually does not require to turn GPS chip on and thus saves battery power. 
        /// Values like 5-10 could be used for getting best accuracy.
        /// </summary>
        [SerializeField]
        [Tooltip(
            "Using higher value like 500 usually does not require to turn GPS chip on and thus saves battery power. Values like 5-10 could be used for getting best accuracy.")]
        public float DesiredAccuracyInMeters = 1.0f;

        /// <summary>
        /// The minimum distance (measured in meters) a device must move laterally before Input.location property is updated. 
        /// Higher values like 500 imply less overhead.
        /// </summary>
        [SerializeField]
        [Tooltip(
            "The minimum distance (measured in meters) a device must move laterally before Input.location property is updated. Higher values like 500 imply less overhead.")]
        public float UpdateDistanceInMeters = 0.0f;

        [SerializeField]
        [Tooltip(
            "The minimum time interval between location updates, in milliseconds. It's reasonable to not go below 500ms.")]
        public long UpdateTimeInMilliSeconds = 500;

        [SerializeField] [Tooltip("Smoothing strategy to be applied to the UserHeading.")]
        public AngleSmoothingAbstractBase UserHeadingSmoothing;

        [SerializeField] [Tooltip("Smoothing strategy to applied to the DeviceOrientation.")]
        public AngleSmoothingAbstractBase DeviceOrientationSmoothing;
    }

    /// <summary>
    /// The DeviceLocationProvider is responsible for providing real world location and heading data,
    /// served directly from native hardware and OS. 
    /// This relies on Unity's <see href="https://docs.unity3d.com/ScriptReference/LocationService.html">LocationService</see> for location
    /// and <see href="https://docs.unity3d.com/ScriptReference/Compass.html">Compass</see> for heading.
    /// </summary>
    public class UnityLocationProvider : AbstractLocationProvider
    {
        private IMapboxLocationService _locationService;
        private double _lastLocationTimestamp;
        private float _lastPollTime;
        private float _updateInterval;

        /// <summary>list of positions to keep for calculations</summary>
        private CircularBuffer<LatitudeLongitude> _lastPositions;

        /// <summary>number of last positons to keep</summary>
        private int _maxLastPositions = 5;

        /// <summary>minimum needed distance between oldest and newest position before UserHeading is calculated</summary>
        private double _minDistanceOldestNewestPosition = 1.5;

        private readonly UnityLocationProviderSettings _unityLocationProviderSettings = new UnityLocationProviderSettings();

        private const string FineLocation = Permission.FineLocation;

        public UnityLocationProviderSettings UnityLocationProviderSettings => _unityLocationProviderSettings;

        /// <summary>
        /// Routes through <see cref="MapboxLocationServiceUnityWrapper"/>'s
        /// LocationAuthorizationBridge gate on iOS so callers (LocationProviderFactory's
        /// startup coroutine) don't trip Main Thread Checker against
        /// Input.location.isEnabledByUser before native authorization has been observed.
        /// </summary>
        public bool IsEnabledByUser => _locationService.isEnabledByUser;

        /// <summary>
        /// Constructor for production use - uses Unity's real location service.
        /// </summary>
        [Preserve]
        public UnityLocationProvider(UnityLocationProviderSettings settings) : this(settings, new MapboxLocationServiceUnityWrapper())
        {
        }

        /// <summary>
        /// Constructor for testing - accepts mock location service.
        /// Use MapboxLocationServiceMock to replay location logs for testing.
        /// </summary>
        public UnityLocationProvider(UnityLocationProviderSettings settings, IMapboxLocationService locationService)
        {
            _unityLocationProviderSettings = settings ?? throw new ArgumentNullException(nameof(settings));
            _locationService = locationService ?? throw new ArgumentNullException(nameof(locationService));
            //HandlePermission();

            _currentLocation.Provider = "unity";
            _updateInterval = UnityLocationProviderSettings.UpdateTimeInMilliSeconds < 500
                ? 0.5f
                : (float)UnityLocationProviderSettings.UpdateTimeInMilliSeconds / 1000.0f;

            if (null == UnityLocationProviderSettings.UserHeadingSmoothing)
            {
                UnityLocationProviderSettings.UserHeadingSmoothing = new AngleSmoothingNoOp();
            }

            if (null == UnityLocationProviderSettings.DeviceOrientationSmoothing)
            {
                UnityLocationProviderSettings.DeviceOrientationSmoothing = new AngleSmoothingNoOp();
            }

            _lastPositions = new CircularBuffer<LatitudeLongitude>(_maxLastPositions);
        }

        // Foreground power tiering (iOS_App_Review_Gaps.md "Foreground GPS and
        // compass tiering"): scenes without live map-follow/AR don't need
        // best-accuracy fixes or the compass. Coarse mode restarts the OS
        // service with relaxed parameters and disables the compass; fine mode
        // restores the serialized values. Authorization is untouched — only a
        // running service is cycled, so the #133 authorization-observed start
        // path in LocationProviderFactory.Initialize stays the single owner of
        // first start.
        private const float CoarseDesiredAccuracyInMeters = 10.0f;
        private const float CoarseUpdateDistanceInMeters = 25.0f;
        private bool _coarsePowerMode;

        private float ActiveDesiredAccuracyInMeters => _coarsePowerMode ? CoarseDesiredAccuracyInMeters : UnityLocationProviderSettings.DesiredAccuracyInMeters;
        private float ActiveUpdateDistanceInMeters => _coarsePowerMode ? CoarseUpdateDistanceInMeters : UnityLocationProviderSettings.UpdateDistanceInMeters;

        /// <summary>
        /// Starts (or retunes an already-running) location session at the
        /// currently active accuracy tier. The sole entry point for touching
        /// _locationService.Start — called once by LocationProviderFactory
        /// after its permission/authorization gate clears, and again by
        /// SetCoarsePowerMode to retune a live session. Routing both through
        /// here means a coarse-mode request made before the first Start() is
        /// still honored: ActiveDesiredAccuracyInMeters/ActiveUpdateDistanceInMeters
        /// always reflect the current _coarsePowerMode, however it was set.
        /// </summary>
        public void Start()
        {
            _locationService.Start(ActiveDesiredAccuracyInMeters, ActiveUpdateDistanceInMeters);
            Input.compass.enabled = !_coarsePowerMode;
        }

        /// <summary>
        /// Pushes one final disabled-state location update and stops delivering
        /// fixes. Called by LocationProviderFactory when its startup coroutine
        /// gives up before ever calling Start() (permission denied, iOS
        /// authorization-observation timeout) — without this, OnLocationUpdated
        /// never fires on those paths and subscribers only ever see silence.
        /// </summary>
        public void SetUnavailable()
        {
            _currentLocation.IsLocationServiceEnabled = false;
            SendLocation(_currentLocation);
        }

        public void SetCoarsePowerMode(bool coarse)
        {
            if (_coarsePowerMode == coarse)
            {
                return;
            }
            _coarsePowerMode = coarse;

            // Not delivering fixes yet (authorization wait, denied, or still
            // initializing) — the eventual Start() call picks up the active
            // mode via ActiveDesiredAccuracyInMeters/ActiveUpdateDistanceInMeters;
            // nothing to cycle now. IsLocationServiceEnabled (not a strict status
            // check) matches Update()'s own tolerant "is this service usable"
            // read, which on some devices stays true through transient status
            // blips that aren't exactly Running while fixes keep arriving.
            if (_locationService == null || !_currentLocation.IsLocationServiceEnabled)
            {
                return;
            }

            // Re-issuing Start() on an already-running service retunes its accuracy/
            // distance parameters in place (Unity's documented behavior for calling
            // Start() while LocationServiceStatus is Running) — no Stop() first.
            // Stopping and restarting the platform session invalidates the current
            // fix and forces a fresh acquisition, producing a real GPS gap on every
            // Map<->other-scene tier flip; a bare Start() call updates the live
            // session without that gap, and status stays Running throughout so no
            // re-init/NotifyServiceStarted signal is needed.
            Start();
        }

        public override void Update()
        {
            if (Time.realtimeSinceStartup - _lastPollTime < _updateInterval)
                return;
            _lastPollTime = Time.realtimeSinceStartup;

            // Check service status BEFORE reading lastData. Reading
            // Input.location.lastData while the service is not running triggers
            // Unity's "Location service updates are not enabled" warning and
            // returns stale/zero data.
            var serviceRunning = _locationService.status == LocationServiceStatus.Running;
            if (!serviceRunning && _lastLocationTimestamp == 0)
            {
                // Service has never delivered a fix — nothing to read yet.
                _currentLocation.IsLocationServiceEnabled = false;
                _currentLocation.IsUserHeadingUpdated = false;
                _currentLocation.IsLocationUpdated = false;
                return;
            }

            // Service is running, or it was running previously and we have a
            // valid last timestamp (transient status blip). Safe to read.
            var lastData = _locationService.lastData;
            var timestamp = lastData.timestamp;

            _currentLocation.IsLocationServiceEnabled =
                serviceRunning
                || timestamp > _lastLocationTimestamp;

            _currentLocation.IsUserHeadingUpdated = false;
            _currentLocation.IsLocationUpdated = false;

            if (!_currentLocation.IsLocationServiceEnabled)
                return;

            // device orientation, user heading get calculated below
            UnityLocationProviderSettings.DeviceOrientationSmoothing.Add(Input.compass.trueHeading);
            _currentLocation.DeviceOrientation =
                (float)UnityLocationProviderSettings.DeviceOrientationSmoothing.Calculate();

            double latitude = lastData.latitude;
            double longitude = lastData.longitude;
            Vector2d previousLocation = new Vector2d(_currentLocation.LatitudeLongitude.Latitude, _currentLocation.LatitudeLongitude.Longitude);
            _currentLocation.LatitudeLongitude = new LatitudeLongitude(latitude, longitude);

            _currentLocation.Accuracy = (float)Math.Floor(lastData.horizontalAccuracy);
            // sometimes Unity's timestamp doesn't seem to get updated, or even jump back in time
            // do an additional check if location has changed
            _currentLocation.IsLocationUpdated = timestamp > _lastLocationTimestamp || !_currentLocation.LatitudeLongitude.Equals(previousLocation);
            _currentLocation.Timestamp = timestamp;
            _lastLocationTimestamp = timestamp;

            if (_currentLocation.IsLocationUpdated)
            {
                if (_lastPositions.Count > 0)
                {
                    // only add position if user has moved +1m since we added the previous position to the list
                    CheapRuler cheapRuler = new CheapRuler(_currentLocation.LatitudeLongitude.Latitude, CheapRulerUnits.Meters);
                    var p = _currentLocation.LatitudeLongitude;
                    double distance = cheapRuler.Distance(
                        new double[] { p.Longitude, p.Latitude },
                        new double[] { _lastPositions[0].Longitude, _lastPositions[0].Latitude }
                    );
                    if (distance > 1.0)
                    {
                        _lastPositions.Add(_currentLocation.LatitudeLongitude);
                    }
                }
                else
                {
                    _lastPositions.Add(_currentLocation.LatitudeLongitude);
                }
            }

            // if we have enough positions calculate user heading ourselves.
            // Unity does not provide bearing based on GPS locations, just
            // device orientation based on Compass.Heading.
            // nevertheless, use compass for initial UserHeading till we have
            // enough values to calculate ourselves.
            if (_lastPositions.Count < _maxLastPositions)
            {
                _currentLocation.UserHeading = _currentLocation.DeviceOrientation;
                _currentLocation.IsUserHeadingUpdated = true;
            }
            else
            {
                var newestPos = _lastPositions[0];
                var oldestPos = _lastPositions[_maxLastPositions - 1];
                CheapRuler cheapRuler = new CheapRuler(newestPos.Latitude, CheapRulerUnits.Meters);
                double distance = cheapRuler.Distance(
                    new double[] { newestPos.Longitude, newestPos.Latitude },
                    new double[] { oldestPos.Longitude, oldestPos.Latitude }
                );
                // positions are minimum required distance apart (user is moving), calculate user heading
                if (distance >= _minDistanceOldestNewestPosition)
                {
                    float[] lastHeadings = new float[_maxLastPositions - 1];

                    for (int i = 1; i < _maxLastPositions; i++)
                    {
                        // atan2 increases angle CCW, flip sign of latDiff to get CW
                        double latDiff = -(_lastPositions[i].Latitude - _lastPositions[i - 1].Latitude);
                        double lngDiff = _lastPositions[i].Longitude - _lastPositions[i - 1].Longitude;
                        // +90.0 to make top (north) 0 degrees
                        double heading = (Math.Atan2(latDiff, lngDiff) * 180.0 / Math.PI) + 90.0f;
                        // stay within [0..360] range
                        if (heading < 0)
                        {
                            heading += 360;
                        }

                        if (heading >= 360)
                        {
                            heading -= 360;
                        }

                        lastHeadings[i - 1] = (float)heading;
                    }

                    UnityLocationProviderSettings.UserHeadingSmoothing.Add(lastHeadings[0]);
                    float finalHeading = (float)UnityLocationProviderSettings.UserHeadingSmoothing.Calculate();

                    //fix heading to have 0 for north, 90 for east, 180 for south and 270 for west
                    finalHeading = finalHeading >= 180.0f ? finalHeading - 180.0f : finalHeading + 180.0f;

                    _currentLocation.UserHeading = finalHeading;
                    _currentLocation.IsUserHeadingUpdated = true;
                }
            }

            _currentLocation.TimestampDevice = UnixTimestampUtils.To(DateTime.UtcNow);
            SendLocation(_currentLocation);
        }
    }
}