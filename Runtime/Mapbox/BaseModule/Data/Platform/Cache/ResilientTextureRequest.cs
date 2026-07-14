using UnityEngine;
using UnityEngine.Networking;

namespace Mapbox.BaseModule.Data.Platform.Cache
{
    public class ResilientTextureRequest : ResilientWebRequest, ITextureWebRequest
    {
        private readonly bool _isTextureNonreadable;
        private bool _textureOwnershipTransferred;

        public ResilientTextureRequest(string rawUri, bool isNonReadable, int timeout, string etag = "") : base(rawUri, timeout, etag)
        {
            _isTextureNonreadable = isNonReadable;
            _request = null;
        }

        public override ResilientWebRequest Ready()
        {
            base.Ready();
            if (!HasInvalidUrl)
            {
                _request.downloadHandler = new DownloadHandlerTexture(!_isTextureNonreadable);
            }
            return this;
        }

        public Texture2D TakeTexture()
        {
            if (_request == null)
            {
                return null;
            }

            var texture = DownloadHandlerTexture.GetContent(_request);
            _textureOwnershipTransferred = texture != null;
            return texture;
        }

        protected override void ReleaseRequest()
        {
            try
            {
                DestroyCompletedUnclaimedTexture();
            }
            finally
            {
                _textureOwnershipTransferred = false;
                base.ReleaseRequest();
            }
        }

        private void DestroyCompletedUnclaimedTexture()
        {
            if (_request == null ||
                _textureOwnershipTransferred ||
                !_request.isDone ||
                _request.result != UnityWebRequest.Result.Success)
            {
                return;
            }

            var downloadHandler = _request.downloadHandler as DownloadHandlerTexture;
            if (downloadHandler == null)
            {
                return;
            }

            try
            {
                var texture = downloadHandler.texture;
                if (texture == null)
                {
                    return;
                }

                Object.Destroy(texture);
            }
            catch (System.InvalidOperationException)
            {
            }
        }
    }
}
