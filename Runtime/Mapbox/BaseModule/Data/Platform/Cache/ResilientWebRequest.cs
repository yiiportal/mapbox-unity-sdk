using System;
using Mapbox.BaseModule.Data.Platform;
using UnityEngine.Networking;

namespace Mapbox.BaseModule.Data.Platform.Cache
{
    public class ResilientWebRequest : IDisposable, IWebRequest
    {
        protected UnityWebRequest _request;
        public string RawUri;
        protected int _timeout;
        protected string _etag;

        public bool IsAborted = false;

        // Set by Ready() when RawUri fails the http(s) guard; _request stays null in that
        // case, so every accessor below falls back to a safe default instead of NRE'ing.
        public bool HasInvalidUrl { get; private set; }

        public int TryCount { get; protected set; }

        public UnityWebRequest Core => _request;
        public DownloadHandler downloadHandler => _request?.downloadHandler;
        public long responseCode => _request?.responseCode ?? 0;
        public string error => _request != null ? _request.error : "Invalid or malformed URL";
        public UnityWebRequest.Result result => _request?.result ?? UnityWebRequest.Result.ProtocolError;
        public string Url => _request?.url ?? RawUri;

        private string EtagHeaderName = "ETag";
        private string CacheControlHeaderName = "Cache-Control";
		
        public ResilientWebRequest(string rawUri, int timeout, string etag = "")
        {
            RawUri = rawUri;
            _timeout = timeout;
            _etag = etag;
            _request = null;
        }

        public virtual ResilientWebRequest Ready()
        {
            if (_request != null)
            {
                _request.Abort();
                _request.Dispose();
                _request = null;
            }

            // iOS_App_Review_Gaps.md #139 — guard against malformed or non-HTTP(S) URLs
            HasInvalidUrl = !MapboxUrlValidator.IsValidHttpUrl(RawUri);
            if (HasInvalidUrl)
            {
                UnityEngine.Debug.LogWarning($"[ResilientWebRequest] Skipped Ready — invalid or malformed URL: '{RawUri}'");
                return this;
            }

            _request = UnityWebRequest.Get(RawUri);
            _request.timeout = _timeout;
            if (!string.IsNullOrEmpty(_etag))
            {
                _request.SetRequestHeader("If-None-Match", _etag);
            }

            TryCount++;
            return this;
        }

        public void Abort()
        {
            IsAborted = true;
            _request?.Abort();
            Dispose();
        }

        public void Dispose()
        {
            _request?.Dispose();
            _request = null;
        }

        public UnityWebRequestAsyncOperation SendWebRequest()
        {
            return _request?.SendWebRequest();
        }

        public string GetETag()
        {
            string eTag = _request?.GetResponseHeader(EtagHeaderName);
            if (string.IsNullOrEmpty(eTag))
            {
                //Debug.LogWarning("no 'ETag' header present in response");
            }

            return eTag;
        }

        public DateTime GetExpirationDate()
        {
            DateTime expirationDate = DateTime.Now;
            var headerValue = _request?.GetResponseHeader(CacheControlHeaderName);
            if (!string.IsNullOrEmpty(headerValue))
            {
                var cacheEntries = headerValue.Split(',');
                if (cacheEntries.Length > 0)
                {
                    foreach (var entry in cacheEntries)
                    {
                        var value = entry.Split('=');
                        if (value[0] == "max-age")
                        {
                            expirationDate = expirationDate + TimeSpan.FromSeconds(int.Parse(value[1]));
                            return expirationDate;
                        }
                    }
                }
            }

            return expirationDate;
        }
    }
}