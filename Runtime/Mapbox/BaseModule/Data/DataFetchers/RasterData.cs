using System;
using UnityEngine;

namespace Mapbox.BaseModule.Data.DataFetchers
{
    [Serializable]
    public class RasterData : MapboxTileData
    {
        public Texture2D Texture;
        private int _visualReferenceCount;
        private bool _disposeRequested;
        private bool _isDisposed;

        public bool IsDisposed => _isDisposed;
        
        public virtual void Clear()
        {
            //cant null texture here as native will reuse it
            //this is just for debugging unity impl
            Texture = null;
        }

        public override void Dispose()
        {
            if (_disposeRequested || _isDisposed)
            {
                return;
            }

            _disposeRequested = true;
            if (_visualReferenceCount == 0)
            {
                DisposeNow();
            }
        }

        public void RetainVisualReference()
        {
            if (_isDisposed)
            {
                throw new InvalidOperationException("Cannot retain disposed raster data.");
            }

            _visualReferenceCount++;
        }

        public void ReleaseVisualReference()
        {
            if (_visualReferenceCount == 0)
            {
                return;
            }

            _visualReferenceCount--;
            if (_visualReferenceCount == 0 && _disposeRequested)
            {
                DisposeNow();
            }
        }

        protected virtual void DisposeNow()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
#if UNITY_EDITOR
            if (!Application.isPlaying)
                GameObject.DestroyImmediate(Texture);
            else
#endif
            GameObject.Destroy(Texture);
            Texture = null;
            base.Dispose();
        }
    }
}
