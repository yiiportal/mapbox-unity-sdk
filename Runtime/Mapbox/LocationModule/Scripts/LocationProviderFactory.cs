using System;
using System.Collections;
using Mapbox.BaseModule.Plugins.Android.UniAndroidPermission;
using Mapbox.LocationModule.MapboxLocation;
using UnityEngine;
using YiiPortal.Common;

namespace Mapbox.LocationModule
{
	/// <summary>
	/// Factory to provide access to various LocationProviders.
	/// This is meant to be attached to a game object.
	/// </summary>
	public class LocationProviderFactory : MonoBehaviour
	{
		public LocationProviderType LocationProviderType;
		[NonSerialized] public bool IsLocationProviderReady = false;
		private bool _initializeStarted;
		private bool _startupFailed;
		public Action<LocationProviderFactory> OnLocationProviderReady = (f) => { };

		[SerializeField]
		[Tooltip("Mapbox location provider for android and ios")]
		private MapboxLocationSettings _mapboxLocationProviderSettings = null;

		[SerializeField]
		[Tooltip("Provider using Unity's builtin 'Input.Location' service")]
		private UnityLocationProviderSettings _unityLocationProviderSettings;

		private AbstractLocationProvider _customLocationProvider;

		[SerializeField] private string EditorLatitudeLongitude;

		[SerializeField]
		bool _dontDestroyOnLoad;

		/// <summary>
		/// The singleton instance of this factory.
		/// </summary>
		public static LocationProviderFactory Instance { get; private set; }

		/// <summary>
		/// The default location provider.
		/// Outside of the editor, this will be a <see cref="T:LocationModule.UnityLocationProvider"/>.
		/// In the Unity editor, this will be an <see cref="T:StaticLocationProvider"/>
		/// </summary>
		public ILocationProvider DefaultLocationProvider { get; set; }

		private void Awake()
		{
			if (Instance != null)
			{
				Debug.LogWarning("LocationProviderFactory: an instance already exists; destroying the duplicate.");
				Destroy(gameObject);
				return;
			}
			Instance = this;

			// Must happen here, not in Initialize(): the factory is placed in a
			// bootstrap scene and Initialize() is only driven later by a map
			// behaviour in another scene. If persistence waits for Initialize(),
			// the single-mode scene transition destroys the factory first and
			// Instance is null by the time any map wants a location provider.
			if (_dontDestroyOnLoad)
			{
				DontDestroyOnLoad(gameObject);
			}
		}

		/// <summary>
		/// Initialize and inject the DefaultLocationProvider.
		/// </summary>
		public IEnumerator Initialize()
		{
			// Idempotent: the factory can be initialized eagerly at bootstrap
			// (so GPS starts before any map scene) and again by a map
			// behaviour's init path. A second caller must not create a second
			// provider or restart Input.location — it just waits for the
			// first run to finish.
			if (_initializeStarted)
			{
				while (!IsLocationProviderReady)
				{
					yield return null;
				}
				yield break;
			}
			_initializeStarted = true;

			if (_dontDestroyOnLoad)
			{
				DontDestroyOnLoad(gameObject);
			}

			// A retry after a failed startup (see _startupFailed below) reuses
			// the existing provider instance so subscribers who already hold
			// its OnLocationUpdated event stay subscribed.
			if (DefaultLocationProvider == null)
			{
				if(Application.isEditor || LocationProviderType == LocationProviderType.StaticLocationProvider)
				{
					DefaultLocationProvider = new StaticLocationProvider(EditorLatitudeLongitude);
				}
				else if(LocationProviderType == LocationProviderType.MapboxLocationProvider)
				{
					var mapboxLocationProvider = new MapboxLocationProvider(_mapboxLocationProviderSettings);
					mapboxLocationProvider.AvailabilityChanged += b => Debug.Log("MAPBOX_UNITY_SDK: LocationProviderFactory.AvailabilityChanged " + b);
					mapboxLocationProvider.AuthorizationChanged += b => Debug.Log("MAPBOX_UNITY_SDK: AuthorizationChanged " + b);
					mapboxLocationProvider.AccuracyAuthorizationChanged += b => Debug.Log("MAPBOX_UNITY_SDK: AuthorizationChanged " + b);
					DefaultLocationProvider = mapboxLocationProvider;
				}
				else if(LocationProviderType == LocationProviderType.UnityLocationProvider)
				{
					DefaultLocationProvider = new UnityLocationProvider(_unityLocationProviderSettings);
				}
				// else if(LocationProviderType == LocationProviderType.CustomLocationProvider)
				// {
				// 	DefaultLocationProvider = new UnityLocationProvider(_unityLocationProviderSettings);
				// }
			}

			Debug.Log($"MAPBOX_UNITY_SDK:  LocationProviderFactory: Injected Location Provider - {DefaultLocationProvider.GetType()}");

			if (DefaultLocationProvider.GetType() != typeof(StaticLocationProvider))
			{
				var unityLocationProvider = DefaultLocationProvider as UnityLocationProvider;

				if (unityLocationProvider != null)
				{
					yield return StartUnityLocationServiceCoroutine(unityLocationProvider);
				}
				else if (Input.location.status != LocationServiceStatus.Running)
				{
					Input.location.Start();

					while (Input.location.status < LocationServiceStatus.Running)
						yield return null;
				}

				// Wait for first location update with timeout (10 seconds)
				float timeout = 10f;
				float elapsed = 0f;
				while (!DefaultLocationProvider.HasReceivedFirstFix &&
				       elapsed < timeout)
				{
					elapsed += UnityEngine.Time.deltaTime;
					yield return null;
				}
			}

			IsLocationProviderReady = true;
			OnLocationProviderReady(this);

			// A run whose service startup failed (permission denied, iOS
			// authorization-observation timeout, status never Running) may be
			// retried once conditions change — e.g. the user grants location
			// in Settings and the app resumes. Ready stays true so waiters
			// don't re-arm; only the startup attempt is reopened.
			if (_startupFailed)
			{
				_startupFailed = false;
				_initializeStarted = false;
			}
		}

		/// <summary>
		/// Starts Input.location for a <see cref="UnityLocationProvider"/>.
		/// Re-homes the fork's iOS authorization-observed gate + Android runtime
		/// permission flow + parameterized Start() (item #133 in
		/// docs/iOS_App_Review_Gaps.md) — this is the single place Input.location
		/// gets started, replacing the old DeviceLocationProvider.PollLocationRoutine
		/// one-time startup ritual. Deferring the first authorization check out of
		/// this coroutine's first frame (rather than calling Input.location.Start()
		/// eagerly) avoids the iOS "method can cause UI unresponsiveness" warning
		/// from a synchronous main-thread CLLocationManager authorization check.
		/// </summary>
		private IEnumerator StartUnityLocationServiceCoroutine(UnityLocationProvider unityLocationProvider)
		{
			if (Input.location.status == LocationServiceStatus.Running)
			{
				// Someone else started the session (e.g. an onboarding
				// permission primer priming at its own accuracy). Re-issuing
				// Start() on a running service retunes it in place to the
				// provider's active tier and applies its compass policy, so
				// the session is adopted instead of left at foreign settings.
				unityLocationProvider.Start();
				yield break;
			}

			var wait1Sec = new WaitForSeconds(1f);

#if UNITY_ANDROID
			// Android 6+ requires runtime permission grant before Input.location can start.
			if (!unityLocationProvider.IsEnabledByUser)
			{
				bool gotPermissionResponse = false;
				UniAndroidPermission.RequestPermission(AndroidPermission.ACCESS_FINE_LOCATION,
					onAllow: () => gotPermissionResponse = true,
					onDeny: () => gotPermissionResponse = true,
					onDenyAndNeverAskAgain: () => gotPermissionResponse = true);
				while (!gotPermissionResponse)
					yield return wait1Sec;
			}
#endif

#if UNITY_IOS && !UNITY_EDITOR
			LocationAuthorizationBridge.Initialize();
			int authorizationObservationRetries = 15;
			while (!LocationAuthorizationBridge.HasObservedAuthorization && authorizationObservationRetries > 0)
			{
				authorizationObservationRetries--;
				yield return wait1Sec;
			}

			if (!LocationAuthorizationBridge.HasObservedAuthorization)
			{
				Debug.LogWarning("LocationProviderFactory: Timed out waiting for native authorization observation; skipping location start.");
				_startupFailed = true;
				unityLocationProvider.SetUnavailable();
				yield break;
			}
#endif

			// On iOS, isEnabledByUser can return false for a few seconds after
			// launch while the OS confirms the existing authorization. Retry before
			// giving up so a valid prior grant isn't treated as a denial.
			int enabledRetries = 15;
			while (!unityLocationProvider.IsEnabledByUser && enabledRetries > 0)
			{
				enabledRetries--;
				yield return wait1Sec;
			}

			if (!unityLocationProvider.IsEnabledByUser)
			{
				Debug.LogError("LocationProviderFactory: Location is not enabled by user!");
#if UNITY_IOS && !UNITY_EDITOR
				LocationAuthorizationBridge.NotifyServiceStarted(false);
#endif
				_startupFailed = true;
				unityLocationProvider.SetUnavailable();
				yield break;
			}

			unityLocationProvider.Start();

			// Bounded wait matching the old DeviceLocationProvider.PollLocationRoutine's
			// maxWait=20: Input.location.status has no cross-platform guarantee of ever
			// resolving to Running/Failed (disabled location hardware, a stuck driver,
			// some emulators), so an unbounded wait here can hang map bootstrap forever.
			int statusRetries = 20;
			while (Input.location.status != LocationServiceStatus.Running
			       && Input.location.status != LocationServiceStatus.Failed
			       && statusRetries > 0)
			{
				statusRetries--;
				yield return wait1Sec;
			}

			bool started = Input.location.status == LocationServiceStatus.Running;
			if (!started)
			{
				Debug.LogError("LocationProviderFactory: Timed out or failed waiting for location service to start.");
				_startupFailed = true;
				unityLocationProvider.SetUnavailable();
			}

#if UNITY_IOS && !UNITY_EDITOR
			LocationAuthorizationBridge.NotifyServiceStarted(started);
#endif
		}

		private void Update()
		{
			DefaultLocationProvider?.Update();
		}

		private void OnDestroy()
		{
			DefaultLocationProvider?.OnDestroy();
			if (Instance == this)
			{
				Instance = null;
			}
		}
	}
	
	public enum LocationProviderType
	{
		[InspectorName("Static Location Provider")]
		StaticLocationProvider,

		[InspectorName("Unity Location Provider")]
		UnityLocationProvider,

		[InspectorName("Mapbox Location Provider (Experimental)")]
		MapboxLocationProvider,

		// [InspectorName("Custom Location Provider")]
		// CustomLocationProvider
	}
}


