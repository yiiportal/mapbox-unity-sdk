using System;
using Mapbox.BaseModule.Data.Vector2d;
using Mapbox.BaseModule.Utilities;
using Mapbox.BaseModule.Utilities.Attributes;
using UnityEngine;

namespace Mapbox.LocationModule
{
	/// <summary>
	/// The EditorLocationProvider is responsible for providing mock location and heading data
	/// for testing purposes in the Unity editor.
	/// </summary>
	public class StaticLocationProvider : AbstractEditorLocationProvider
	{
		private LatitudeLongitude _latLng;

		public StaticLocationProvider(LatitudeLongitude latLng)
		{
			_latLng = latLng;
			_currentLocation = new Location()
			{
				LatitudeLongitude = _latLng
			};
		}
		
		public StaticLocationProvider(string latLng)
		{
			_latLng = Conversions.StringToLatLon(latLng);
			_currentLocation = new Location()
			{
				LatitudeLongitude = _latLng
			};
		}

		/// <summary>
		/// Pumped by LocationProviderFactory.Update. The legacy
		/// EditorLocationProvider auto-fired every frame; without this,
		/// SetLocation never runs, so IsLocationServiceEnabled stays false
		/// and OnLocationUpdated never fires — consumers waiting for a
		/// valid first fix (e.g. map recentering) hang forever in Editor.
		/// </summary>
		public override void Update()
		{
			SendLocationEvent();
		}

		protected override void SetLocation()
		{
			//_currentLocation.UserHeading = transform.eulerAngles.y;
			_currentLocation.LatitudeLongitude = _latLng;
			_currentLocation.Accuracy = _accuracy;
			_currentLocation.Timestamp = UnixTimestampUtils.To(DateTime.UtcNow);
			_currentLocation.IsLocationUpdated = true;
			_currentLocation.IsUserHeadingUpdated = true;
			_currentLocation.IsLocationServiceEnabled = true;
		}
	}
}
