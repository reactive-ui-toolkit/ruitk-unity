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

        /// <summary>Config files this layout has outgrown, because the tree root
        /// moved and the file is NAMED after the root. Deleted on the next save so
        /// a renamed tree does not leave a stale layout behind forever. Private, so
        /// it is not serialized into the file itself.</summary>
        private readonly HashSet<string> _retired =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

        /// <summary>The layout stored under this exact root, or null. The tree
        /// root is derived from the folder walk and is the same answer from every
        /// module in the tree, which makes it the only DETERMINISTIC key - the
        /// member scan below returns whichever file happens to list the focus
        /// first in directory order, and this project has accumulated a config per
        /// style module from the days when a style module could be its own root
        /// (UB-185).</summary>
        public static BuilderCanvasConfig TryLoadForRoot(string rootFullPath)
        {
            try
            {
                string path = PathFor(rootFullPath);
                if (!File.Exists(path))
                    return null;
                return JsonConvert.DeserializeObject<BuilderCanvasConfig>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RUITK Builder] canvas config load failed: {ex.Message}");
                return null;
            }
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

        /// <summary>The layout that already covers this SET of modules.
        ///
        /// The root is DERIVED - from where the modules sit and which of them
        /// owns the top folder - so it is not a stable name for a tree: a save
        /// that re-files a folder, or a mount that comes up focused somewhere
        /// else, can resolve a different module as root. Addressed by root alone,
        /// the tree then looks like a tree nobody has ever laid out, and the mount
        /// mints a fresh default column and writes it down (UB-221).
        ///
        /// A tree is better identified by WHO IS IN IT than by which member is
        /// currently its head, so this asks for the config sharing the most
        /// members with the graph. Scanning for the focus file alone was the same
        /// idea applied to a single member, and missed whenever the focus was the
        /// one module that had just changed.</summary>
        public static BuilderCanvasConfig LoadForMembers(IEnumerable<string> memberPaths)
        {
            if (memberPaths == null)
                return null;
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string member in memberPaths)
            {
                if (string.IsNullOrEmpty(member))
                    continue;
                try { wanted.Add(Path.GetFullPath(member)); } catch { }
            }
            if (wanted.Count == 0)
                return null;

            BuilderCanvasConfig best = null;
            int bestOverlap = 0;
            string bestSavedAt = "";
            try
            {
                if (!Directory.Exists(ConfigDir))
                    return null;
                foreach (string file in Directory.GetFiles(ConfigDir, "*.json"))
                {
                    BuilderCanvasConfig cfg;
                    try { cfg = JsonConvert.DeserializeObject<BuilderCanvasConfig>(File.ReadAllText(file)); }
                    catch { continue; }
                    if (cfg?.Members == null)
                        continue;
                    int overlap = 0;
                    foreach (string member in cfg.Members)
                    {
                        if (string.IsNullOrEmpty(member))
                            continue;
                        try { if (wanted.Contains(Path.GetFullPath(member))) overlap++; } catch { }
                    }
                    if (overlap == 0)
                        continue;
                    // Most of the tree wins; the newest breaks a tie, so a stale
                    // config from an earlier shape cannot outrank the live one.
                    if (overlap > bestOverlap
                        || (overlap == bestOverlap
                            && string.CompareOrdinal(cfg.SavedAt ?? "", bestSavedAt) > 0))
                    {
                        best = cfg;
                        bestOverlap = overlap;
                        bestSavedAt = cfg.SavedAt ?? "";
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RUITK Builder] canvas config scan failed: {ex.Message}");
                return null;
            }
            return best;
        }
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                SavedAt = DateTime.UtcNow.ToString("o");
                string target = PathFor(RootPath);
                File.WriteAllText(target, JsonConvert.SerializeObject(this, Formatting.Indented));
                foreach (string stale in _retired)
                {
                    if (!string.Equals(stale, target, StringComparison.OrdinalIgnoreCase)
                        && File.Exists(stale))
                        File.Delete(stale);
                }
                _retired.Clear();
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

        /// <summary>POC createNode(x, y): a card created from the canvas lands at
        /// the world coordinates of the right-click, not the default layout slot.</summary>
        public void SetPosition(string filePath, float x, float y)
        {
            Positions[RelKey(filePath)] = new[] { x, y };
        }

        /// <summary>Writes down the slot of every node that does not have one
        /// yet, and reports whether any were new.
        ///
        /// A layout that is never recorded is RECOMPUTED on every mount, and the
        /// default layout is a breadth-first walk over the whole graph - so its
        /// answer depends on the node SET. Adding one module therefore moved
        /// every card the user had never dragged, which is not a layout, it is a
        /// reshuffle. A slot is decided once and then remembered.</summary>
        public bool AdoptUnplaced(BuilderGraph graph)
        {
            bool added = false;
            foreach (var node in graph.Nodes)
            {
                string key = RelKey(node.FilePath);
                if (Positions.ContainsKey(key))
                    continue;
                Positions[key] = new[] { node.X, node.Y };
                added = true;
            }
            return added;
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

        /// <summary>Follows a module, or a whole folder, to a new location so a
        /// rename does not throw the layout away.
        ///
        /// Positions are keyed RELATIVE to the tree root and the config file is
        /// NAMED after the root, so a rename can move the keys and the file name at
        /// once - which is why renaming a folder-owning component used to lose the
        /// whole layout: every member path changed, so neither the by-root lookup
        /// nor the by-member scan could find the config again. Each key is resolved
        /// back to an absolute path, moved, and re-keyed against the new root.</summary>
        public void Repath(string oldPath, string newPath, bool isFolder)
        {
            string from = SafeFull(oldPath);
            string to = SafeFull(newPath);
            if (from == null || to == null
                || string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
                return;

            // Resolved against the OLD root, before it moves out from under them.
            var carried = new List<(string Abs, float[] Pos)>();
            foreach (var pair in Positions)
                carried.Add((Moved(AbsoluteOf(pair.Key), from, to, isFolder), pair.Value));

            string previousRoot = SafeFull(RootPath);
            string movedRoot = Moved(previousRoot, from, to, isFolder);
            if (movedRoot != null
                && !string.Equals(movedRoot, previousRoot, StringComparison.OrdinalIgnoreCase))
            {
                _retired.Add(PathFor(RootPath));
                RootPath = movedRoot;
            }

            Positions.Clear();
            foreach (var (abs, pos) in carried)
                if (!string.IsNullOrEmpty(abs))
                    Positions[RelKey(abs)] = pos;

            for (int i = 0; i < Members.Count; i++)
            {
                string moved = Moved(SafeFull(Members[i]), from, to, isFolder);
                if (!string.IsNullOrEmpty(moved))
                    Members[i] = moved;
            }
        }

        private static string Moved(string path, string from, string to, bool isFolder)
        {
            if (string.IsNullOrEmpty(path))
                return path;
            if (!isFolder)
                return string.Equals(path, from, StringComparison.OrdinalIgnoreCase) ? to : path;
            string prefix = from + Path.DirectorySeparatorChar;
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(to, path.Substring(prefix.Length))
                : path;
        }

        /// <summary>A stored key back to the absolute path it names. Keys are
        /// root-relative for members of the tree and absolute for anything else,
        /// which is exactly what RelKey produces.</summary>
        private string AbsoluteOf(string key)
        {
            try
            {
                if (Path.IsPathRooted(key))
                    return Path.GetFullPath(key);
                string rootDir = Path.GetDirectoryName(Path.GetFullPath(RootPath)) ?? "";
                return Path.GetFullPath(Path.Combine(rootDir, key));
            }
            catch
            {
                return null;
            }
        }

        private static string SafeFull(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return null;
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
