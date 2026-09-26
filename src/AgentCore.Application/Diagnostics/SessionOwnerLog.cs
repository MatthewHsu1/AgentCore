using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Diagnostics
{
    /// <summary>
    /// Every line the session owner writes: a conversation's workspace folder, and the close or idle-expiry
    /// routine that deletes it, disposes its shells, and releases its background sessions.
    /// </summary>
    internal static partial class SessionOwnerLog
    {
        /// <summary>A conversation's workspace folder could not be deleted when its session closed or unloaded.</summary>
        /// <param name="logger">The logger of the session.</param>
        /// <param name="path">The folder that could not be deleted.</param>
        /// <param name="exception">The cause.</param>
        [LoggerMessage(
            EventId = 20,
            Level = LogLevel.Warning,
            Message = "Could not delete the conversation workspace at '{Path}'.")]
        public static partial void WorkspaceDeleteFailed(ILogger logger, string path, Exception exception);

        /// <summary>A conversation's shell executor could not be disposed when its session closed or unloaded.</summary>
        /// <param name="logger">The logger of the session.</param>
        /// <param name="workspace">The conversation's workspace folder, whose shell failed to dispose.</param>
        /// <param name="exception">The cause.</param>
        [LoggerMessage(
            EventId = 21,
            Level = LogLevel.Warning,
            Message = "Could not dispose a shell: executor of the conversation at workspace '{Workspace}'.")]
        public static partial void ShellDisposeFailed(ILogger logger, string workspace, Exception exception);

        /// <summary>A conversation's background agent sessions could not be released when its session closed or unloaded.</summary>
        /// <param name="logger">The logger of the session.</param>
        /// <param name="conversationId">The id of the conversation whose children were being released.</param>
        /// <param name="exception">The cause.</param>
        [LoggerMessage(
            EventId = 22,
            Level = LogLevel.Warning,
            Message = "Could not release the background agent sessions of the conversation '{ConversationId}'.")]
        public static partial void BackgroundReleaseFailed(ILogger logger, string conversationId, Exception exception);

        /// <summary>A background provider's release waited its whole timeout: a child did not acknowledge its cancel.</summary>
        /// <param name="logger">The logger of the session.</param>
        /// <param name="conversationId">The id of the conversation whose children were being released.</param>
        /// <param name="timeoutSeconds">How long the release waited, in seconds.</param>
        [LoggerMessage(
            EventId = 43,
            Level = LogLevel.Warning,
            Message = "A background child of the conversation '{ConversationId}' did not stop within {TimeoutSeconds} s "
                + "of its cancel. The session was released anyway, and the child may still be running.")]
        public static partial void BackgroundReleaseTimedOut(ILogger logger, string conversationId, double timeoutSeconds);

        /// <summary>An idle session could not be unloaded cleanly.</summary>
        /// <param name="logger">The logger of the session.</param>
        /// <param name="conversationId">The id of the conversation that expired.</param>
        /// <param name="exception">The cause.</param>
        [LoggerMessage(
            EventId = 44,
            Level = LogLevel.Error,
            Message = "The idle conversation '{ConversationId}' did not unload cleanly. It is no longer held, but "
                + "its last words, its shells, or its workspace folder may not have been cleaned up.")]
        public static partial void SessionExpiryFailed(ILogger logger, string conversationId, Exception exception);

        /// <summary>The idle check could not read whether a background child is still running.</summary>
        /// <param name="logger">The logger of the session.</param>
        /// <param name="conversationId">The id of the conversation.</param>
        /// <param name="exception">The cause.</param>
        [LoggerMessage(
            EventId = 45,
            Level = LogLevel.Warning,
            Message = "Could not read whether conversation '{ConversationId}' has a background child still running. "
                + "The idle check treats it as one, and holds the session until a later poll can read it cleanly.")]
        public static partial void BackgroundChildCheckFailed(ILogger logger, string conversationId, Exception exception);

        /// <summary>A conversation's workspace folder could not be stamped at the start of a turn.</summary>
        /// <param name="logger">The logger of the session.</param>
        /// <param name="path">The folder that could not be stamped.</param>
        /// <param name="exception">The cause.</param>
        [LoggerMessage(
            EventId = 46,
            Level = LogLevel.Warning,
            Message = "Could not stamp the conversation workspace at '{Path}'. The boot sweep of another process "
                + "sharing this workspace root may mistake it for one a crashed process left behind.")]
        public static partial void WorkspaceStampFailed(ILogger logger, string path, Exception exception);

        /// <summary>The boot sweep could not list the folders directly under the workspace root.</summary>
        /// <param name="logger">The boot's own logger.</param>
        /// <param name="root">The workspace root.</param>
        /// <param name="exception">The cause.</param>
        [LoggerMessage(
            EventId = 47,
            Level = LogLevel.Warning,
            Message = "The boot sweep could not list the folders under workspace root '{Root}'. No folder was swept this boot.")]
        public static partial void WorkspaceSweepListFailed(ILogger logger, string root, Exception exception);

        /// <summary>The boot sweep found a folder older than the idle timeout, but could not delete it.</summary>
        /// <param name="logger">The boot's own logger.</param>
        /// <param name="path">The folder the sweep could not delete.</param>
        /// <param name="exception">The cause.</param>
        [LoggerMessage(
            EventId = 48,
            Level = LogLevel.Warning,
            Message = "The boot sweep could not delete the workspace folder '{Path}' left behind by a crashed process.")]
        public static partial void WorkspaceSweepDeleteFailed(ILogger logger, string path, Exception exception);

        /// <summary>A conversation's workspace marker could not be written when its folder was created or reused.</summary>
        /// <param name="logger">The logger of the session.</param>
        /// <param name="path">The marker path that could not be written.</param>
        /// <param name="exception">The cause.</param>
        [LoggerMessage(
            EventId = 49,
            Level = LogLevel.Warning,
            Message = "Could not write the workspace marker at '{Path}'. The folder still works, but the boot sweep "
                + "will never consider it for cleanup.")]
        public static partial void WorkspaceMarkerWriteFailed(ILogger logger, string path, Exception exception);

        /// <summary>A conversation's workspace marker could not be deleted when its folder closed or unloaded.</summary>
        /// <param name="logger">The logger of the session.</param>
        /// <param name="path">The marker path that could not be deleted.</param>
        /// <param name="exception">The cause.</param>
        [LoggerMessage(
            EventId = 50,
            Level = LogLevel.Warning,
            Message = "Could not delete the workspace marker at '{Path}'.")]
        public static partial void WorkspaceMarkerDeleteFailed(ILogger logger, string path, Exception exception);
    }
}
