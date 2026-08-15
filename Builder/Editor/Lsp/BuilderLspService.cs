#if UNITY_EDITOR
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace Ruitk.Builder
{
    /// <summary>
    /// Session-scoped owner of the ONE shared LSP client (plan: never
    /// per-window). Windows ask for the client; the service starts and
    /// initializes it once, republishes it to <see cref="BuilderAssetEvents"/>,
    /// and restarts it transparently if the process died (crash, orphan kill).
    /// Concurrent first callers share the same start task.
    /// </summary>
    internal static class BuilderLspService
    {
        private static BuilderLspClient _client;
        private static Task<BuilderLspClient> _starting;

        /// <summary>UB-06: service-level relay of textDocument/publishDiagnostics.
        /// Subscribers attach HERE, once — a crash-restart creates a fresh
        /// client, and subscriptions taken on the dead instance would die
        /// silently with it.</summary>
        public static event System.Action<string, Newtonsoft.Json.Linq.JToken> DiagnosticsPublished;

        public static Task<BuilderLspClient> GetOrStartAsync()
        {
            if (_client != null && _client.IsRunning)
                return Task.FromResult(_client);
            if (_starting == null)
                _starting = StartAsync();
            return _starting;
        }

        private static async Task<BuilderLspClient> StartAsync()
        {
            try
            {
                _client?.Dispose();
                var client = BuilderLspClient.StartOrThrow();
                string projectRoot = Path.GetDirectoryName(Application.dataPath);
                await client.InitializeAsync(projectRoot);
                client.DiagnosticsPublished +=
                    (path, diagnostics) => DiagnosticsPublished?.Invoke(path, diagnostics);
                _client = client;
                BuilderAssetEvents.ActiveClient = client;
                return client;
            }
            finally
            {
                _starting = null;
            }
        }

        public static void Stop()
        {
            _client?.Dispose();
            _client = null;
            BuilderAssetEvents.ActiveClient = null;
        }
    }
}
#endif
