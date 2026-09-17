#pragma warning disable MEAI001 // HostedFileContent.Scope is evaluation-only in Microsoft.Extensions.AI 10.10.0.

using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Blobs;

/// <summary>Finds the files a vendor's sandbox wrote, wherever the vendor surfaced them on a message.</summary>
public static class SandboxFiles
{
    /// <summary>The names of every sandbox file the contents reference, in order, each once.</summary>
    /// <param name="contents">The contents of one message or one streaming update.</param>
    /// <returns>The names a blob was, or would have been, stored under. Only names that pass <see cref="BlobName.IsSafe"/>.</returns>
    public static IReadOnlyList<string> NamesIn(IEnumerable<AIContent> contents)
    {
        ArgumentNullException.ThrowIfNull(contents);

        List<string> names = [];

        foreach (var file in In(contents))
        {
            var name = NameOf(file);

            if (BlobName.IsSafe(name) && !names.Contains(name, StringComparer.Ordinal))
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>
    /// The facts of every sandbox file the contents reference that the capture kept, in order, each
    /// name once. A reference the capture never stamped, or stamped as refused, is not a file
    /// anyone can fetch and is left out.
    /// </summary>
    /// <remarks>
    /// The facts are read off the message and never off the store, so a history read makes no
    /// request per file. That is how every production chat keeps its attachments: the bytes in an
    /// object store, the index on the message.
    /// </remarks>
    /// <param name="contents">The contents of one message or one streaming update.</param>
    /// <returns>The kept files, as the store described them at capture time.</returns>
    public static IReadOnlyList<SandboxFileFacts> KeptIn(IEnumerable<AIContent> contents)
    {
        ArgumentNullException.ThrowIfNull(contents);

        List<SandboxFileFacts> kept = [];

        foreach (var file in In(contents))
        {
            if (FactsOf(file) is not { } facts)
            {
                continue;
            }

            // A second write with the same name replaced the first in the store, so the later
            // facts win, in the place the first reference took.
            var at = kept.FindIndex(known => string.Equals(known.Name, facts.Name, StringComparison.Ordinal));

            if (at >= 0)
            {
                kept[at] = facts;
            }
            else
            {
                kept.Add(facts);
            }
        }

        return kept;
    }

    /// <summary>Writes onto the reference what the store kept, so every later read has it without asking the store.</summary>
    /// <param name="file">The vendor's reference, as it sits on the reply.</param>
    /// <param name="blob">What the store wrote.</param>
    public static void MarkKept(HostedFileContent file, BlobRef blob)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(blob);

        file.MediaType = blob.MediaType;

        var properties = file.AdditionalProperties ??= [];
        properties[KeptKey] = true;
        properties[LengthKey] = blob.Length;
    }

    /// <summary>Writes onto the reference that the store did not keep the file, so no read links it.</summary>
    /// <param name="file">The vendor's reference, as it sits on the reply.</param>
    public static void MarkRefused(HostedFileContent file)
    {
        ArgumentNullException.ThrowIfNull(file);

        (file.AdditionalProperties ??= [])[KeptKey] = false;
    }

    /// <summary>The name a sandbox file is stored under: what the model called it, else the vendor's id.</summary>
    internal static string NameOf(HostedFileContent file) => file.Name ?? file.FileId;

    /// <summary>The key under which the capture records whether it kept the file. A wire format: it is stored.</summary>
    private const string KeptKey = "agentcore.kept";

    /// <summary>The key under which the capture records the stored length in bytes. A wire format: it is stored.</summary>
    private const string LengthKey = "agentcore.length";

    /// <summary>Reads the stamp off one reference, or <see langword="null"/> when it was never kept.</summary>
    private static SandboxFileFacts? FactsOf(HostedFileContent file)
    {
        var name = NameOf(file);

        if (!BlobName.IsSafe(name)
            || file.AdditionalProperties is not { } properties
            || !properties.TryGetValue(KeptKey, out var keptValue)
            || !ReadBool(keptValue)
            || !properties.TryGetValue(LengthKey, out var lengthValue)
            || ReadLong(lengthValue) is not { } length)
        {
            return null;
        }

        return new SandboxFileFacts(name, file.MediaType ?? "application/octet-stream", length);
    }

    // A stored message comes back with JsonElement values; a fresh one holds the CLR values written.
    private static bool ReadBool(object? value) => value switch
    {
        bool kept => kept,
        JsonElement { ValueKind: JsonValueKind.True } => true,
        _ => false,
    };

    private static long? ReadLong(object? value) => value switch
    {
        long length => length,
        int length => length,
        JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt64(out var length) => length,
        _ => null,
    };

    /// <summary>Every sandbox file reference in the contents: on the message, or inside an interpreter result.</summary>
    internal static IEnumerable<HostedFileContent> In(IEnumerable<AIContent> contents)
    {
        foreach (var content in contents)
        {
            switch (content)
            {
                case HostedFileContent file:
                    yield return file;
                    break;

                case CodeInterpreterToolResultContent { Outputs: { } outputs }:
                    foreach (var output in outputs)
                    {
                        if (output is HostedFileContent nested)
                        {
                            yield return nested;
                        }
                    }

                    break;
            }
        }
    }
}
