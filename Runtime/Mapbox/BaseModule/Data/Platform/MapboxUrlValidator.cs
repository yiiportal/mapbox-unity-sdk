using System;

namespace Mapbox.BaseModule.Data.Platform
{
    // iOS_App_Review_Gaps.md #139 — shared by FileSource and ResilientWebRequest so the
    // http(s)-only guard against malformed/mapbox:// URLs isn't duplicated between them.
    internal static class MapboxUrlValidator
    {
        internal static bool IsValidHttpUrl(string url)
        {
            return !string.IsNullOrEmpty(url)
                && Uri.TryCreate(url, UriKind.Absolute, out var parsedUri)
                && (parsedUri.Scheme == Uri.UriSchemeHttps || parsedUri.Scheme == Uri.UriSchemeHttp);
        }
    }
}
