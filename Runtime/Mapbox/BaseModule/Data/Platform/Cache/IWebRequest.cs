using UnityEngine;
using UnityEngine.Networking;

namespace Mapbox.BaseModule.Data.Platform.Cache
{
    public interface IWebRequest
    {
        void Abort();
        UnityWebRequest Core { get; }

        int TryCount { get; }
    }

    public interface ITextureWebRequest : IWebRequest
    {
        Texture2D TakeTexture();
    }
}
