---
paths:
  - "**/*.cs"
---

# C#

## Style comes from the analyzers

`.editorconfig` raises every IDE style rule to a suggestion, and SonarQube reports the rest. Those
diagnostics are the style guide. The owner ruled the repo's older habits to be slop, so follow the
diagnostic, not the code around it.

- Clear every IDE and Sonar diagnostic in the code you touch. Most of them push you to the newer
  C# form: collection expressions, pattern matching, `is null`, primary constructors.
- Namespaces are block-scoped. That is the Roslyn default here; do not convert a file to
  file-scoped.
- `dotnet format style` is safe except for these fixers. Pass them to `--exclude-diagnostics` and
  fix by hand:
  - IDE0010, IDE0072: they write `throw new NotImplementedException()` stubs. Name every member
    and keep `_ =>` for CS8524.
  - IDE0130: crashes the workspace. Move or rename the file by hand.
  - IDE0032: rewrites `ref _field` to `ref Property` and breaks `Interlocked`.
  - IDE0001, IDE0002: shorten a cref to `ShellKind.X`, which is ambiguous with MAF Tools.Shell's
    `ShellKind`. Keep the `using ShellKind = ...` alias.
- IDE0010 and Sonar S3458 conflict on empty cases before `default:`. Give the stacked group its
  own body, or replace the switch with a `FrozenDictionary` (see `ConversationEventKinds`).

## File size

Keep each `.cs` file under 400 lines.

**Scope**
- Apply this to files you are **already editing** for the task. Do not go hunting.
- If a file you must edit is over 400 lines, split it first, then make your change.
- Skip generated files and `*.schema.json`.

**Rules**
- One public type per file. Name the file after the type.
- A private helper type gets its own file when it passes 20 lines.
- Split by responsibility. Move one cohesive group of members into its own
  class, in the same namespace and folder.
- A split that only moves lines does not count. `FooPart2.cs` and a new
  `partial` are not a split.
- `partial` is for source generators.

If you cannot find a clean seam, stop and say so in your report. Do not
split at a random line.
