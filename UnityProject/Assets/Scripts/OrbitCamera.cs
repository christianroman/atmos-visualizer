// Camera around the room: drag to rotate, scroll to zoom, on-screen Rotate/Zoom sliders, and
// named views (Overview, Seat, Top, Front, Side) that glide into place.
//
// Dragging uses the change in mouse position between frames rather than Unity's "Mouse X/Y"
// axes, which come from raw device deltas and can stay at zero over Remote Desktop.

using System;
using UnityEngine;

public sealed class OrbitCamera : MonoBehaviour
{
    public Vector3 Target = new Vector3(0, 1.3f, 0.3f);
    public float Yaw = -28f, Pitch = 26f, Distance = OverviewDistance;   // starts at the Overview
    public const float OverviewDistance = 11f;
    public const float MinDistance = 2.5f, MaxDistance = 16f;

    /// <summary>Set by the UI: true when the pointer is over a UI panel, so drags there don't orbit.</summary>
    public Func<Vector2, bool> IsPointerOverUI;

    float targetYaw, targetPitch, targetDistance;
    Vector3 targetTarget;
    bool dragging;
    Vector2 lastMouse;

    /// <summary>Where the rotation is heading, in degrees (-180..180, 0 = behind the seat).</summary>
    public float TargetYaw
    {
        get => Mathf.DeltaAngle(0, targetYaw);
        set => targetYaw = Yaw + Mathf.DeltaAngle(Yaw, value);   // the short way round, even after many drags
    }

    /// <summary>0 = furthest, 1 = closest.</summary>
    public float Zoom
    {
        get => Mathf.InverseLerp(MaxDistance, MinDistance, targetDistance);
        set => targetDistance = Mathf.Lerp(MaxDistance, MinDistance, Mathf.Clamp01(value));
    }

    void Awake()
    {
        targetYaw = Yaw;
        targetPitch = Pitch;
        targetDistance = Distance;
        targetTarget = Target;
        Apply();
    }

    float sideYaw = 90f;   // 90 = from the left wall, -90 = from the right
    string lastView;

    public void SetView(string name)
    {
        switch (name)
        {
            // low, from just behind the back wall: the screen and speakers about as the listener
            // sees them, with the head just under the picture (and in front of the sub)
            case "Seat": Go(0, 10, 5.6f, new Vector3(0, 1.4f, 1.0f)); break;
            case "Top": Go(0, 89, 12f, new Vector3(0, 0.6f, 0)); break;
            case "Front": Go(180, 12, 12f, new Vector3(0, 1.3f, 0)); break;
            case "Side":
                // clicked again while still looking from that side: go round to the other one
                if (lastView == "Side" && Mathf.Abs(Mathf.DeltaAngle(targetYaw, sideYaw)) < 1f) sideYaw = -sideYaw;
                Go(sideYaw, 10, 12f, new Vector3(0, 1.3f, 0));
                break;
            default: Go(-28, 26, OverviewDistance, new Vector3(0, 1.2f, 0.3f)); break;   // "Overview"
        }
        lastView = name;
    }

    /// <summary>Glide to a pose: yaw and pitch in degrees, distance in metres from the target.</summary>
    public void Go(float yaw, float pitch, float distance, Vector3 target)
    {
        // take the short way round from wherever the user has dragged to
        targetYaw = Yaw + Mathf.DeltaAngle(Yaw, yaw);
        targetPitch = pitch;
        targetDistance = distance;
        targetTarget = target;
    }

    void Update()
    {
        Vector2 mouse = Input.mousePosition;
        bool overUi = IsPointerOverUI != null && IsPointerOverUI(mouse);
        if ((Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1)) && !overUi)
        {
            dragging = true;
            lastMouse = mouse;
        }
        if (!Input.GetMouseButton(0) && !Input.GetMouseButton(1)) dragging = false;
        if (dragging)
        {
            Vector2 d = mouse - lastMouse;
            lastMouse = mouse;
            // a drag across the whole window turns the room about one and a half times
            float perPixel = 540f / Mathf.Max(400, Screen.width);
            targetYaw += d.x * perPixel;
            targetPitch = Mathf.Clamp(targetPitch - d.y * perPixel * 0.6f, -10f, 89f);
        }
        float scroll = Input.mouseScrollDelta.y;
        if (Math.Abs(scroll) > 0.01f && !overUi)
            targetDistance = Mathf.Clamp(targetDistance * Mathf.Pow(0.9f, scroll), MinDistance, MaxDistance);

        float t = 1f - Mathf.Exp(-Time.unscaledDeltaTime * 8f);
        Yaw = Mathf.Lerp(Yaw, targetYaw, t);
        Pitch = Mathf.Lerp(Pitch, targetPitch, t);
        Distance = Mathf.Lerp(Distance, targetDistance, t);
        Target = Vector3.Lerp(Target, targetTarget, t);
        Apply();
    }

    void Apply()
    {
        // Yaw 0 looks from behind the seat towards the screen (+Z)
        var rot = Quaternion.Euler(Pitch, Yaw, 0);
        transform.position = Target - rot * Vector3.forward * Distance;
        transform.rotation = rot;
    }
}
