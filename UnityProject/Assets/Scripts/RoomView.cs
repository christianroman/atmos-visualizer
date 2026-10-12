// The 3D room: walls, speakers (lit by their rendered feeds), objects (white dots that move with
// the metadata) and bed channels (purple dots that never move, the Trinnov's colour for beds).
//
// Coordinates: DAMF/ADM room-box x (-1 left..+1 right), y (-1 rear..+1 front), z (0 ear level..
// 1 ceiling) map to Unity X (right), Z (forward) and Y (up). Unity is left-handed, so +X right and
// +Z forward keep left and right the right way round when looking at the screen.

using System;
using System.Collections.Generic;
using AtmosEngine;
using UnityEngine;

public static class Room
{
    public const float Width = 6f, Depth = 7f, EarHeight = 1.2f, CeilingHeight = 2.8f;

    public static Vector3 ToWorld(double x, double y, double z)
    {
        float wy = z >= 0 ? EarHeight + (float)z * (CeilingHeight - EarHeight) : EarHeight + (float)z * EarHeight;
        return new Vector3((float)x * Width / 2f, wy, (float)y * Depth / 2f);
    }

    public static Vector3 Listener => new Vector3(0, EarHeight, 0);
}

public sealed class RoomView : MonoBehaviour
{
    public Material LineMaterial;      // Sprites/Default: coloured lines
    public Material SpeakerMaterial;   // Unlit/Color
    public Material ObjectMaterial;    // Sprites/Default: transparent, fades
    public Material FurnitureMaterial; // Atmos/SoftLit: the couch and the person, softly shaded

    // level -> brightness: below FloorDb is dark; TopDb and above is full
    public float FloorDb = -65f, TopDb = -10f, ReleaseDbPerSecond = 30f;

    static readonly Color Off = new Color(0.16f, 0.17f, 0.20f);
    static readonly Color SpeakerOn = new Color(0.25f, 0.85f, 1.0f);
    static readonly Color LfeOn = new Color(0.68f, 0.38f, 1.0f);       // purple, as the Trinnov shows beds
    // white: neutral, so unlike the Trinnov's yellow it can't be mistaken for a speaker in the
    // level colours (green, yellow, red), the lit-speaker cyan or the bed channels' purple
    static readonly Color ObjectColor = new Color(0.94f, 0.95f, 1.0f);
    static readonly Color BedColor = LfeOn;                             // purple, as the Trinnov shows bed channels

    public sealed class SpeakerVisual
    {
        public PresetSpeaker Speaker;
        public Transform Transform;
        public Renderer Renderer;
        public Vector3 BaseScale;
        public float DisplayDb = -200f;
    }

    public sealed class ObjectVisualRef
    {
        public DamfElement Element;
        public Transform Transform;
        public Renderer Renderer;
        public float DisplayDb = -200f;
        public bool EverActive;
    }

    public sealed class BedVisualRef
    {
        public DamfElement Element;
        public Transform Transform;
        public Renderer Renderer;
        public float DisplayDb = -200f;
    }

    public readonly List<SpeakerVisual> Speakers = new List<SpeakerVisual>();
    public readonly List<ObjectVisualRef> Objects = new List<ObjectVisualRef>();
    public readonly List<BedVisualRef> Beds = new List<BedVisualRef>();
    public bool ShowObjects = true;

    /// <summary>Hide the speakers to watch just the objects move.</summary>
    public bool ShowSpeakers
    {
        get => speakerRoot.gameObject.activeSelf;
        set => speakerRoot.gameObject.SetActive(value);
    }

    /// <summary>"Level colours": speakers go from green (quiet) through yellow to red (loud), like
    /// a Trinnov processor's speaker display. The level used is the speaker feed plus the volume
    /// setting, so turning the volume down turns the room greener, as on a real processor.</summary>
    public bool LevelColors;
    public float VolumeDb;
    public float LevelGreenDb = -50f, LevelRedDb = -6f;   // post-volume dBFS at the two ends

    static readonly Color Green = new Color(0.20f, 0.85f, 0.30f);
    static readonly Color Yellow = new Color(1.00f, 0.84f, 0.15f);
    static readonly Color Red = new Color(1.00f, 0.18f, 0.12f);

    Transform speakerRoot, objectRoot;
    MaterialPropertyBlock block;
    static readonly int ColorId = Shader.PropertyToID("_Color");

    void Awake()
    {
        block = new MaterialPropertyBlock();
        BuildRoom();
        speakerRoot = new GameObject("Speakers").transform;
        speakerRoot.SetParent(transform, false);
        objectRoot = new GameObject("Objects").transform;
        objectRoot.SetParent(transform, false);
    }

    // ---- static room ------------------------------------------------------------------------

    void BuildRoom()
    {
        float w = Room.Width / 2, d = Room.Depth / 2, h = Room.CeilingHeight;
        var edge = new Color(0.55f, 0.6f, 0.7f, 0.55f);
        Vector3[] c =
        {
            new Vector3(-w, 0, -d), new Vector3(w, 0, -d), new Vector3(w, 0, d), new Vector3(-w, 0, d),
            new Vector3(-w, h, -d), new Vector3(w, h, -d), new Vector3(w, h, d), new Vector3(-w, h, d),
        };
        int[,] edges = { { 0, 1 }, { 1, 2 }, { 2, 3 }, { 3, 0 }, { 4, 5 }, { 5, 6 }, { 6, 7 }, { 7, 4 }, { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 } };
        for (int i = 0; i < edges.GetLength(0); i++) Line("Edge", edge, 0.015f, c[edges[i, 0]], c[edges[i, 1]]);

        // floor grid, 1 m
        var grid = new Color(0.4f, 0.45f, 0.55f, 0.18f);
        for (float x = -w + 1; x < w - 0.01f; x += 1) Line("Grid", grid, 0.008f, new Vector3(x, 0, -d), new Vector3(x, 0, d));
        for (float z = -d + 1; z < d - 0.01f; z += 1) Line("Grid", grid, 0.008f, new Vector3(-w, 0, z), new Vector3(w, 0, z));

        // ear-level guide around the walls (z = 0 in room coordinates)
        var ear = new Color(0.45f, 0.75f, 0.9f, 0.25f);
        float e = Room.EarHeight;
        Line("Ear level", ear, 0.01f, new Vector3(-w, e, -d), new Vector3(w, e, -d), new Vector3(w, e, d), new Vector3(-w, e, d), new Vector3(-w, e, -d));

        BuildListener();
    }

    // ---- the listener: a person on a couch, or just a head marker ------------------------------

    GameObject couchGroup, headMarker;

    /// <summary>A couch with a seated person whose head is at the listening position; off shows
    /// only a small head marker there.</summary>
    public bool ShowCouch
    {
        get => couchGroup.activeSelf;
        set { couchGroup.SetActive(value); headMarker.SetActive(!value); }
    }

    void BuildListener()
    {
        var grey = new Color(0.55f, 0.58f, 0.65f);
        headMarker = new GameObject("Head marker");
        headMarker.transform.SetParent(transform, false);
        var head = Part(headMarker.transform, PrimitiveType.Sphere, SpeakerMaterial, Room.Listener, new Vector3(0.22f, 0.26f, 0.24f), Quaternion.identity, grey);
        var nose = Part(head.transform, PrimitiveType.Cube, SpeakerMaterial, Vector3.zero, new Vector3(0.18f, 0.18f, 0.25f), Quaternion.identity, grey);
        nose.transform.localPosition = new Vector3(0, 0, 0.5f);   // shows which way is front

        couchGroup = new GameObject("Couch and listener");
        couchGroup.transform.SetParent(transform, false);
        var c = couchGroup.transform;
        var mat = FurnitureMaterial != null ? FurnitureMaterial : SpeakerMaterial;
        var frame = new Color(0.21f, 0.22f, 0.26f);
        var cushion = new Color(0.30f, 0.31f, 0.36f);
        var leg = new Color(0.12f, 0.12f, 0.14f);
        var person = new Color(0.64f, 0.67f, 0.74f);
        Vector3 at = Room.Listener - new Vector3(0, Room.EarHeight, 0);   // the floor under the listener's head

        // a three-seat couch on short legs, with rolled arms and puffy cushions; the listener sits in the middle
        Box(c, mat, at + new Vector3(0, 0.23f, 0), new Vector3(2.0f, 0.32f, 0.86f), 0.04f, frame);
        Box(c, mat, at + new Vector3(0, 0.71f, -0.34f), new Vector3(2.0f, 0.56f, 0.18f), 0.06f, frame);
        foreach (float side in new[] { -1f, 1f })
        {
            Box(c, mat, at + new Vector3(side * 1.09f, 0.34f, 0), new Vector3(0.18f, 0.62f, 0.9f), 0.08f, frame);
            foreach (float z in new[] { -0.36f, 0.36f })
                Part(c, PrimitiveType.Cylinder, mat, at + new Vector3(side * 1.05f, 0.035f, z), new Vector3(0.06f, 0.035f, 0.06f), Quaternion.identity, leg);
        }
        foreach (float x in new[] { -0.66f, 0f, 0.66f })
        {
            Box(c, mat, at + new Vector3(x, 0.46f, 0.07f), new Vector3(0.64f, 0.16f, 0.72f), 0.07f, cushion);
            Box(c, mat, at + new Vector3(x, 0.74f, -0.19f), new Vector3(0.64f, 0.46f, 0.14f), 0.07f, cushion, Quaternion.Euler(-10, 0, 0));
        }

        // a seated person facing the screen, head at the listening position: an artist's mannequin
        // with an hourglass torso and rounded joints
        Part(c, PrimitiveType.Sphere, mat, Room.Listener, new Vector3(0.2f, 0.25f, 0.22f), Quaternion.identity, person);
        Limb(c, mat, at + new Vector3(0, 1.0f, -0.04f), at + new Vector3(0, 1.1f, -0.02f), 0.04f, person);                                             // neck
        Part(c, PrimitiveType.Sphere, mat, at + new Vector3(0, 0.89f, -0.1f), new Vector3(0.38f, 0.3f, 0.24f), Quaternion.Euler(-8, 0, 0), person);     // chest
        Part(c, PrimitiveType.Capsule, mat, at + new Vector3(0, 0.7f, -0.08f), new Vector3(0.3f, 0.17f, 0.2f), Quaternion.Euler(-6, 0, 0), person);     // waist
        Part(c, PrimitiveType.Capsule, mat, at + new Vector3(0, 0.57f, -0.04f), new Vector3(0.2f, 0.19f, 0.2f), Quaternion.Euler(0, 0, 90), person);    // hips
        foreach (float s in new[] { -1f, 1f })
        {
            Part(c, PrimitiveType.Sphere, mat, at + new Vector3(s * 0.18f, 0.97f, -0.1f), Vector3.one * 0.12f, Quaternion.identity, person);              // shoulder
            Limb(c, mat, at + new Vector3(s * 0.2f, 0.96f, -0.1f), at + new Vector3(s * 0.23f, 0.72f, -0.02f), 0.048f, person);                          // upper arm
            Part(c, PrimitiveType.Sphere, mat, at + new Vector3(s * 0.23f, 0.72f, -0.02f), Vector3.one * 0.1f, Quaternion.identity, person);              // elbow
            Limb(c, mat, at + new Vector3(s * 0.23f, 0.72f, -0.02f), at + new Vector3(s * 0.17f, 0.61f, 0.26f), 0.042f, person);                         // forearm
            Part(c, PrimitiveType.Sphere, mat, at + new Vector3(s * 0.165f, 0.605f, 0.3f), new Vector3(0.08f, 0.06f, 0.1f), Quaternion.identity, person); // hand
            Limb(c, mat, at + new Vector3(s * 0.1f, 0.57f, -0.02f), at + new Vector3(s * 0.11f, 0.59f, 0.42f), 0.075f, person);                          // thigh
            Part(c, PrimitiveType.Sphere, mat, at + new Vector3(s * 0.11f, 0.58f, 0.42f), Vector3.one * 0.14f, Quaternion.identity, person);              // knee
            Limb(c, mat, at + new Vector3(s * 0.11f, 0.57f, 0.43f), at + new Vector3(s * 0.11f, 0.1f, 0.47f), 0.056f, person);                           // shin
            Box(c, mat, at + new Vector3(s * 0.11f, 0.045f, 0.53f), new Vector3(0.09f, 0.07f, 0.22f), 0.03f, person);                                     // foot
        }
        ShowCouch = true;
    }

    GameObject Part(Transform parent, PrimitiveType type, Material material, Vector3 position, Vector3 scale, Quaternion rotation, Color color)
    {
        var go = GameObject.CreatePrimitive(type);
        Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.position = position;
        go.transform.rotation = rotation;
        go.transform.localScale = scale;
        var r = go.GetComponent<Renderer>();
        r.sharedMaterial = material;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        SetColor(r, color);
        return go;
    }

    // a rounded box (ShapeMeshes) at its size: for upholstery and feet
    GameObject Box(Transform parent, Material material, Vector3 position, Vector3 size, float radius, Color color, Quaternion? rotation = null)
    {
        var go = new GameObject("Box");
        go.transform.SetParent(parent, false);
        go.transform.position = position;
        go.transform.rotation = rotation ?? Quaternion.identity;
        go.AddComponent<MeshFilter>().sharedMesh = ShapeMeshes.RoundedBox(size, radius);
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = material;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        SetColor(r, color);
        return go;
    }

    // a capsule from one joint to another
    void Limb(Transform parent, Material material, Vector3 from, Vector3 to, float radius, Color color)
    {
        var d = to - from;
        Part(parent, PrimitiveType.Capsule, material, (from + to) / 2, new Vector3(2 * radius, d.magnitude / 2, 2 * radius),
             Quaternion.FromToRotation(Vector3.up, d.normalized), color);
    }

    void Line(string name, Color color, float width, params Vector3[] points)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.sharedMaterial = LineMaterial;
        lr.useWorldSpace = true;
        lr.positionCount = points.Length;
        lr.SetPositions(points);
        lr.startWidth = lr.endWidth = width;
        lr.startColor = lr.endColor = color;
        lr.numCapVertices = 2;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
    }

    // ---- speakers ---------------------------------------------------------------------------

    public void SetPreset(LayoutPreset preset)
    {
        foreach (var s in Speakers) Destroy(s.Transform.gameObject);
        Speakers.Clear();
        foreach (var sp in preset.Speakers)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = sp.Name;
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(speakerRoot, false);

            Vector3 pos = Room.ToWorld(sp.X, sp.Y, sp.Z);
            Vector3 scale;
            bool onWall = sp.Z >= 0.5 && sp.Mount == "wall";
            if (sp.IsLfe)
            {
                pos.y = 0.22f;
                scale = new Vector3(0.42f, 0.42f, 0.42f);
            }
            else if (onWall)
            {
                scale = new Vector3(0.24f, 0.3f, 0.2f);   // a height speaker high on its wall
                pos.y = Room.CeilingHeight - 0.35f;
            }
            else if (sp.Z >= 0.5)
            {
                scale = new Vector3(0.3f, 0.07f, 0.3f);   // flat, in the ceiling
                pos.y = Room.CeilingHeight - 0.05f;
            }
            else
            {
                scale = new Vector3(0.24f, 0.36f, 0.2f);
            }
            // keep boxes just inside the walls so they don't clip through them
            pos.x = Mathf.Clamp(pos.x, -Room.Width / 2 + 0.2f, Room.Width / 2 - 0.2f);
            pos.z = Mathf.Clamp(pos.z, -Room.Depth / 2 + 0.2f, Room.Depth / 2 - 0.2f);
            go.transform.position = pos;
            go.transform.localScale = scale;
            if (!sp.IsLfe && (sp.Z < 0.5 || onWall))
            {
                var look = Room.Listener - pos;
                if (!onWall) look.y = 0;   // wall heights also tilt down towards the seat
                if (look.sqrMagnitude > 1e-4f) go.transform.rotation = Quaternion.LookRotation(look.normalized, Vector3.up);
            }
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = SpeakerMaterial;
            SetColor(r, Off);
            Speakers.Add(new SpeakerVisual { Speaker = sp, Transform = go.transform, Renderer = r, BaseScale = scale });
        }
        PlaceBeds();
    }

    // ---- objects ----------------------------------------------------------------------------

    public void SetDamf(DamfFile damf)
    {
        foreach (var o in Objects) Destroy(o.Transform.gameObject);
        Objects.Clear();
        foreach (var b in Beds) Destroy(b.Transform.gameObject);
        Beds.Clear();
        if (damf == null) return;
        foreach (var e in damf.Beds)
        {
            if (!GainTimeline.TryGetBedPosition(e.BedChannel, out double x, out double y, out double z)) continue;
            bool ever = false;
            foreach (var ev in e.Events) if (ev.LinearGain > 0) { ever = true; break; }
            if (!ever) continue;   // a bed channel that never plays stays out of the way, like unused object slots
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "Bed " + e.BedChannel;
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(objectRoot, false);
            go.transform.localScale = Vector3.one * 0.26f;
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = ObjectMaterial;
            Beds.Add(new BedVisualRef { Element = e, Transform = go.transform, Renderer = r });
        }
        PlaceBeds();
        foreach (var e in damf.Objects)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "Object " + e.Id;
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(objectRoot, false);
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = ObjectMaterial;
            go.SetActive(false);
            bool ever = false;
            foreach (var ev in e.Events) if (ev.Active && ev.LinearGain > 0) { ever = true; break; }
            Objects.Add(new ObjectVisualRef { Element = e, Transform = go.transform, Renderer = r, EverActive = ever });
        }
    }

    // Bed dots sit at their channel's standard place, except the LFE, which goes beside this
    // layout's sub wherever the layout puts it.
    void PlaceBeds()
    {
        SpeakerVisual sub = null;
        foreach (var s in Speakers) if (s.Speaker.IsLfe) { sub = s; break; }
        foreach (var b in Beds)
        {
            GainTimeline.TryGetBedPosition(b.Element.BedChannel, out double x, out double y, out double z);
            if (z < 0 && sub != null) { x = sub.Speaker.X; y = sub.Speaker.Y; }
            b.Transform.position = BedDotPosition(x, y, z);
        }
    }

    // A bed channel's dot: at its speaker's place, a little towards the seat so it isn't hidden
    // inside the speaker box; overhead ones just below the ceiling. The LFE sits on the floor
    // beside the sub, along the front wall: both are purple, so touching they'd merge, and the
    // space above the sub is where its label goes.
    static Vector3 BedDotPosition(double x, double y, double z)
    {
        Vector3 p = Room.ToWorld(x, y, z);
        p.x = Mathf.Clamp(p.x, -Room.Width / 2 + 0.2f, Room.Width / 2 - 0.2f);
        p.z = Mathf.Clamp(p.z, -Room.Depth / 2 + 0.2f, Room.Depth / 2 - 0.2f);
        if (z < 0)
        {
            p.y = 0.3f;
            p.x += p.x <= 0 ? 0.55f : -0.55f;   // to the sub's right, or inwards from a right-hand corner
            return p;
        }
        if (z >= 0.5) p.y = Room.CeilingHeight - 0.3f;
        var toSeat = Room.Listener - p;
        toSeat.y = 0;
        if (toSeat.sqrMagnitude > 1e-4f) p += toSeat.normalized * 0.45f;
        return p;
    }

    // ---- per-frame update -----------------------------------------------------------------------

    /// <param name="sample">the audible file position</param>
    /// <param name="speakerMeanSquare">mean-square level of each rendered speaker feed (null = silent)</param>
    /// <param name="elementMeanSquare">mean-square level of each element's audio (null = silent)</param>
    public void UpdateVisuals(long sample, double[] speakerMeanSquare, double[] elementMeanSquare, float dt)
    {
        for (int k = 0; k < Speakers.Count; k++)
        {
            var s = Speakers[k];
            float db = speakerMeanSquare != null && k < speakerMeanSquare.Length ? ToDb(speakerMeanSquare[k]) : -200f;
            s.DisplayDb = db >= s.DisplayDb ? db : Mathf.Max(db, s.DisplayDb - ReleaseDbPerSecond * dt);
            float b = Brightness(s.DisplayDb);
            SetColor(s.Renderer, LevelColors ? LevelColor(s.DisplayDb + VolumeDb) : Color.Lerp(Off, s.Speaker.IsLfe ? LfeOn : SpeakerOn, b));
            s.Transform.localScale = s.BaseScale * (1f + 0.25f * b);
        }

        foreach (var o in Objects)
        {
            var v = ObjectMotion.At(o.Element, sample);
            bool show = ShowObjects && v.Visible;
            if (o.Transform.gameObject.activeSelf != show) o.Transform.gameObject.SetActive(show);
            if (!show) continue;
            o.Transform.position = Room.ToWorld(v.X, v.Y, v.Z);
            float radius = 0.13f + 0.5f * (float)Math.Min(1.0, v.Size);
            o.Transform.localScale = Vector3.one * radius * 2f;

            // glow with the object's own audio level (its channel, times its metadata gain)
            double ms = elementMeanSquare != null && o.Element.Index < elementMeanSquare.Length ? elementMeanSquare[o.Element.Index] : 0;
            float db = ToDb(ms * v.LinearGain * v.LinearGain);
            o.DisplayDb = db >= o.DisplayDb ? db : Mathf.Max(db, o.DisplayDb - ReleaseDbPerSecond * dt);
            float b = Brightness(o.DisplayDb);
            var c = Color.Lerp(ObjectColor * 0.45f, ObjectColor, b);
            c.a = (float)v.Alpha * (0.55f + 0.45f * b);
            SetColor(o.Renderer, c);
        }

        foreach (var bd in Beds)
        {
            if (bd.Transform.gameObject.activeSelf != ShowObjects) bd.Transform.gameObject.SetActive(ShowObjects);
            if (!ShowObjects) continue;
            // glow with the channel's own audio level, times its metadata gain
            var e = bd.Element;
            double gain = e.Events.Count == 0 ? 0 : e.Events[Math.Max(0, DamfFile.EventIndexAt(e, sample))].LinearGain;
            double ms = elementMeanSquare != null && e.Index < elementMeanSquare.Length ? elementMeanSquare[e.Index] : 0;
            float db = ToDb(ms * gain * gain);
            bd.DisplayDb = db >= bd.DisplayDb ? db : Mathf.Max(db, bd.DisplayDb - ReleaseDbPerSecond * dt);
            float b = Brightness(bd.DisplayDb);
            var c = Color.Lerp(BedColor * 0.45f, BedColor, b);
            c.a = 0.55f + 0.45f * b;
            SetColor(bd.Renderer, c);
        }
    }

    /// <summary>Green -> yellow -> red across LevelGreenDb..LevelRedDb, fading from dark below.</summary>
    public Color LevelColor(float db)
    {
        float fade = Mathf.InverseLerp(LevelGreenDb - 15f, LevelGreenDb, db);   // dark -> green
        if (fade <= 0) return Off;
        float t = Mathf.InverseLerp(LevelGreenDb, LevelRedDb, db);
        Color c = t < 0.5f ? Color.Lerp(Green, Yellow, t * 2f) : Color.Lerp(Yellow, Red, (t - 0.5f) * 2f);
        return Color.Lerp(Off, c, fade);
    }

    public float Brightness(float db) => db <= FloorDb ? 0f : Mathf.Clamp01(0.15f + 0.85f * (db - FloorDb) / (TopDb - FloorDb));

    public static float ToDb(double meanSquare) => meanSquare > 1e-20 ? (float)(10.0 * Math.Log10(meanSquare)) : -200f;

    void SetColor(Renderer r, Color c)
    {
        r.GetPropertyBlock(block);
        block.SetColor(ColorId, c);
        r.SetPropertyBlock(block);
    }
}
