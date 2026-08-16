#if UNITY_EDITOR
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using Debug = UnityEngine.Debug;

namespace Ruitk.Builder
{
    /// <summary>
    /// The builder's LSP client: spawns the UITKX language server (stdio,
    /// Content-Length framing), correlates requests, marshals notifications to
    /// the editor main thread, debounces full-document sync off the typing path
    /// (VE-R10 — the server publishes diagnostics synchronously per didChange),
    /// and owns the process lifecycle across domain reloads: the PID rides
    /// <see cref="SessionState"/>, a stale child is killed before respawn, and
    /// teardown tries the graceful shutdown/exit pair before resorting to Kill.
    /// </summary>
    internal sealed class BuilderLspClient : IDisposable
    {
        private const string PidSessionKey = "Ruitk.Builder.LspPid";
        private const int DidChangeDebounceMs = 150;

        private Process _process;
        private StreamWriter _stdin;
        private Thread _readerThread;
        private volatile bool _disposed;
        private int _nextRequestId;

        private readonly object _writeLock = new object();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JToken>> _pending =
            new ConcurrentDictionary<int, TaskCompletionSource<JToken>>();
        private readonly ConcurrentQueue<(string Method, JToken Params)> _notifications =
            new ConcurrentQueue<(string, JToken)>();
        private readonly Dictionary<string, CancellationTokenSource> _pendingChanges =
            new Dictionary<string, CancellationTokenSource>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _documentVersions =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>uri (file path form), diagnostics array — already on the main thread.</summary>
        public event Action<string, JToken> DiagnosticsPublished;

        public bool IsRunning => _process != null && !_process.HasExited && !_disposed;

        // ── Lifecycle ────────────────────────────────────────────────────────

        public static BuilderLspClient StartOrThrow()
        {
            KillStaleFromPriorDomain();

            string dotnet = RuitkDotnetLocator.Resolve()
                ?? throw new InvalidOperationException(RuitkDotnetLocator.FailureMessage);
            string serverDll = RuitkDotnetLocator.ResolveServerDll()
                ?? throw new InvalidOperationException(
                    "UITKX language server not found (probed Server~/, Server/, and the dev repo's "
                    + "ide-extensions~/vscode/server/). The package install is incomplete.");

            var client = new BuilderLspClient();
            client.Spawn(dotnet, serverDll);
            return client;
        }

        // The session key holds a comma-separated PID LIST, not one slot: several
        // clients can exist in one session (spike probes + the shared service),
        // and a single-slot key leaked every PID but the last across reloads —
        // observed as four orphaned dotnet processes in one owner session.

        private static List<int> ReadPidList()
        {
            var pids = new List<int>();
            foreach (string part in (SessionState.GetString(PidSessionKey, "") ?? "").Split(','))
                if (int.TryParse(part, out int pid) && pid > 0)
                    pids.Add(pid);
            return pids;
        }

        private static void WritePidList(List<int> pids) =>
            SessionState.SetString(PidSessionKey, string.Join(",", pids));

        private static void RegisterPid(int pid)
        {
            var pids = ReadPidList();
            if (!pids.Contains(pid))
                pids.Add(pid);
            WritePidList(pids);
        }

        private static void UnregisterPid(int pid)
        {
            var pids = ReadPidList();
            pids.Remove(pid);
            WritePidList(pids);
        }

        private static bool s_staleSweepDone;

        private static void KillStaleFromPriorDomain()
        {
            // Once per DOMAIN (statics reset on reload): the listed PIDs are
            // prior-domain leftovers on the first spawn, but live siblings on
            // every later spawn in the same domain — never kill those.
            if (s_staleSweepDone)
                return;
            s_staleSweepDone = true;

            var stalePids = ReadPidList();
            if (stalePids.Count == 0)
                return;
            SessionState.EraseString(PidSessionKey);
            foreach (int stalePid in stalePids)
                KillIfAliveServer(stalePid);
        }

        private static void KillIfAliveServer(int stalePid)
        {
            try
            {
                var stale = Process.GetProcessById(stalePid);
                if (!stale.HasExited
                    && stale.ProcessName.IndexOf("dotnet", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    stale.Kill();
                }
            }
            catch
            {
                // Already gone - the desired state.
            }
        }

        private void Spawn(string dotnetPath, string serverDll)
        {
            var psi = new ProcessStartInfo
            {
                FileName = dotnetPath,
                Arguments = $"\"{serverDll}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(serverDll) ?? ".",
            };
            psi.EnvironmentVariables["DOTNET_ROLL_FORWARD"] = "LatestMajor";

            _process = Process.Start(psi)
                ?? throw new InvalidOperationException("failed to start the language server process");
            RegisterPid(_process.Id);

            _stdin = new StreamWriter(_process.StandardInput.BaseStream, new UTF8Encoding(false))
            {
                AutoFlush = true,
            };

            _readerThread = new Thread(ReadLoop) { IsBackground = true, Name = "RuitkLspReader" };
            _readerThread.Start();

            _process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    Debug.LogWarning($"[RUITK Builder] LSP stderr: {e.Data}");
            };
            _process.BeginErrorReadLine();

            EditorApplication.update -= PumpNotifications;
            EditorApplication.update += PumpNotifications;
        }

        /// <summary>
        /// The initialize handshake. <paramref name="projectRoot"/> must be the
        /// Unity project root exactly (the folder holding Library/ScriptAssemblies):
        /// reference discovery walks up but the server's recompile watcher does not.
        /// </summary>
        public async Task InitializeAsync(string projectRoot)
        {
            var initParams = new JObject
            {
                ["processId"] = Process.GetCurrentProcess().Id,
                ["rootUri"] = new Uri(Path.GetFullPath(projectRoot)).AbsoluteUri,
                ["capabilities"] = new JObject(),
            };
            // The server advertises null for most providers (OmniSharp 0.19.9);
            // the capabilities in the response are deliberately IGNORED - every
            // method the builder needs is called blind, per the VS Code precedent.
            await Request("initialize", initParams);
            Notify("initialized", new JObject());
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            EditorApplication.update -= PumpNotifications;

            foreach (var cts in _pendingChanges.Values)
                cts.Cancel();
            _pendingChanges.Clear();

            try
            {
                var shutdown = Request("shutdown", new JObject());
                shutdown.Wait(TimeSpan.FromSeconds(2));
                Notify("exit", new JObject());
                if (_process != null && !_process.WaitForExit(2000))
                    _process.Kill();
            }
            catch
            {
                try { if (_process != null && !_process.HasExited) _process.Kill(); }
                catch { }
            }
            finally
            {
                if (_process != null)
                {
                    try { UnregisterPid(_process.Id); }
                    catch { }
                }
                foreach (var tcs in _pending.Values)
                    tcs.TrySetCanceled();
                _pending.Clear();
                _stdin?.Dispose();
                _process?.Dispose();
                _process = null;
            }
        }

        // ── Wire protocol ────────────────────────────────────────────────────

        public Task<JToken> Request(string method, JToken @params)
        {
            if (!IsRunning && method != "initialize" && method != "shutdown")
                return Task.FromException<JToken>(
                    new InvalidOperationException("language server is not running"));

            int id = Interlocked.Increment(ref _nextRequestId);
            var tcs = new TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;

            var message = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = @params,
            };
            WriteMessage(message);
            return tcs.Task;
        }

        public void Notify(string method, JToken @params)
        {
            var message = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method,
                ["params"] = @params,
            };
            WriteMessage(message);
        }

        private void WriteMessage(JObject message)
        {
            string json = message.ToString(Formatting.None);
            byte[] payload = Encoding.UTF8.GetBytes(json);
            lock (_writeLock)
            {
                if (_stdin == null)
                    return;
                _stdin.Write($"Content-Length: {payload.Length}\r\n\r\n");
                _stdin.Flush();
                _stdin.BaseStream.Write(payload, 0, payload.Length);
                _stdin.BaseStream.Flush();
            }
        }

        private void ReadLoop()
        {
            var stdout = _process.StandardOutput.BaseStream;
            try
            {
                while (!_disposed)
                {
                    int contentLength = ReadContentLength(stdout);
                    if (contentLength < 0)
                        return;

                    byte[] buffer = new byte[contentLength];
                    int read = 0;
                    while (read < contentLength)
                    {
                        int n = stdout.Read(buffer, read, contentLength - read);
                        if (n <= 0)
                            return;
                        read += n;
                    }

                    var message = JObject.Parse(Encoding.UTF8.GetString(buffer));
                    DispatchIncoming(message);
                }
            }
            catch (System.Threading.ThreadAbortException)
            {
                // Normal teardown: Unity aborts background threads on every
                // domain reload. Warning on it spammed the console once per
                // recompile without carrying information.
            }
            catch (Exception ex)
            {
                if (!_disposed)
                    Debug.LogWarning($"[RUITK Builder] LSP read loop ended: {ex.Message}");
            }
        }

        private static int ReadContentLength(Stream stdout)
        {
            int contentLength = -1;
            var line = new StringBuilder(64);
            while (true)
            {
                int b = stdout.ReadByte();
                if (b < 0)
                    return -1;
                if (b == '\n')
                {
                    string header = line.ToString().TrimEnd('\r');
                    line.Clear();
                    if (header.Length == 0)
                        return contentLength;
                    const string prefix = "Content-Length:";
                    if (header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        int.TryParse(header.Substring(prefix.Length).Trim(), out contentLength);
                }
                else
                {
                    line.Append((char)b);
                }
            }
        }

        private void DispatchIncoming(JObject message)
        {
            var idToken = message["id"];
            string method = message.Value<string>("method");

            if (method == null && idToken != null)
            {
                // Response to one of ours.
                if (_pending.TryRemove(idToken.Value<int>(), out var tcs))
                {
                    var error = message["error"];
                    if (error != null)
                        tcs.TrySetException(new InvalidOperationException(
                            $"LSP error {error.Value<int?>("code")}: {error.Value<string>("message")}"));
                    else
                        tcs.TrySetResult(message["result"]);
                }
                return;
            }

            if (method != null && idToken != null)
            {
                // Server->client request (client/registerCapability etc.) - the
                // builder consumes none of them; answer null so the pipeline
                // never stalls on an unanswered request.
                var reply = new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = idToken,
                    ["result"] = JValue.CreateNull(),
                };
                WriteMessage(reply);
                return;
            }

            if (method != null)
                _notifications.Enqueue((method, message["params"]));
        }

        private void PumpNotifications()
        {
            while (_notifications.TryDequeue(out var item))
            {
                if (item.Method == "textDocument/publishDiagnostics")
                {
                    string uri = item.Params?.Value<string>("uri");
                    if (uri != null)
                    {
                        string path;
                        try { path = new Uri(uri).LocalPath; }
                        catch { continue; }
                        DiagnosticsPublished?.Invoke(path, item.Params["diagnostics"]);
                    }
                }
            }
        }

        // ── Document sync ────────────────────────────────────────────────────

        private static string PathToUri(string filePath) =>
            new Uri(Path.GetFullPath(filePath)).AbsoluteUri;

        public void DidOpen(string filePath, string textLf)
        {
            _documentVersions[filePath] = 1;
            Notify("textDocument/didOpen", new JObject
            {
                ["textDocument"] = new JObject
                {
                    ["uri"] = PathToUri(filePath),
                    ["languageId"] = filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        ? "csharp" : "uitkx",
                    ["version"] = 1,
                    ["text"] = textLf,
                },
            });
        }

        /// <summary>
        /// Debounced full-document sync: coalesces to last-writer-wins per path.
        /// The server's diagnostics publish is synchronous per didChange, so the
        /// client MUST keep intermediate keystrokes off the wire.
        /// </summary>
        public void DidChangeDebounced(string filePath, string textLf)
        {
            CancellationTokenSource cts;
            lock (_pendingChanges)
            {
                if (_pendingChanges.TryGetValue(filePath, out var old))
                    old.Cancel();
                cts = new CancellationTokenSource();
                _pendingChanges[filePath] = cts;
            }

            _ = Task.Run(async () =>
            {
                try { await Task.Delay(DidChangeDebounceMs, cts.Token); }
                catch (TaskCanceledException) { return; }
                lock (_pendingChanges)
                {
                    if (!_pendingChanges.TryGetValue(filePath, out var current) || current != cts)
                        return;
                    _pendingChanges.Remove(filePath);
                }
                SendDidChangeNow(filePath, textLf);
            });
        }

        public void SendDidChangeNow(string filePath, string textLf)
        {
            int version;
            lock (_documentVersions)
            {
                _documentVersions.TryGetValue(filePath, out version);
                version++;
                _documentVersions[filePath] = version;
            }
            Notify("textDocument/didChange", new JObject
            {
                ["textDocument"] = new JObject
                {
                    ["uri"] = PathToUri(filePath),
                    ["version"] = version,
                },
                ["contentChanges"] = new JArray(new JObject { ["text"] = textLf }),
            });
        }

        public void DidClose(string filePath)
        {
            lock (_pendingChanges)
            {
                if (_pendingChanges.TryGetValue(filePath, out var cts))
                {
                    cts.Cancel();
                    _pendingChanges.Remove(filePath);
                }
            }
            _documentVersions.Remove(filePath);
            Notify("textDocument/didClose", new JObject
            {
                ["textDocument"] = new JObject { ["uri"] = PathToUri(filePath) },
            });
        }

        public void NotifyWatchedFilesChanged(IReadOnlyList<(string Path, int ChangeType)> changes)
        {
            var arr = new JArray();
            foreach (var (path, changeType) in changes)
                arr.Add(new JObject { ["uri"] = PathToUri(path), ["type"] = changeType });
            Notify("workspace/didChangeWatchedFiles", new JObject { ["changes"] = arr });
        }

        // ── Typed conveniences ───────────────────────────────────────────────

        public Task<JToken> RequestSchema() => Request("ruitk/schema", new JObject());
        public Task<JToken> RequestHooks() => Request("ruitk/hooks", new JObject());

        public Task<JToken> RequestComponentProps(string name) =>
            Request("ruitk/componentProps", new JObject { ["name"] = name });

        public Task<JToken> RequestWorkspaceGraph() =>
            Request("ruitk/workspaceGraph", new JObject());

        public Task<JToken> RequestCompletion(string filePath, int line0, int character0) =>
            Request("textDocument/completion", new JObject
            {
                ["textDocument"] = new JObject { ["uri"] = PathToUri(filePath) },
                ["position"] = new JObject { ["line"] = line0, ["character"] = character0 },
            });

        public Task<JToken> RequestHover(string filePath, int line0, int character0) =>
            Request("textDocument/hover", new JObject
            {
                ["textDocument"] = new JObject { ["uri"] = PathToUri(filePath) },
                ["position"] = new JObject { ["line"] = line0, ["character"] = character0 },
            });

        public Task<JToken> RequestFormatting(string filePath) =>
            Request("textDocument/formatting", new JObject
            {
                ["textDocument"] = new JObject { ["uri"] = PathToUri(filePath) },
                ["options"] = new JObject { ["tabSize"] = 4, ["insertSpaces"] = true },
            });

        public Task<JToken> RequestSemanticTokens(string filePath) =>
            Request("textDocument/semanticTokens/full", new JObject
            {
                ["textDocument"] = new JObject { ["uri"] = PathToUri(filePath) },
            });
    }

    /// <summary>
    /// Feeds asset events into <c>workspace/didChangeWatchedFiles</c> — the server
    /// has no file watcher of its own for sources (VS Code's client library does
    /// this for the extension; the builder synthesizes it from Unity's importer,
    /// the same signal HMR trusts).
    /// </summary>
    internal sealed class BuilderAssetEvents : AssetPostprocessor
    {
        internal static BuilderLspClient ActiveClient;

        /// <summary>Full paths of imported .uitkx files. The window's clean
        /// sessions reload from these — external syncs and git pulls must not
        /// leave open cards serving stale buffers. Fired regardless of LSP
        /// state.</summary>
        internal static event Action<List<string>> UitkxImported;

        private static void OnPostprocessAllAssets(
            string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            var importedUitkx = new List<string>();
            foreach (var p in imported)
                if (p.EndsWith(".uitkx", StringComparison.OrdinalIgnoreCase))
                    importedUitkx.Add(Path.GetFullPath(p));
            if (importedUitkx.Count > 0)
                UitkxImported?.Invoke(importedUitkx);

            var client = ActiveClient;
            if (client == null || !client.IsRunning)
                return;

            var changes = new List<(string, int)>();
            foreach (var p in imported)
                if (IsInteresting(p))
                    changes.Add((Path.GetFullPath(p), 2));
            foreach (var p in deleted)
                if (IsInteresting(p))
                    changes.Add((Path.GetFullPath(p), 3));
            foreach (var p in moved)
                if (IsInteresting(p))
                    changes.Add((Path.GetFullPath(p), 1));
            foreach (var p in movedFrom)
                if (IsInteresting(p))
                    changes.Add((Path.GetFullPath(p), 3));

            if (changes.Count > 0)
                client.NotifyWatchedFilesChanged(changes);
        }

        private static bool IsInteresting(string assetPath) =>
            assetPath.EndsWith(".uitkx", StringComparison.OrdinalIgnoreCase)
            || assetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    }
}
#endif
