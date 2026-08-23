#if UNITY_EDITOR
using System;

namespace Ruitk.Builder
{
    /// <summary>
    /// One module in the builder's tree: its text, where it currently sits on
    /// disk, and nothing else. NOTHING here touches disk - the whole tree is
    /// projected at Save (owner decision VE-D2).
    ///
    /// A module is REMOVED from the tree to delete it. There is deliberately no
    /// pending-delete flag: intent kept in a list beside the data is what the
    /// previous shape did, and every consumer then had to join the two. Five
    /// defects in two days came from consumers that forgot, or from routes that
    /// bypassed the join - see Plans~/BUILDER_TREE_MODEL.md.
    ///
    /// Text is LF-normalized internally (the language pipeline's printer
    /// contract rejects CR); the file's original EOL flavor is recorded so Save
    /// can write bytes matching what the file used before.
    /// </summary>
    [Serializable]
    public sealed class BuilderModule
    {
        /// <summary>Stable identity, opaque, generated once. A module keeps this
        /// across every rename and move, so anything that refers to a module -
        /// a ledger entry, a card position, a selection - survives its path
        /// changing. The path is DERIVED; this is not.</summary>
        public string Id;

        /// <summary>Folder the module lives in. Authoritative and mutable:
        /// stored rather than derived from a parent link, because deriving it
        /// would want to MOVE every existing tree that does not follow the
        /// ComponentName/ComponentName.uitkx convention. Change it through
        /// <see cref="BuilderTree.MoveTo"/>, which carries the subtree.</summary>
        public string Folder;

        /// <summary>Module name without suffix - "ShowcasePage".</summary>
        public string Name;

        public BuilderNodeKind Kind;

        /// <summary>The live buffer. The only mutable content a module has.</summary>
        public string BufferText;

        /// <summary>The text this module was last PROJECTED from - written to
        /// disk, or read from it at load. Dirtiness is the difference between
        /// this and <see cref="BufferText"/>, so it needs no flag to maintain.</summary>
        public string ProjectedText;

        /// <summary>Immutable-package policy: a module inside a registered,
        /// non-embedded package can be read but never written.</summary>
        public bool IsReadOnly;

        /// <summary>The EOL flavor of the file this came from, so a Save writes
        /// bytes matching what was there before.</summary>
        public bool UsedCrlf;

        /// <summary>A tree begun from the empty state has no folder to infer, so
        /// its modules sit at a provisional path until the first Save asks for
        /// one. Save REFUSES to write these, whoever calls.</summary>
        public bool NeedsLocation;

        /// <summary>Where this module sits on disk RIGHT NOW, or empty when it
        /// has never been written.
        ///
        /// Never compared directly - use <see cref="IsOnDisk"/>. Unity's
        /// serializer turns a null string into "" across a domain reload, and
        /// "has never been written" is load-bearing here: a module that came
        /// back claiming to live at "" would be treated as already on disk and
        /// silently not written. One accessor means no call site can ask the
        /// question wrongly.</summary>
        public string DiskPath;

        public bool IsOnDisk => !string.IsNullOrEmpty(DiskPath);

        /// <summary>Where this module BELONGS - derived from the model, every
        /// time, never stored. Save compares it against <see cref="DiskPath"/>:
        /// they disagree exactly when the module has moved.</summary>
        public string FilePath =>
            string.IsNullOrEmpty(Folder) || string.IsNullOrEmpty(Name)
                ? string.Empty
                : System.IO.Path.Combine(Folder, Name + SuffixFor(Kind));

        public bool IsDirty =>
            !string.Equals(BufferText, ProjectedText, StringComparison.Ordinal);

        /// <summary>Moved since it was last projected: it has a file, and that
        /// file is not where the model says the module belongs.</summary>
        public bool HasMoved =>
            IsOnDisk && !string.Equals(DiskPath, FilePath, StringComparison.OrdinalIgnoreCase);

        /// <summary>A component owns the folder it is named after, and takes the
        /// folder with it when renamed. A COMPANION never does - a style module
        /// beside its component shares that folder without owning it, and a card
        /// title has its .style/.hooks stripped, so the two report the same name.</summary>
        public bool OwnsFolder =>
            Kind != BuilderNodeKind.Style && Kind != BuilderNodeKind.Hook
            && !string.IsNullOrEmpty(Folder) && !string.IsNullOrEmpty(Name)
            && string.Equals(
                System.IO.Path.GetFileName(Folder.TrimEnd('\\', '/')), Name, StringComparison.Ordinal);

        public static string SuffixFor(BuilderNodeKind kind) => kind switch
        {
            BuilderNodeKind.Style => ".style.uitkx",
            BuilderNodeKind.Hook => ".hooks.uitkx",
            _ => ".uitkx",
        };

        public static string NewId() => Guid.NewGuid().ToString("N");

        public static string NormalizeLf(string text) =>
            text == null ? string.Empty : text.Replace("\r\n", "\n").Replace("\r", "\n");

        /// <summary>Splits a file name into the name and kind the model holds.
        /// Suffix-first, exactly as the rest of the builder classifies: a
        /// ".style.uitkx" is a style module whatever its contents say.</summary>
        public static void SplitFileName(string fileName, out string name, out BuilderNodeKind kind)
        {
            fileName ??= string.Empty;
            if (fileName.EndsWith(".style.uitkx", StringComparison.OrdinalIgnoreCase))
            {
                kind = BuilderNodeKind.Style;
                name = fileName.Substring(0, fileName.Length - ".style.uitkx".Length);
                return;
            }
            if (fileName.EndsWith(".hooks.uitkx", StringComparison.OrdinalIgnoreCase))
            {
                kind = BuilderNodeKind.Hook;
                name = fileName.Substring(0, fileName.Length - ".hooks.uitkx".Length);
                return;
            }
            kind = BuilderNodeKind.Component;
            name = fileName.EndsWith(".uitkx", StringComparison.OrdinalIgnoreCase)
                ? fileName.Substring(0, fileName.Length - ".uitkx".Length)
                : fileName;
        }

        /// <summary>Builds a module for a file, splitting its name and kind and
        /// recording the EOL flavor of the bytes it came from.</summary>
        public static BuilderModule FromFile(string fullPath, string rawText, bool isReadOnly)
        {
            SplitFileName(System.IO.Path.GetFileName(fullPath), out string name, out var kind);
            string lf = NormalizeLf(rawText);
            return new BuilderModule
            {
                Id = NewId(),
                Folder = System.IO.Path.GetDirectoryName(fullPath) ?? string.Empty,
                Name = name,
                Kind = kind,
                BufferText = lf,
                ProjectedText = lf,
                DiskPath = fullPath,
                IsReadOnly = isReadOnly,
                UsedCrlf = rawText != null && rawText.Contains("\r\n"),
            };
        }

        /// <summary>Builds a module that exists only in memory. It is dirty from
        /// the moment it is made, and the first Save writes it.</summary>
        public static BuilderModule Fresh(
            string folder, string name, BuilderNodeKind kind, string initialText)
        {
            return new BuilderModule
            {
                Id = NewId(),
                Folder = folder,
                Name = name,
                Kind = kind,
                BufferText = NormalizeLf(initialText),
                ProjectedText = string.Empty,
                DiskPath = null,
                IsReadOnly = false,
                UsedCrlf = false,
            };
        }

        /// <summary>Replaces the buffer. Rejected on read-only modules - callers
        /// gate the UI, this is the last line of defense - and rejects CR,
        /// because every buffer in the builder is LF-normalized.</summary>
        public void ApplyEdit(string newTextLf)
        {
            if (IsReadOnly)
                throw new InvalidOperationException(
                    $"'{FilePath}' is read-only (immutable package) - the builder must not edit it.");
            if (newTextLf == null)
                throw new ArgumentNullException(nameof(newTextLf));
            if (newTextLf.IndexOf('\r') >= 0)
                throw new ArgumentException("builder buffers are LF-normalized", nameof(newTextLf));
            BufferText = newTextLf;
        }

        /// <summary>Records that the module now matches what is on disk at
        /// <paramref name="projectedPath"/> - the one place both halves of
        /// "clean" are set, so they cannot drift apart.</summary>
        public void MarkProjected(string projectedPath)
        {
            ProjectedText = BufferText;
            DiskPath = projectedPath;
        }

        /// <summary>External change under a CLEAN module: adopt the new disk text
        /// so open cards never keep serving a stale buffer. The caller enforces
        /// the dirty policy - unsaved edits are never clobbered. Returns true
        /// when the text actually changed.</summary>
        public bool AdoptDiskText(string rawText)
        {
            string lf = NormalizeLf(rawText);
            UsedCrlf = rawText != null && rawText.Contains("\r\n");
            if (string.Equals(lf, BufferText, StringComparison.Ordinal))
                return false;
            BufferText = lf;
            ProjectedText = lf;
            return true;
        }
    }
}
#endif
