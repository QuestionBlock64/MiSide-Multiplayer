using System;
using UnityEngine;
using UnityEngine.UI;

namespace MiSideMultiplayer.API.Credits
{
    public sealed class CreditsGradient : BaseMeshEffect
    {
        public CreditsGradient(IntPtr ptr) : base(ptr) { }

        public Color topColor    = Color.white;
        public Color bottomColor = Color.white;

        public override void ModifyMesh(VertexHelper vh)
        {
            try
            {
                if (!IsActive()) return;

                int count = vh.currentVertCount;
                if (count == 0) return;

                RectTransform rt = GetComponent<RectTransform>();
                if (rt == null) return;

                float top    = rt.rect.yMax;
                float bottom = rt.rect.yMin;
                float range  = top - bottom;
                if (range <= 0.0001f) return;

                UIVertex vert = new UIVertex();
                for (int i = 0; i < count; i++)
                {
                    vh.PopulateUIVertex(ref vert, i);
                    float t = Mathf.Clamp01((vert.position.y - bottom) / range);
                    vert.color = vert.color * Color.Lerp(bottomColor, topColor, t);
                    vh.SetUIVertex(vert, i);
                }
            }
            catch { }
        }
    }
}