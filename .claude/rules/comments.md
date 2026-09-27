---
paths:
  - "**/*.cs"
  - "**/*.sql"
  - "**/*.csproj"
  - "**/*.props"
  - "**/*.targets"
---

# Comments

Write comments a maintainer needs. Do not write comments that repeat the code.

**Default: no comment.** Code should say *what*. A comment says *why*.
Before you keep a comment, ask: "Would a good teammate be surprised here
without it?" If no, delete it.

If you cannot name the reader and the surprise, do not write the comment.

## Do write these

- Why a non-obvious choice was made, when the obvious choice is wrong
- A workaround, with the cause and a link (bug report, issue, ticket)
- An invariant or precondition a caller must hold
- Units, ranges, and limits that the type does not show (`ms`, `0-1`, `UTC`)
- A pointer to the spec or design section a rule comes from
- A real safety warning: races, ordering, money, data loss
- Doc comments on public API, in the language's normal format

## Never write these

- Restating the line: `// increment the counter`, `// return the result`
- Step narration: `// Step 1: ...`, `// First we ..., then we ...`
- Changelog or diff talk: `// added for task 5`, `// was 30s before`,
  `// new`, `// removed the old path`
- Talking to the reviewer: `// as requested`, `// note: I chose this because`
- Obvious types or names: `// the user id (string)`
- Banner art or divider lines: `// ===== HELPERS =====`
- Commented-out code. Delete it. Git has it.
- Bare `TODO`. Only keep a TODO with a ticket link.

## Style

- Match the comment density of the file you are editing. Do not add a new
  style to an old file.
- One idea per comment. Keep it short.
- Say it in plain words. No hedging, no apologies, no jokes.
- Keep the comment next to the code it explains.
- If a comment explains a confusing block, try to fix the code first. A better
  name or a small function often beats the comment.

## Cleaning up old comments

Fix comment slop in code you are **already editing**. Do not go hunting.

**Scope**
- Only inside the function, block, or file you are changing for the task.
- Never open an unrelated file just to clean comments.
- Never make a "comment cleanup" commit unless the user asks for one.

**Do this when you see it**
- Delete any comment that matches the "Never write these" list.
- Delete a comment that the code no longer matches. A stale comment is worse
  than no comment.
- Delete commented-out code.
- If a comment says *why* but says it badly, keep the *why* and shorten it.

**Do not touch these**
- Licence and copyright headers.
- Machine-read comments: `// eslint-disable`, `# type: ignore`, `#pragma`,
  `// nolint`, `<!-- prettier-ignore -->`, codegen markers.
- Doc comments that a doc tool publishes.
- A comment you do not understand. It may hold knowledge that is not in the
  code. Leave it, and say so in your report.
