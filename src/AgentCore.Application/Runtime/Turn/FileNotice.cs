using AgentCore.Application.Transcript;

namespace AgentCore.Application.Runtime.Turn
{
    /// <summary>
    /// A file a graph participant published, on its way to the turn's caller around the workflow. The turn's stream
    /// hands the caller the file itself.
    /// </summary>
    /// <param name="file">The file the store kept.</param>
    /// <param name="author">The participant that published it.</param>
    internal sealed class FileNotice(FileContent file, string? author) : NoticeContent
    {
        /// <summary>Gets the file the store kept.</summary>
        public FileContent File { get; } = file;

        /// <summary>Gets the participant that published the file.</summary>
        public string? Author { get; } = author;
    }
}
