using AgentCore.Application.Blobs;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Application.Tools.Builtin
{
    /// <summary>
    /// The <c>uses: file.publish</c> builtin: one <see cref="FilePublishTool"/> over the blob store.
    /// </summary>
    internal sealed class FilePublishToolDefinition : IBuiltinToolDefinition
    {
        /// <inheritdoc />
        public string Name => BuiltinToolNames.FilePublish;

        /// <inheritdoc />
        public string DefaultDescription =>
            "Hands one file from the workspace to the person. "
            + "A file that is not published is lost when the conversation ends. "
            + "The result has a `link`. Link the file in your reply like [Sales by month](sandbox:/sales.csv), "
            + "copying the `link` value exactly, with no angle brackets or other changes. Never write any other address for the file. "
            + "Give each file a distinct name: a second file with the same name replaces the first.";

        /// <inheritdoc />
        public AITool Build(ToolConfiguration tool, BuiltinToolPorts ports)
        {
            ArgumentNullException.ThrowIfNull(tool);
            ArgumentNullException.ThrowIfNull(ports);

            IBlobStore blobs = ports.Blobs ?? throw BuiltinToolSource.Unbound(tool, Name, "providers.blobs");

            string root = ports.WorkspaceRoot ?? throw BuiltinToolSource.Unbound(
                tool, Name, "the workspace root (options.UseWorkspace(...))");

            return new FilePublishTool(
                tool,
                blobs,
                ports.BlobPolicy ?? BlobPolicy.Default,
                root,
                ports.Loggers?.CreateLogger<FilePublishTool>() ?? NullLogger<FilePublishTool>.Instance)
                .AsAIFunction();
        }
    }
}
