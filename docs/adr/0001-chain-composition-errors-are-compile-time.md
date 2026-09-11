---
authors: [Theauxm]
areas: [platform]
status: accepted
---

# A mis-composed chain does not compile

`Trax.Core` ships a Roslyn analyzer inside the NuGet package, at `analyzers/dotnet/cs`.
It triggers on a `Resolve()` call, walks back through the fluent chain that produced it,
simulates what each junction puts into memory, and raises an **error** when a junction asks
for a type nothing has produced (`CHAIN001`) or when the train's return type is never
produced (`CHAIN002`).

Both are `DiagnosticSeverity.Error`, not warnings. The build stops.

## Status

**Accepted.**

## Considered options

**Validate at run time, when the train is first executed.** This is what the memory
dictionary would do anyway: a missing type is a lookup that finds nothing. It was rejected
because the failure lands at the worst moment. A chain is wired in a constructor and
executed somewhere else entirely, often behind a scheduler or a GraphQL resolver, so the
exception surfaces far from the line that caused it and only on the code path that runs.

**Validate at registration**, walking the chains once at startup. Better than run time, and
still worse than the compiler: it needs the host to boot, it cannot say which line is wrong,
and a train nobody registered is never checked at all.

**Warning instead of error.** Rejected because there is no legitimate way to continue. A
junction whose input is not in memory cannot run; allowing the build to proceed only moves
the failure back to run time, which is the option already rejected.

## Consequences

**The analyzer is part of the package's contract.** It is packed to
`analyzers/dotnet/cs`, so every consumer gets it by referencing `Trax.Core`. A false
positive is a broken build for someone else, which is why the analyzer only reports when it
can see the whole chain and stays silent when it cannot.

**It only sees what it can read syntactically**, and it bails out rather than guessing. The
chain must terminate in `Resolve()` and start with `Activate()`, and any symbol it cannot
resolve stops the analysis silently. A chain assembled dynamically, or across a method
boundary, is invisible to it, and those cases fall back to the runtime behaviour the
analyzer exists to avoid.

## Exemplars

- `TrainChainAnalyzerTests` drives the analyzer over source fixtures and asserts the two
  diagnostics fire where expected and stay quiet where the chain is sound.
- [Analyzer](/docs/core/analyzer) documents the two diagnostics for users.

Not covered: nothing asserts the analyzer is actually packed into the NuGet output at the
right path. A packaging regression would ship a package whose analyzer never loads, and
every consumer's build would go quiet rather than red.

## Changelog

- **2026-09-11**: Corrected the mechanism: the analyzer triggers on Resolve() and walks the
  chain back, rather than reading constructor Chain() calls.
- **2026-09-11**: Recorded.
