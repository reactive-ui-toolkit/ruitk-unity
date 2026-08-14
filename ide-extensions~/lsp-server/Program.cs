using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Server;
using UitkxLanguageServer;
using UitkxLanguageServer.Roslyn;

// Redirect Console.Error to stderr (Console.Out is the LSP transport)
Console.InputEncoding = System.Text.Encoding.UTF8;
Console.OutputEncoding = System.Text.Encoding.UTF8;

ServerLog.Log(
    $"=== UitkxLanguageServer starting (PID={System.Diagnostics.Process.GetCurrentProcess().Id}) ==="
);

// Detect VS2022 client via --vs2022 flag (passed by UitkxLanguageClient, not by VSCode).
CapabilityPatchStream.IsVisualStudio = args.Any(a =>
    a.Equals("--vs2022", StringComparison.OrdinalIgnoreCase));
ServerLog.Log($"IsVisualStudio={CapabilityPatchStream.IsVisualStudio} (args=[{string.Join(", ", args)}])");

// Wrap stdout so we can patch the InitializeResult capabilities before VS2022 sees them.
// OmniSharp v0.19.9 emits null for all providers (dynamic registration), which causes
// VS2022's built-in navigation handlers to suppress Ctrl+Click / F12.
var patchedOutput = new CapabilityPatchStream(Console.OpenStandardOutput());

try
{
var server = await LanguageServer.From(options =>
    options
        .WithInput(Console.OpenStandardInput())
        .WithOutput(patchedOutput)
        .WithLoggerFactory(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance)
        // Force OmniSharp to use STATIC registration (capabilities in InitializeResult)
        // instead of dynamic registration (client/registerCapability).
        // VS2022's ILanguageClient framework ignores dynamic registrations, so without
        // this it sees all providers as null and never routes F12/Shift+F12/F2/etc.
        .OnInitialize((server, request, token) =>
        {
            var td = request.Capabilities?.TextDocument;
            if (td != null)
            {
                td.Definition = new DefinitionCapability { DynamicRegistration = false };
                td.Completion = new CompletionCapability { DynamicRegistration = false };
                td.Hover = new HoverCapability { DynamicRegistration = false };
                td.SignatureHelp = new SignatureHelpCapability { DynamicRegistration = false };
                td.Formatting = new DocumentFormattingCapability { DynamicRegistration = false };
                td.Rename = new RenameCapability { DynamicRegistration = false };
                td.References = new ReferenceCapability { DynamicRegistration = false };
                td.SemanticTokens = new SemanticTokensCapability { DynamicRegistration = false };
            }
            return Task.CompletedTask;
        })
        .WithHandler<TextSyncHandler>()
        .WithHandler<CompletionHandler>()
        .WithHandler<SignatureHelpHandler>()
        .WithHandler<HoverHandler>()
        .WithHandler<FormattingHandler>()
        .WithHandler<SemanticTokensHandler>()
        .WithHandler<DefinitionHandler>()
        .WithHandler<ImportCodeActionHandler>()
        .WithHandler<RenameHandler>()
        .WithHandler<ReferencesHandler>()
        .WithHandler<WatchedFilesHandler>()
        .WithHandler<RuitkSchemaHandler>()
        .WithHandler<RuitkHooksHandler>()
        .WithHandler<RuitkComponentPropsHandler>()
        .WithHandler<RuitkWorkspaceGraphHandler>()
        .WithServices(services =>
        {
            services.AddSingleton<UitkxSchema>();
            services.AddSingleton<DocumentStore>();
            services.AddSingleton<WorkspaceIndex>();
            services.AddSingleton<RoslynHost>();
            services.AddSingleton<DiagnosticsPublisher>();
            services.AddSingleton<OmniSharp.Extensions.LanguageServer.Protocol.Server.IOnLanguageServerStarted>(
                new StartupLogger()
            );
            services.AddSingleton<OmniSharp.Extensions.LanguageServer.Protocol.Server.IOnLanguageServerStarted>(
                sp => sp.GetRequiredService<WorkspaceIndex>()
            );
            // Notify RoslynHost of the workspace root once the server has completed
            // the Initialize handshake (rootUri / rootPath is available at that point).
            services.AddSingleton<OmniSharp.Extensions.LanguageServer.Protocol.Server.IOnLanguageServerStarted>(
                sp =>
                {
                    var roslynHost = sp.GetRequiredService<RoslynHost>();

                    // Wire DiagnosticsPublisher → RoslynHost so that
                    // RevalidateOpenDocuments triggers full T3 Roslyn rebuilds
                    // (not just T1+T2) when background index changes occur.
                    var diagnostics = sp.GetRequiredService<DiagnosticsPublisher>();
                    diagnostics.SetRoslynHost(roslynHost);

                    return new RoslynHostStartup(roslynHost, sp.GetRequiredService<WorkspaceIndex>());
                });
        })
);

ServerLog.Log("=== LanguageServer.From completed — waiting for exit ===");
await server.WaitForExit;
}
catch (Exception ex)
{
    ServerLog.Log($"=== FATAL: LanguageServer.From threw: {ex} ===");
}
