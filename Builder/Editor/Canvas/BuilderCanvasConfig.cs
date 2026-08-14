#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace Ruitk.Builder
{
    /// <summary>
    /// Per-tree canvas persistence (owner decision VE-D8): card positions and the
    /// camera, keyed by the ROOT file's full path, one JSON per tree under the
    /// consumer project's <c>UserSettings/</c> (per-user, survives Library
    /// deletion, conventionally gitignored). Members are recorded so opening ANY
    /// file of a tree finds the existing config even when the clicked file is not
    /// the root. Newtonsoft (not JsonUtility) because positions are a dictionary.
    /// </summary>
    public sealed class BuilderCanvasConfig
    {
        public string RootPath = "";
        public List<string> Members = new List<string>();
        public Dictionary<string, float[]> Positions =
            new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        public float CameraX;
        public float CameraY;
        public float Zoom = 1f;
        public string SavedAt = "";

        private static string ConfigDir =>
            Path.Combine(
                Path.GetDirectoryName(Application.dataPath) ?? ".",
                "UserSettings", "ReactiveUIToolkit", "Builder");

        private static string PathFor(string rootFullPath)
        {
            using var sha = SHA1.Create();
            byte[] hash = sha.ComputeHash(
                Encoding.UTF8.GetBytes(Path.GetFullPath(rootFullPath).ToLowerInvariant()));
            string key = BitConverter.ToString(hash, 0, 4).Replace("-", "").ToLowerInvariant();
            return Path.Combine(ConfigDir, key + ".json");
        }

        public static BuilderCanvasConfig LoadForRoot(string rootFullPath)
        {
            try
            {
                string path = PathFor(rootFullPath);
                if (File.Exists(path))
                {
                    var cfg = JsonConvert.DeserializeObject<BuilderCanvasConfig>(File.ReadAllText(path));
                    if (cfg != null)
                        return cfg;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RUITK Builder] canvas config load failed: {ex.Message}");
            }
            return new BuilderCanvasConfig { RootPath = rootFullPath };
        }

        /// <summary>
        /// Finds the config whose member set contains <paramref name="anyMemberPath"/> —
        /// tree identity survives opening from a non-root file, and tree merges keep
        /// the first root's layout.
        /// </summary>
        public static BuilderCanvasConfig LoadForMember(string anyMemberPath)
        {
            try
            {
                if (Directory.Exists(ConfigDir))
                {
                    string full = Path.GetFullPath(anyMemberPath);
                    foreach (string file in Directory.GetFiles(ConfigDir, "*.json"))
                    {
                        BuilderCanvasConfig cfg;
                        try { cfg = JsonConvert.DeserializeObject<BuilderCanvasConfig>(File.ReadAllText(file)); }
                        catch { continue; }
                        if (cfg == null)
                            continue;
                        foreach (string member in cfg.Members)
                            if (string.Equals(Path.GetFullPath(member), full, StringComparison.OrdinalIgnoreCase))
                                return cfg;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RUITK Builder] canvas config scan failed: {ex.Message}");
            }
            return null;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                SavedAt = DateTime.UtcNow.ToString("o");
                File.WriteAllText(PathFor(RootPath), JsonConvert.SerializeObject(this, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RUITK Builder] canvas config save failed: {ex.Message}");
            }
        }

        public void ApplyTo(BuilderGraph graph)
        {
            Members.Clear();
            foreach (var node in graph.Nodes)
            {
                Members.Add(node.FilePath);
                if (Positions.TryGetValue(RelKey(node.FilePath), out var pos) && pos != null && pos.Length == 2)
                {
                    node.X = pos[0];
                    node.Y = pos[1];
                }
            }
        }

        public void CaptureFrom(BuilderGraph graph, float cameraX, float cameraY, float zoom)
        {
            CameraX = cameraX;
            CameraY = cameraY;
            Zoom = zoom;
            Members.Clear();
            foreach (var node in graph.Nodes)
            {
                Members.Add(node.FilePath);
                Positions[RelKey(node.FilePath)] = new[] { node.X, node.Y };
            }
        }

        private string RelKey(string filePath)
        {
            try
            {
                string root = Path.GetDirectoryName(Path.GetFullPath(RootPath)) ?? "";
                string full = Path.GetFullPath(filePath);
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return full.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');
                return full.Replace('\\', '/');
            }
            catch
            {
                return filePath;
            }
        }
    }
}
#endif
