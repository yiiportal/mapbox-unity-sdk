using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "Mapbox/Others/Animation Curve Container")]
public class AnimationCurveContainer : ScriptableObject
{
    public AnimationCurve Curve;

    public float Evaluate(float zoom)
    {
        return Curve.Evaluate(zoom);
    }

    public float EvaluateClamped(float zoom)
    {
        if (Curve == null || Curve.length == 0)
        {
            return 1f;
        }

        float minimumTime = Curve[0].time;
        float maximumTime = Curve[Curve.length - 1].time;
        return Curve.Evaluate(Mathf.Clamp(zoom, minimumTime, maximumTime));
    }
}
