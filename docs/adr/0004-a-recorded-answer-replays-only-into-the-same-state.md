---
authors: [Theauxm]
areas: [platform]
status: accepted
---

# A recorded answer replays only into the same state

A run that repeats an earlier one (a requeue, and an automatic retry) replays the earlier run's
answers instead of asking its deciders again (`Trax.Docs/adr/0041`). Trax.Core replays an answer
only when the state the question is asked about now hashes exactly as the state the answer was
given about did. Otherwise, including when either hash is missing, the decider is asked afresh.

## Status

**Accepted.** Narrows the matching rule of `Trax.Docs/adr/0041`, which matched an answer to its
asking by question key, occurrence and fingerprint, and never by the state's value.

## Why this is written down

Because matching on the question alone looks sufficient and is not. A repeated run runs every
step again, so the state a question is about is read afresh: the data may have changed while the
run waited to be retried, and a loop may meet its items in another order, so its second asking is
about a different item. An answer given about one state and acted on for another is a decision
nobody made, and a repeated run is usually unattended. Approving a refund of 20 must not approve
one of 2000.

## Considered options

**Match on question, occurrence and fingerprint only.** What 0041 shipped. Rejected for the
reason above.

**Each replay implementation compares the state.** Rejected: every journal would have to get it
right, and one that forgot would replay silently. The hash is computed and compared in
Trax.Core, so a journal only stores `DecisionMade.StateHash` and returns it as
`RecordedAnswer.StateHash`, and every replay inherits the check.

**Store the state, not its hash.** Rejected: the journal would hold a second copy of the run's
data, and comparing needs equality on arbitrary types.

**Hash the JSON the state is written as.** What this ADR first shipped, and rejected because it
can match two different states. JSON leaves out a tuple's items, public fields, `[JsonIgnore]`
and non-public members, and the members of a derived type held where its base type or an
interface is declared, while an in-process decider reads all of them. A missed difference is a
false match, the one failure this rule exists to prevent. The hash now walks every instance field
of each value's runtime type, public or not, base type first and by name, writing each value's
runtime type and an unambiguous encoding of its primitives, and a framework collection as its
elements in order.

**Replay an answer recorded without a hash.** Rejected: an answer recorded before this rule
cannot be shown to be about the same state, and the safe reading of "cannot tell" is "ask".

## Consequences

A state whose fields differ from run to run although nothing that matters changed (a timestamp,
a cache, a dictionary filled in another order, a generated id, a private counter) is asked afresh
on every repeat, so it never replays. That is the cost of failing closed, and it falls on the
deciders, not on the run's correctness.

A state the hash cannot read the same way every time has no hash and never replays, and that is
never the run's failure: one holding a reference cycle, a delegate (an ORM's lazy-loading proxy,
say), a pointer or native handle, a type, member or assembly, a stream, wait handle, task or
thread, a field whose read throws, or one nested deeper than 64 levels or larger than the hash's
size caps.

Only the hash leaves the run. A state with few possible values can be recovered from its hash by
trying each, but only by someone who can read the journal, which already holds the run's input.

A refused replay is reported, not hidden: `DecisionMade.ReplayRefused` says the answer was given
about a different state, was recorded without a hash, or could not be compared.

## Exemplars

- `StateDigestTests` pins that states differing only where JSON would not look (a tuple, a public
  field, a derived type held as its base or an interface, a `[JsonIgnore]` property, a private
  field) hash differently, that equal states built separately hash alike, and that a cycle, a
  delegate, a type or a stream, and nesting past the limit give no hash.
- `DecisionRuntimeTests` pins the same shapes through a replay; that the same state replays for
  `Decide`, `Switch`, `Gate` and `Scale`; that a different state, a loop whose items come back in
  another order, an answer recorded without a hash and a state that cannot be hashed are all
  asked afresh with the reason in `ReplayRefused`; that `StateHash` is a hash, never the state;
  and that `QuestionType` is set on decisions and refusals.
- [Decisions](/docs/core/decisions) is the rule this produces.

Not covered: nothing checks that a
host's journal stores and returns the hash. One that drops it
is safe (every answer is asked afresh) but never replays.

## Changelog

- **2026-10-02**: The hash walks every field of the state's runtime types instead of hashing its
  JSON, which matched states that differed in what JSON leaves out; the fail-closed cases are
  listed.
- **2026-10-02**: Recorded.
