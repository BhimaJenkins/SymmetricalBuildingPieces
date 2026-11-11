using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace SymmetricalBuildingPieces
{
    /// <summary>
    /// Clones vanilla wall prefabs, swaps their meshes for symmetrical versions,
    /// registers the clones with ZNetScene and adds them to the Hammer right after the original.
    /// </summary>
    internal static class PieceRegistry
    {
        public const string ClonePrefix = "SBP_";

        private sealed class MeshPair
        {
            public MeshFilter Filter;
            public Mesh Source;
            public Mesh Result;
        }

        private sealed class Entry
        {
            public string Source;
            public GameObject Clone;
            public readonly List<MeshPair> Meshes = new List<MeshPair>();
        }

        private static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>();
        private static readonly HashSet<string> Missing = new HashSet<string>();
        private static GameObject _holder;

        private static readonly AccessTools.FieldRef<ZNetScene, Dictionary<int, GameObject>> NamedPrefabs =
            AccessTools.FieldRefAccess<ZNetScene, Dictionary<int, GameObject>>("m_namedPrefabs");

        // ---------- config parsing ----------

        /// <summary>"woodwall, wood_wall_half:+, scale_wall_2x2:-"  →  name → side ('a' auto, '+', '-')</summary>
        private static Dictionary<string, char> ParsePieceList()
        {
            var result = new Dictionary<string, char>(StringComparer.Ordinal);
            foreach (string raw in Plugin.PieceList.Value.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string token = raw.Trim();
                if (token.Length == 0) continue;
                string[] parts = token.Split(':');
                string name = parts[0].Trim();
                string side = parts.Length > 1 ? parts[1].Trim().ToLowerInvariant() : "auto";
                char s = side == "+" || side == "front" ? '+'
                       : side == "-" || side == "back" ? '-'
                       : side == "flip" ? 'f'
                       : 'a';
                result[name] = s;
            }
            return result;
        }

        // ---------- setup ----------

        public static void TrySetup()
        {
            if (ZNetScene.instance == null || ObjectDB.instance == null) return;

            if (_holder == null)
            {
                _holder = new GameObject("SBP_PrefabHolder");
                _holder.SetActive(false); // children never run Awake while parked here
                UnityEngine.Object.DontDestroyOnLoad(_holder);
            }

            foreach (var kv in ParsePieceList())
            {
                if (Entries.ContainsKey(kv.Key) || Missing.Contains(kv.Key)) continue;
                CreateClone(kv.Key, kv.Value);
            }

            RegisterWithZNetScene();
            AddToHammer();
        }

        private static void CreateClone(string sourceName, char side)
        {
            GameObject original = ZNetScene.instance.GetPrefab(sourceName);
            if (original == null || original.GetComponent<Piece>() == null)
            {
                Missing.Add(sourceName);
                Plugin.Log.LogWarning($"'{sourceName}' is not a known piece prefab. Run 'sbp_dump' in the F5 console to list prefab names.");
                return;
            }

            GameObject clone = UnityEngine.Object.Instantiate(original, _holder.transform, false);
            clone.name = ClonePrefix + sourceName;

            Piece piece = clone.GetComponent<Piece>();
            piece.m_name += Plugin.NameSuffix.Value; // "$piece_woodwall (Symmetrical)" localizes fine
            Sprite icon = LoadIconOverride(sourceName);
            if (icon != null) piece.m_icon = icon;

            var entry = new Entry { Source = sourceName, Clone = clone };
            foreach (MeshFilter mf in clone.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh src = mf.sharedMesh;
                if (src == null) continue;
                if (!src.isReadable)
                {
                    Plugin.Log.LogError($"{sourceName}: mesh '{src.name}' on '{GetPath(mf.transform, clone.transform)}' is not readable — left unchanged.");
                    continue;
                }
                var result = new Mesh { name = src.name + "_sym" };
                entry.Meshes.Add(new MeshPair { Filter = mf, Source = src, Result = result });
                mf.sharedMesh = result;
            }

            Entries[sourceName] = entry;
            Build(entry, side);
            Plugin.Log.LogInfo($"Created {clone.name} ({entry.Meshes.Count} meshes symmetrized).");
        }

        // ---------- mesh building ----------

        private static void Build(Entry entry, char side)
        {
            if (entry.Meshes.Count == 0) return;
            Transform root = entry.Clone.transform;

            Analyze(root, entry.Meshes.Select(m => (m.Filter, m.Source)), out Vector3 axis, out Vector3 planePoint,
                    out float posExtent, out float negExtent);

            char auto = Mathf.Abs(posExtent - negExtent) < 0.005f ? '+' : (posExtent < negExtent ? '+' : '-');
            char keep = side == 'a' ? auto
                      : side == 'f' ? (auto == '+' ? '-' : '+')   // "flip" = opposite of what auto picks
                      : side;

            Vector3 keepNormal = keep == '+' ? axis : -axis;
            foreach (MeshPair m in entry.Meshes)
            {
                Matrix4x4 meshToRoot = root.worldToLocalMatrix * m.Filter.transform.localToWorldMatrix;
                MeshSymmetrizer.Build(m.Source, m.Result, meshToRoot, planePoint, keepNormal);
            }

            Plugin.Log.LogInfo($"{entry.Clone.name}: thin axis {AxisName(axis)}, extents +{posExtent:0.###} / -{negExtent:0.###}, keeping '{keep}' side (config: '{side}').");
        }

        /// <summary>Finds the wall's thin horizontal axis and the mirror plane, in root space.</summary>
        private static void Analyze(Transform root, IEnumerable<(MeshFilter filter, Mesh mesh)> meshes,
            out Vector3 axis, out Vector3 planePoint, out float posExtent, out float negExtent)
        {
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            var buf = new List<Vector3>();
            foreach (var (mf, mesh) in meshes)
            {
                Matrix4x4 m = root.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                buf.Clear();
                if (mesh.isReadable) mesh.GetVertices(buf);
                else buf.AddRange(BoundsCorners(mesh.bounds));
                foreach (Vector3 v in buf)
                {
                    Vector3 p = m.MultiplyPoint3x4(v);
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
            }

            Vector3 size = max - min;
            axis = size.x < size.z ? Vector3.right : Vector3.forward;

            float lo = Vector3.Dot(min, axis), hi = Vector3.Dot(max, axis);
            float plane = Plugin.Plane.Value == PlaneMode.BoundsCenter ? (lo + hi) * 0.5f : 0f;
            plane += Plugin.PlaneOffset.Value;
            planePoint = axis * plane;
            posExtent = hi - plane;
            negExtent = plane - lo;
        }

        /// <summary>Re-reads config and rebuilds every symmetrical mesh in place (placed pieces update live).</summary>
        public static void RebuildAll()
        {
            var sides = ParsePieceList();
            foreach (Entry e in Entries.Values)
                Build(e, sides.TryGetValue(e.Source, out char s) ? s : 'a');
            if (sides.Keys.Any(k => !Entries.ContainsKey(k) && !Missing.Contains(k)))
                Plugin.Log.LogInfo("New pieces in the list will be added the next time a world loads.");
        }

        // ---------- registration ----------

        private static void RegisterWithZNetScene()
        {
            ZNetScene zs = ZNetScene.instance;
            Dictionary<int, GameObject> named = NamedPrefabs(zs);
            foreach (Entry e in Entries.Values)
            {
                if (!zs.m_prefabs.Contains(e.Clone)) zs.m_prefabs.Add(e.Clone);
                named[e.Clone.name.GetStableHashCode()] = e.Clone;
            }
        }

        private static void AddToHammer()
        {
            GameObject hammer = ObjectDB.instance.GetItemPrefab("Hammer");
            PieceTable table = hammer ? hammer.GetComponent<ItemDrop>()?.m_itemData.m_shared.m_buildPieces : null;
            if (table == null) { Plugin.Log.LogWarning("Hammer piece table not found."); return; }

            List<GameObject> pieces = table.m_pieces;
            foreach (Entry e in Entries.Values)
            {
                if (pieces.Contains(e.Clone)) continue;
                int idx = pieces.FindIndex(p => p != null && p.name == e.Source);
                if (idx >= 0) pieces.Insert(idx + 1, e.Clone);
                else pieces.Add(e.Clone);
            }
        }

        // ---------- sbp_dump ----------

        public static int Dump(string filter)
        {
            GameObject hammer = ObjectDB.instance ? ObjectDB.instance.GetItemPrefab("Hammer") : null;
            PieceTable table = hammer ? hammer.GetComponent<ItemDrop>()?.m_itemData.m_shared.m_buildPieces : null;
            if (table == null) return 0;

            var sb = new StringBuilder();
            sb.AppendLine($"===== SBP dump (filter: '{filter}') =====");
            int count = 0;
            foreach (GameObject go in table.m_pieces)
            {
                if (go == null || go.name.StartsWith(ClonePrefix)) continue;
                Piece piece = go.GetComponent<Piece>();
                string display = piece && Localization.instance != null ? Localization.instance.Localize(piece.m_name) : "";
                if (go.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                    display.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                count++;
                sb.AppendLine($"{go.name}  |  \"{display}\"  |  category {piece?.m_category}");
                var filters = go.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh != null).ToList();
                foreach (MeshFilter mf in filters)
                {
                    Mesh mesh = mf.sharedMesh;
                    var mr = mf.GetComponent<MeshRenderer>();
                    string mats = mr ? string.Join(", ", mr.sharedMaterials.Select(m => m ? m.name : "null")) : "-";
                    sb.AppendLine($"    {GetPath(mf.transform, go.transform)}: mesh '{mesh.name}' readable={mesh.isReadable} verts={mesh.vertexCount} submeshes={mesh.subMeshCount} mats=[{mats}]");
                }
                if (filters.Count > 0)
                {
                    Analyze(go.transform, filters.Select(f => (f, f.sharedMesh)), out Vector3 axis, out _, out float pos, out float neg);
                    sb.AppendLine($"    thin axis {AxisName(axis)}, extents from plane: +{pos:0.###} / -{neg:0.###}");
                }
            }
            sb.AppendLine($"===== {count} pieces =====");
            Plugin.Log.LogInfo(sb.ToString());
            return count;
        }

        // ---------- icon overrides ----------

        /// <summary>
        /// Looks for &lt;prefab&gt;.png in an "icons" folder next to the DLL, or next to the DLL itself
        /// (flat fallback for r2modman installs). Returns null if none found.
        /// </summary>
        private static Sprite LoadIconOverride(string sourceName)
        {
            string dir = Path.GetDirectoryName(Plugin.PluginPath) ?? "";
            string[] candidates =
            {
                Path.Combine(dir, "icons", sourceName + ".png"),
                Path.Combine(dir, sourceName + ".png"),
            };
            string file = candidates.FirstOrDefault(File.Exists);
            if (file == null) return null;

            try
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "SBP_icon_" + sourceName };
                if (!LoadImage(tex, File.ReadAllBytes(file)))
                {
                    Plugin.Log.LogWarning($"Could not decode icon '{file}'.");
                    return null;
                }
                tex.wrapMode = TextureWrapMode.Clamp;
                Plugin.Log.LogInfo($"Using custom icon for {sourceName}: {file}");
                return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Failed to load icon '{file}': {ex.Message}");
                return null;
            }
        }

        // ImageConversion.LoadImage via reflection, so the project doesn't need to reference
        // UnityEngine.ImageConversionModule (which drags in netstandard 2.1 and breaks net48 builds).
        private static System.Reflection.MethodInfo _loadImage;

        private static bool LoadImage(Texture2D tex, byte[] data)
        {
            if (_loadImage == null)
            {
                Type t = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule");
                _loadImage = t?.GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]) });
                if (_loadImage == null)
                {
                    Plugin.Log.LogWarning("UnityEngine.ImageConversion.LoadImage not found; custom icons disabled.");
                    return false;
                }
            }
            return (bool)_loadImage.Invoke(null, new object[] { tex, data });
        }

        // ---------- helpers ----------

        private static string AxisName(Vector3 axis) => axis == Vector3.right ? "X" : "Z";

        private static IEnumerable<Vector3> BoundsCorners(Bounds b)
        {
            for (int i = 0; i < 8; i++)
                yield return new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
        }

        private static string GetPath(Transform t, Transform root)
        {
            var parts = new List<string>();
            for (; t != null && t != root; t = t.parent) parts.Add(t.name);
            parts.Reverse();
            return parts.Count == 0 ? "(root)" : string.Join("/", parts);
        }
    }
}