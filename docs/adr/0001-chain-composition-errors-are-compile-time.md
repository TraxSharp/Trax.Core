---
authors: [Theauxm]
areas: [platform]
status: accepted
---

# A mis-composed chain does not compile

A Roslyn analyzer ships as the separate, opt-in `Trax.Core.Analyzers` package. It triggers
on a `Resolve()` call, walks back through the fluent chain that produced it, simulates what
each junction puts into memory, and raises an **error** when a junction asks for a type
nothing has produced (`CHAIN001`) or when the train's return type is never produced
(`CHAIN002`).

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

**Opting in is the consumer's choice, and the default is off.** `Trax.Core` ships
`lib/net10.0/Trax.Core.dll` alone and takes no dependency on the analyzer, so a consumer
gets it only by adding `Trax.Core.Analyzers` themselves. That is deliberate: the analyzer
raises errors, and an error nobody asked for is a broken build in somebody else's
repository. Inside this solution it is propagated to every project by `Directory.Build.props`
as a `ProjectReference` with `OutputItemType="Analyzer"`, which is why it looks automatic
from in here and is not.

**A false positive costs a consumer their build**, which is why the analyzer reports only
when it can see the whole chain and stays silent when it cannot.

**It only sees what it can read syntactically**, and it bails out rather than guessing. The
chain must terminate in `Resolve()` and start with `Activate()`, and any symbol it cannot
resolve stops the analysis silently. A chain assembled dynamically, or across a method
boundary, is invisible to it, and those cases fall back to the runtime behaviour the
analyzer exists to avoid.

## Exemplars

- `TrainChainAnalyzerTests` drives the analyzer over source fixtures and asserts the two
  diagnostics fire where expected and stay quiet where the chain is sound.
- [Analyzer](/docs/core/analyzer) documents the two diagnostics for users.

Not covered: nothing asserts the analyzer is packed into the NuGet output at the right path,
or that `Trax.Core` continues **not** to depend on it. A packaging regression in either
direction is silent: one ships a package whose analyzer never loads, the other turns an
opt-in error source on for every consumer at once.

## Changelog

- **2026-09-11**: Corrected the mechanism: the analyzer triggers on Resolve() and walks the
  chain back, rather than reading constructor Chain() calls.
- **2026-09-11**: Corrected the packaging claim. The analyzer is a separate opt-in package;
  referencing Trax.Core does not bring it in, and the published setup page said it did.
- **2026-09-11**: Recorded.
