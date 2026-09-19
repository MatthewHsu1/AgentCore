using AgentCore.Application.Transcript;

namespace AgentCore.Application.Runtime.Turn;

/// <summary>What a turn has published to the caller's file store and not yet attached to a message.</summary>
internal sealed class TurnFiles : TurnAttachments<FileContent>
{
    /// <summary>Files one published file under the outermost tool call, or drops it outside a turn.</summary>
    /// <param name="file">The file the store kept.</param>
    public void Publish(FileContent file)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (OuterCallId is not { } callId)
        {
            return;
        }

        // The same name published twice is one blob: the store replaced the bytes.
        Attach(callId, file, existing => string.Equals(existing.Name, file.Name, StringComparison.Ordinal));
    }
}
