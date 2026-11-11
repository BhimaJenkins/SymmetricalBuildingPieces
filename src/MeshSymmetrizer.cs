using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SymmetricalBuildingPieces
{
    /// <summary>
    /// Makes a mesh mirror-symmetric across a plane: everything on the "keep" side of the plane is kept
    /// (triangles that cross the plane are clipped), everything on the other side is thrown away, and the
    /// kept half is mirrored onto the thrown-away side. Silhouette stays the same, both faces end up identical.
    ///
    /// All plane math happens in the piece ROOT's local space so child transforms (offsets, rotations,
    /// non-uniform scale) are handled correctly.
    /// </summary>
    internal static class MeshSymmetrizer
    {
        private const float InsideEps = 1e-5f;   // vertices this close to the plane count as "inside"
        private const float OnPlaneEps = 1e-4f;  // triangles lying entirely on the plane are dropped (internal faces)
        private const int UvChannels = 4;

        private sealed class Vtx
        {
            public Vector3 P;   // root space
            public Vector3 N;   // root space
            public Vector4 T;   // root space xyz, w = handedness
            public Color C;
            public readonly Vector4[] Uv = new Vector4[UvChannels];
            public float D;     // signed distance to plane (positive = keep side)
        }

        /// <param name="src">Readable source mesh.</param>
        /// <param name="dst">Mesh to overwrite (instances using it update live).</param>
        /// <param name="meshToRoot">Matrix from mesh-local space to piece-root space.</param>
        /// <param name="planePoint">A point on the mirror plane, root space.</param>
        /// <param name="keepNormal">Plane normal in root space, pointing toward the side to KEEP.</param>
        public static void Build(Mesh src, Mesh dst, Matrix4x4 meshToRoot, Vector3 planePoint, Vector3 keepNormal)
        {
            keepNormal.Normalize();
            Matrix4x4 rootToMesh = meshToRoot.inverse;
            Matrix4x4 normalToRoot = rootToMesh.transpose; // inverse-transpose of meshToRoot
            Matrix4x4 normalToMesh = meshToRoot.transpose; // inverse-transpose of rootToMesh

            var pos = new List<Vector3>(); src.GetVertices(pos);
            var nrm = new List<Vector3>(); src.GetNormals(nrm);
            var tan = new List<Vector4>(); src.GetTangents(tan);
            var col = new List<Color>(); src.GetColors(col);
            int vc = pos.Count;
            bool hasN = nrm.Count == vc, hasT = tan.Count == vc, hasC = col.Count == vc;

            var uvIn = new List<Vector4>[UvChannels];
            var hasUv = new bool[UvChannels];
            var uvDim = new int[UvChannels];
            for (int ch = 0; ch < UvChannels; ch++)
            {
                uvIn[ch] = new List<Vector4>();
                var attr = VertexAttribute.TexCoord0 + ch;
                hasUv[ch] = src.HasVertexAttribute(attr);
                if (!hasUv[ch]) continue;
                src.GetUVs(ch, uvIn[ch]);
                hasUv[ch] = uvIn[ch].Count == vc;
                uvDim[ch] = src.GetVertexAttributeDimension(attr);
            }

            // Source vertices converted to root space once.
            var verts = new Vtx[vc];
            for (int i = 0; i < vc; i++)
            {
                var v = new Vtx { P = meshToRoot.MultiplyPoint3x4(pos[i]) };
                if (hasN) v.N = normalToRoot.MultiplyVector(nrm[i]).normalized;
                if (hasT)
                {
                    Vector3 t3 = meshToRoot.MultiplyVector(tan[i]).normalized;
                    v.T = new Vector4(t3.x, t3.y, t3.z, tan[i].w);
                }
                if (hasC) v.C = col[i];
                for (int ch = 0; ch < UvChannels; ch++) if (hasUv[ch]) v.Uv[ch] = uvIn[ch][i];
                v.D = Vector3.Dot(v.P - planePoint, keepNormal);
                verts[i] = v;
            }

            var oPos = new List<Vector3>();
            var oNrm = new List<Vector3>();
            var oTan = new List<Vector4>();
            var oCol = new List<Color>();
            var oUv = new List<Vector4>[UvChannels];
            for (int ch = 0; ch < UvChannels; ch++) oUv[ch] = new List<Vector4>();

            int subCount = src.subMeshCount;
            var subIdx = new List<int>[subCount];
            var srcIdx = new List<int>();
            var poly = new List<Vtx>(4);

            int Emit(Vtx v, bool mirror)
            {
                Vector3 p = v.P, n = v.N;
                Vector3 t3 = new Vector3(v.T.x, v.T.y, v.T.z);
                float w = v.T.w;
                if (mirror)
                {
                    float d = Vector3.Dot(p - planePoint, keepNormal);
                    p -= 2f * d * keepNormal;
                    n -= 2f * Vector3.Dot(n, keepNormal) * keepNormal;
                    t3 -= 2f * Vector3.Dot(t3, keepNormal) * keepNormal;
                    w = -w; // a reflection flips tangent-space handedness
                }
                oPos.Add(rootToMesh.MultiplyPoint3x4(p));
                if (hasN) oNrm.Add(normalToMesh.MultiplyVector(n).normalized);
                if (hasT)
                {
                    Vector3 tm = rootToMesh.MultiplyVector(t3).normalized;
                    oTan.Add(new Vector4(tm.x, tm.y, tm.z, w));
                }
                if (hasC) oCol.Add(v.C);
                for (int ch = 0; ch < UvChannels; ch++) if (hasUv[ch]) oUv[ch].Add(v.Uv[ch]);
                return oPos.Count - 1;
            }

            for (int s = 0; s < subCount; s++)
            {
                var outIdx = subIdx[s] = new List<int>();
                if (src.GetTopology(s) != MeshTopology.Triangles) continue;
                srcIdx.Clear();
                src.GetTriangles(srcIdx, s);

                for (int i = 0; i + 2 < srcIdx.Count; i += 3)
                {
                    Vtx a = verts[srcIdx[i]], b = verts[srcIdx[i + 1]], c = verts[srcIdx[i + 2]];

                    // Faces lying exactly on the mirror plane would z-fight with their own mirror: drop them.
                    if (Mathf.Abs(a.D) < OnPlaneEps && Mathf.Abs(b.D) < OnPlaneEps && Mathf.Abs(c.D) < OnPlaneEps)
                        continue;

                    ClipToKeepSide(a, b, c, poly);
                    for (int k = 1; k + 1 < poly.Count; k++)
                    {
                        Vtx p0 = poly[0], p1 = poly[k], p2 = poly[k + 1];
                        // Kept half, original winding.
                        outIdx.Add(Emit(p0, false)); outIdx.Add(Emit(p1, false)); outIdx.Add(Emit(p2, false));
                        // Mirrored half, reversed winding so it faces outward.
                        outIdx.Add(Emit(p0, true)); outIdx.Add(Emit(p2, true)); outIdx.Add(Emit(p1, true));
                    }
                }
            }

            dst.Clear();
            dst.indexFormat = oPos.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            dst.SetVertices(oPos);
            if (hasN) dst.SetNormals(oNrm);
            if (hasT) dst.SetTangents(oTan);
            if (hasC) dst.SetColors(oCol);
            for (int ch = 0; ch < UvChannels; ch++)
            {
                if (!hasUv[ch]) continue;
                if (uvDim[ch] <= 2) dst.SetUVs(ch, oUv[ch].ConvertAll(u => new Vector2(u.x, u.y)));
                else if (uvDim[ch] == 3) dst.SetUVs(ch, oUv[ch].ConvertAll(u => new Vector3(u.x, u.y, u.z)));
                else dst.SetUVs(ch, oUv[ch]);
            }
            dst.subMeshCount = subCount;
            for (int s = 0; s < subCount; s++) dst.SetTriangles(subIdx[s], s, false);
            if (!hasN) dst.RecalculateNormals();
            if (!hasT && hasN) dst.RecalculateTangents();
            dst.RecalculateBounds();
        }

        /// <summary>Sutherland–Hodgman clip of one triangle against the keep half-space (D >= 0).</summary>
        private static void ClipToKeepSide(Vtx a, Vtx b, Vtx c, List<Vtx> result)
        {
            result.Clear();
            Vtx[] tri = { a, b, c };
            for (int i = 0; i < 3; i++)
            {
                Vtx cur = tri[i], nxt = tri[(i + 1) % 3];
                bool curIn = cur.D >= -InsideEps, nxtIn = nxt.D >= -InsideEps;
                if (curIn) result.Add(cur);
                if (curIn != nxtIn) result.Add(Lerp(cur, nxt, cur.D / (cur.D - nxt.D)));
            }
            if (result.Count < 3) result.Clear();
        }

        private static Vtx Lerp(Vtx a, Vtx b, float t)
        {
            var v = new Vtx
            {
                P = Vector3.Lerp(a.P, b.P, t),
                N = Vector3.Lerp(a.N, b.N, t).normalized,
                C = Color.Lerp(a.C, b.C, t),
                D = 0f,
            };
            Vector3 t3 = Vector3.Lerp(new Vector3(a.T.x, a.T.y, a.T.z), new Vector3(b.T.x, b.T.y, b.T.z), t).normalized;
            v.T = new Vector4(t3.x, t3.y, t3.z, a.T.w);
            for (int ch = 0; ch < UvChannels; ch++) v.Uv[ch] = Vector4.Lerp(a.Uv[ch], b.Uv[ch], t);
            return v;
        }
    }
}
