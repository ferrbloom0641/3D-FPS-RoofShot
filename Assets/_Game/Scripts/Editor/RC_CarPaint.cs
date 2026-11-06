using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace RooftopContract.EditorTools
{
    /// <summary>
    /// Repaints an Awbme car's body black without touching its glass, lights or tyres.
    ///
    /// The whole pack shares one 512px colour-swatch atlas, and a car's paint is simply the
    /// swatch its body panels are UV'd onto. Tinting the material would darken every swatch at
    /// once and turn the car into a silhouette, so instead this finds the swatch that covers the
    /// most of the car's surface — the paint — and writes a copy of the atlas with only that
    /// colour replaced. The copy and a material that uses it are saved per model, so other cars
    /// in the pack keep their own colours.
    /// </summary>
    public static class RC_CarPaint
    {
        static readonly Color32 Paint = new Color32(14, 14, 16, 255);

        public static void PaintBodyBlack(GameObject car, string model)
        {
            Material source = null;
            Texture2D atlas = null;
            var coverage = new Dictionary<Color32, float>(new Color32Comparer());

            foreach (var mf in car.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = mf.sharedMesh;
                var renderer = mf.GetComponent<MeshRenderer>();
                if (mesh == null || renderer == null) continue;
                var mats = renderer.sharedMaterials;

                Vector2[] uv;
                Vector3[] verts;
                try
                {
                    uv = mesh.uv;
                    verts = mesh.vertices;
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[Rooftop Contract] {model}: mesh data unreadable ({e.Message}) — not repainted.");
                    return;
                }
                if (uv.Length == 0) continue;

                for (int sub = 0; sub < mesh.subMeshCount && sub < mats.Length; sub++)
                {
                    var subMat = mats[sub];
                    if (subMat == null || subMat.mainTexture == null) continue;
                    if (source == null)
                    {
                        source = subMat;
                        atlas = LoadAtlas(subMat.mainTexture);
                        if (atlas == null) return;
                    }
                    if (subMat != source) continue;

                    int[] tris = mesh.GetTriangles(sub);
                    for (int i = 0; i + 2 < tris.Length; i += 3)
                    {
                        Vector3 a = mf.transform.TransformPoint(verts[tris[i]]);
                        Vector3 b = mf.transform.TransformPoint(verts[tris[i + 1]]);
                        Vector3 c = mf.transform.TransformPoint(verts[tris[i + 2]]);
                        float area = Vector3.Cross(b - a, c - a).magnitude * 0.5f;
                        Vector2 centre = (uv[tris[i]] + uv[tris[i + 1]] + uv[tris[i + 2]]) / 3f;
                        Color32 key = Sample(atlas, centre);
                        coverage.TryGetValue(key, out float sum);
                        coverage[key] = sum + area;
                    }
                }
            }

            if (source == null || coverage.Count == 0)
            {
                Debug.LogWarning($"[Rooftop Contract] {model}: no textured surface found — not repainted.");
                return;
            }

            Color32 body = default;
            float best = -1f, total = 0f;
            foreach (var kv in coverage)
            {
                total += kv.Value;
                if (kv.Value > best) { best = kv.Value; body = kv.Key; }
            }

            var pixels = atlas.GetPixels32();
            int replaced = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                if (!Same(pixels[i], body)) continue;
                pixels[i] = new Color32(Paint.r, Paint.g, Paint.b, pixels[i].a);
                replaced++;
            }
            atlas.SetPixels32(pixels);
            atlas.Apply();

            string safe = model.Replace(' ', '_');
            string texPath = $"{RC_Palette.Folder}/RC_CarPaint_{safe}_Black.png";
            File.WriteAllBytes(texPath, atlas.EncodeToPNG());
            AssetDatabase.ImportAsset(texPath, ImportAssetOptions.ForceUpdate);
            CopyImportSettings(AssetDatabase.GetAssetPath(source.mainTexture), texPath);
            var painted = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);

            string matPath = $"{RC_Palette.Folder}/RC_CarPaint_{safe}_Black.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                mat = new Material(source) { name = $"RC_CarPaint_{safe}_Black" };
                AssetDatabase.CreateAsset(mat, matPath);
            }
            else
            {
                mat.CopyPropertiesFromMaterial(source);
            }
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", painted);
            if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", painted);
            // A little gloss, so black paint still shows its panels under the dusk light.
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.55f);
            EditorUtility.SetDirty(mat);

            foreach (var renderer in car.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mats = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                    if (mats[i] == source) { mats[i] = mat; changed = true; }
                if (changed) renderer.sharedMaterials = mats;
            }

            Debug.Log($"[Rooftop Contract] {model}: body swatch {body} covers {best / total:P0} of the surface — " +
                      $"{replaced} texels painted black → {matPath}");
        }

        /// <summary>The atlas as it is on disk, readable whatever its import settings say.</summary>
        static Texture2D LoadAtlas(Texture texture)
        {
            string path = AssetDatabase.GetAssetPath(texture);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            return tex.LoadImage(File.ReadAllBytes(path)) ? tex : null;
        }

        static Color32 Sample(Texture2D tex, Vector2 uv)
        {
            int x = Mathf.Clamp(Mathf.FloorToInt(Mathf.Repeat(uv.x, 1f) * tex.width), 0, tex.width - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt(Mathf.Repeat(uv.y, 1f) * tex.height), 0, tex.height - 1);
            var c = tex.GetPixel(x, y);
            return (Color32)c;
        }

        static bool Same(Color32 a, Color32 b) => a.r == b.r && a.g == b.g && a.b == b.b;

        static void CopyImportSettings(string fromPath, string toPath)
        {
            var from = AssetImporter.GetAtPath(fromPath) as TextureImporter;
            var to = AssetImporter.GetAtPath(toPath) as TextureImporter;
            if (from == null || to == null) return;
            to.textureType = from.textureType;
            to.sRGBTexture = from.sRGBTexture;
            to.mipmapEnabled = from.mipmapEnabled;
            to.filterMode = from.filterMode;
            to.wrapMode = from.wrapMode;
            to.textureCompression = from.textureCompression;
            to.SaveAndReimport();
        }

        sealed class Color32Comparer : IEqualityComparer<Color32>
        {
            public bool Equals(Color32 a, Color32 b) => Same(a, b);
            public int GetHashCode(Color32 c) => (c.r << 16) | (c.g << 8) | c.b;
        }
    }
}
