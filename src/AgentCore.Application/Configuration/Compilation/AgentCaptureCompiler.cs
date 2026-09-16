#pragma warning disable MEAI001 // IHostedFileClient is evaluation-only in Microsoft.Extensions.AI 10.10.0.

using AgentCore.Application.Blobs;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime.Harness;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Application.Configuration.Compilation;

/// <summary>
/// The provider that keeps what an agent's sandbox wrote. It is added when three things hold: the
/// agent runs a hosted code tool, its vendor can download what the sandbox wrote, and the document
/// opened a blob store. The <c>code.execute</c> tool is the switch; no yaml on the agent names this.
/// </summary>
internal static class AgentCaptureCompiler
{
    /// <summary>Builds the capture provider, or nothing when there is nothing to capture.</summary>
    /// <param name="defaults">The <c>agents.defaults</c> section, or <see langword="null"/>.</param>
    /// <param name="item">The agent being compiled.</param>
    /// <param name="context">The compile-time seams, including the blob store and the chat clients.</param>
    /// <param name="tools">This agent's compiled tools, or <see langword="null"/> when it advertises none.</param>
    public static HostedFileCaptureProvider? Build(
        AgentDefaults? defaults,
        AgentConfiguration item,
        AgentCompilationContext context,
        IReadOnlyList<AITool>? tools)
    {
        if (context.Blobs is not { } blobs || tools?.Any(static tool => tool is HostedCodeInterpreterTool) != true)
        {
            return null;
        }

        var files = context.ChatClients.GetChatClient(item.Model ?? defaults?.Model).GetService<IHostedFileClient>();

        if (files is null)
        {
            return null;
        }

        return new HostedFileCaptureProvider(
            files,
            blobs,
            BlobPolicy.Default,
            context.Loggers?.CreateLogger<HostedFileCaptureProvider>() ?? NullLogger<HostedFileCaptureProvider>.Instance);
    }
}
