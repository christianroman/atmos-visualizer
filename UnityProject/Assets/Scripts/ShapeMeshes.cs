// A small procedural shape for the furniture: a box with rounded edges, for an upholstered couch.
// No model files needed.

using System.Collections.Generic;
using UnityEngine;

public static class ShapeMeshes
{
    static readonly Dictionary<(Vector3, float), Mesh> boxes = new Dictionary<(Vector3, float), Mesh>();

    /// <summary>A box of the given size whose edges and corners are rounded with this radius.
    /// Each face is a grid whose lines bunch up near the edges; every vertex is then pulled onto
    /// the rounded surface (the point on an inner box plus the radius, in the direction away
    /// from it), which also gives its exact normal.</summary>
    public static Mesh RoundedBox(Vector3 size, float radius, int segments = 4)
    {
        if (boxes.TryGetValue((size, radius), out var cached)) return cached;
        Vector3 half = size / 2f;
        float r = Mathf.Min(radius, Mathf.Min(half.x, Mathf.Min(half.y, half.z)));
        Vector3 inner = half - Vector3.one * r;

        // grid coordinates along one axis: dense in the two rounded bands, one span across the middle
        float[] Axis(float h)
        {
            var c = new float[2 * (segments + 1)];
            for (int k = 0; k <= segments; k++)
            {
                float t = r * (1f - Mathf.Cos(k * Mathf.PI / 2f / segments));
                c[k] = -h + t;
                c[c.Length - 1 - k] = h - t;
            }
            return c;
        }
        float Half(Vector3 axis) => Mathf.Abs(Vector3.Dot(axis, half));

        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var tris = new List<int>();
        // each face: outward normal n and grid directions u, v with u x v = n (Unity's front faces)
        void Face(Vector3 n, Vector3 u, Vector3 v)
        {
            float[] us = Axis(Half(u)), vs = Axis(Half(v));
            float hn = Half(n);
            int start = verts.Count;
            foreach (float b in vs)
                foreach (float a in us)
                {
                    Vector3 p = n * hn + u * a + v * b;
                    Vector3 q = new Vector3(Mathf.Clamp(p.x, -inner.x, inner.x), Mathf.Clamp(p.y, -inner.y, inner.y), Mathf.Clamp(p.z, -inner.z, inner.z));
                    Vector3 d = p - q;
                    Vector3 nn = d.sqrMagnitude > 1e-12f ? d.normalized : n;
                    verts.Add(q + nn * r);
                    normals.Add(nn);
                }
            int w = us.Length;
            for (int j = 0; j < vs.Length - 1; j++)
                for (int i = 0; i < w - 1; i++)
                {
                    int a0 = start + j * w + i;
                    tris.Add(a0); tris.Add(a0 + 1); tris.Add(a0 + w);
                    tris.Add(a0 + 1); tris.Add(a0 + w + 1); tris.Add(a0 + w);
                }
        }
        Face(Vector3.right, Vector3.up, Vector3.forward);
        Face(Vector3.left, Vector3.forward, Vector3.up);
        Face(Vector3.up, Vector3.forward, Vector3.right);
        Face(Vector3.down, Vector3.right, Vector3.forward);
        Face(Vector3.forward, Vector3.right, Vector3.up);
        Face(Vector3.back, Vector3.up, Vector3.right);

        var mesh = new Mesh { name = "Rounded box " + size };
        mesh.SetVertices(verts);
        mesh.SetNormals(normals);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();
        boxes[(size, radius)] = mesh;
        return mesh;
    }
}
