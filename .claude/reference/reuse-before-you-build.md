# Reuse before you build

Before you write a new interface, wrapper, or helper, find out if a package
already does the job.

**Scope**
- Applies when you are about to add an abstraction or a utility.
- Read `Directory.Packages.props` for the packages we already have.
- Search `Microsoft.Extensions.AI*` and `Microsoft.Agents.AI*` first. They are
  our base. Then search the rest.

**Prove the overlap**
- "The framework already does this" is proof only when a scratch probe
  compiles and runs against the pinned version.
- Put the probe in the scratchpad. Do not commit it.
- Name the exact type and member in your report, such as
  `DelegatingAIFunction`.
- If the probe fails, say what is missing, then write our own.
- Reason: in the 2026-08-26 framework audit, 4 of 5 "already covered" claims
  were wrong.

**A package we do not have yet**
- Propose the package before you write the code. Do not hand-roll it and
  mention the package afterwards.
- Propose one when it replaces more than ~100 lines of our code, or when the
  job is a known-hard domain: parsing, unicode, time zones, crypto, retry and
  backoff, globbing, schema validation.
- Do not install it. Give the owner a short note with:
  - the package, the exact version, and the licence
  - what it replaces, in lines and files
  - how many packages it pulls in, and any native binaries
  - the date of the last release
- AgentCore ships as NuGet packages. Every dependency lands on every consumer.
- MIT, Apache-2.0, BSD, and MS-PL are safe. A copyleft licence (GPL, AGPL,
  LGPL, MPL) is a stop. Report it and stop.
- Use a stable version. Prerelease needs the owner's OK.
- Pin the version in `Directory.Packages.props`.

**Write our own when**
- No maintained package exists.
- The library type would leak into our public API.
- Our need is under ~20 lines and the package is not.

Say which one applies in your report.
