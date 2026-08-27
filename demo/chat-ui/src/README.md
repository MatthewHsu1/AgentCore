# What is ours and what is not

`runtime/` is ours. Everything in it is written and maintained here, and it is the only place a
change to this application's behaviour belongs.

`components/`, `hooks/`, and `lib/` are assistant-ui's and shadcn's. Those files arrived through
their registries and are byte-identical to what the CLI wrote. **Do not edit them.** They carry no
explanatory comments for the same reason: a diff against upstream is the cheap way to see whether
one has been tampered with, and re-running the commands below has to stay a clean overwrite rather
than a merge.

To refresh them:

    npx shadcn@latest add https://r.assistant-ui.com/thread.json
    npx shadcn@latest add https://r.assistant-ui.com/threadlist-sidebar.json
    npx shadcn@latest add https://ui.shadcn.com/r/styles/new-york-v4/utils.json

The first two pull everything else transitively — the message parts, the thread list, and the
shadcn primitives under `components/ui`. The third is listed separately because the CLI does not
write `lib/utils.ts` when it believes the file already exists, and a fresh checkout that runs only
the first two therefore ends up without it.

The CLI resolves `@/components` by reading `../tsconfig.json` — the solution file, not
`tsconfig.app.json`. That is why the solution file carries a `compilerOptions.paths` entry it never
compiles with. Remove it and the next `shadcn add` writes its files into a directory literally named
`@`.

Radix is the primitive library here, not Base UI. assistant-ui publishes both flavours and the
registry's default entries resolved to Radix on their own; nothing was chosen by hand.

If a vendored file ever needs a change to work here, it is not vendorable. Wrap it from `runtime/`
instead of editing it in place.