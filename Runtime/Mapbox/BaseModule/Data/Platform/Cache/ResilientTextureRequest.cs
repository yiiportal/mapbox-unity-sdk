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
            texture = RemoveMipChain(texture);
            _textureOwnershipTransferred = texture != null;
            return texture;
        }

        private Texture2D RemoveMipChain(Texture2D texture)
        {
            if (Application.platform != RuntimePlatform.IPhonePlayer ||
                !_isTextureNonreadable ||
                texture == null ||
                texture.mipmapCount <= 1)
            {
                return texture;
            }

            Texture2D replacement = null;
            try
            {
                replacement = new Texture2D(
                    texture.width,
                    texture.height,
                    texture.format,
                    mipChain: false,
                    linear: !texture.isDataSRGB)
                {
                    name = texture.name,
                    filterMode = texture.filterMode,
                    wrapMode = texture.wrapMode,
                    anisoLevel = texture.anisoLevel
                };
                replacement.Apply(updateMipmaps: false, makeNoLongerReadable: true);
                Graphics.CopyTexture(texture, 0, 0, replacement, 0, 0);
                Object.Destroy(texture);
                return replacement;
            }
            catch (System.Exception exception)
            {
                if (replacement != null)
                {
                    Object.Destroy(replacement);
                }
                Debug.LogWarning($"[ResilientTextureRequest] Could not remove texture mip chain: {exception.Message}");
                return texture;
            }
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
