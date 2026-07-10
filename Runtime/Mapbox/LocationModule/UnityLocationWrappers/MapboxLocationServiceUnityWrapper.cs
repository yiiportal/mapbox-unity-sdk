using UnityEngine;
using YiiPortal.Common;

namespace Mapbox.LocationModule.UnityLocationWrappers
{
	/// <summary>
	/// Wrap Unity's LocationService into MapboxLocationService.
	///
	/// iOS-only patch (item #15 in docs/iOS_App_Review_Gaps.md): route the
	/// `status` and `isEnabledByUser` reads through
	/// <see cref="LocationAuthorizationBridge"/> so the per-tick poll loop in
	/// <c>DeviceLocationProvider.PollLocationRoutine</c> no longer trips Main
	/// Thread Checker against the deprecated
	/// <c>+[CLLocationManager authorizationStatus]</c> selector that Unity's
	/// `Input.location.status` getter resolves to on some engine versions.
	/// `lastData` still reads from `Input.location.lastData` since that's the
	/// position pipeline (not auth) and uses a thread-safe cached snapshot.
	/// </summary>
	public class MapboxLocationServiceUnityWrapper : IMapboxLocationService
	{
		public MapboxLocationServiceUnityWrapper()
		{
			if (LocationAuthorizationBridge.IsSupported)
			{
				LocationAuthorizationBridge.Initialize();
			}
		}

		public bool isEnabledByUser
		{
			get
			{
				if (LocationAuthorizationBridge.IsSupported)
				{
					return LocationAuthorizationBridge.IsEnabledByUser();
				}
				return Input.location.isEnabledByUser;
			}
		}


		public LocationServiceStatus status
		{
			get
			{
				if (LocationAuthorizationBridge.IsSupported)
				{
					return LocationAuthorizationBridge.GetStatus();
				}
				return Input.location.status;
			}
		}


		public IMapboxLocationInfo lastData { get { return new MapboxLocationInfoUnityWrapper(Input.location.lastData); } }


		public void Start(float desiredAccuracyInMeters, float updateDistanceInMeters)
		{
			if (LocationAuthorizationBridge.IsSupported)
			{
				if (!LocationAuthorizationBridge.HasObservedAuthorization)
				{
					Debug.LogWarning("MapboxLocationServiceUnityWrapper: Starting Unity location before native authorization observation completed.");
				}
				Input.location.Start(desiredAccuracyInMeters, updateDistanceInMeters);
				return;
			}

			Input.location.Start(desiredAccuracyInMeters, updateDistanceInMeters);
		}


		public void Stop()
		{
			Input.location.Stop();
			if (LocationAuthorizationBridge.IsSupported)
			{
				LocationAuthorizationBridge.NotifyServiceStarted(false);
			}
		}



	}
}
