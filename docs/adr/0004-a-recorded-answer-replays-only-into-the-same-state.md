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
data, and comparing needs equality on arbitrary types. A hash of the JSON the state is written as
(`JsonSerializerDefaults.Web`, what a decider that sends the state on sees) needs neither.

**Replay an answer recorded without a hash.** Rejected: an answer recorded before this rule
cannot be shown to be about the same state, and the safe reading of "cannot tell" is "ask".

## Consequences

A state whose JSON differs from run to run although nothing that matters changed (a timestamp,
a dictionary filled in another order, a generated id) is asked afresh on every repeat, so it
never replays. That is the cost of failing closed, and it falls on the deciders, not on the
run's correctness. A state that cannot be written as JSON has no hash and never replays, and
that is never the run's failure.

Only the hash leaves the run. A state with few possible values can be recovered from its hash by
trying each, but only by someone who can read the journal, which already holds the run's input.

A refused replay is reported, not hidden: `DecisionMade.ReplayRefused` says the answer was given
about a different state, was recorded without a hash, or could not be compared.

## Exemplars

- `DecisionRuntimeTests` pins that the same state replays for `Decide`, `Switch`, `Gate` and
  `Scale`; that a different state, a loop whose items come back in another order, an answer
  recorded without a hash and a state that cannot be written as JSON are all asked afresh with
  the reason in `ReplayRefused`; and that `StateHash` is a hash, never the state.
- [Decisions](/docs/core/decisions) is the rule this produces.

Not covered: nothing checks that a host's journal stores and returns the hash. One that drops it
is safe (every answer is asked afresh) but never replays.

## Changelog

- **2026-10-02**: Recorded.
