# A real model for the retention/guard interaction

## Why this exists

Guard 1 ("recent window complete") has needed a correctness patch twice now, for the
same underlying reason each time:

1. Originally, a naive version of guard 1 flagged the tool's own routine pruning as a
   feed gap — fixed by excusing any missing day that's covered by the current hold
   frontier (documented in `docs/SAFETY.md` guard 1 as "a real, once-shipped bug").
2. This session: raising `minNewerHolds` (a legitimate, desirable retention change)
   retroactively made that same exclusion logic flag *already-correctly-pruned*
   history as a gap, because the exclusion re-derives "was this legitimate?" from
   *today's* policy, and today's policy differs from whatever was in effect when the
   pruning actually happened.

Patching the exclusion logic a third time would repeat the pattern instead of fixing
it. The fix is to stop conflating two genuinely different questions that happen to
share one computation today.

## The two questions

**1. Is it safe to delete this snapshot, right now, under our current policy?**

This is the retention decision — the two-holds invariant, the `dailyKeepDays` floor,
keeper expiry. It is *correct*, not a bug, for this to be freely re-evaluated against
whatever the policy is *today* — that's the entire point of "protection only ever
increases" and of retention tuning meaning anything at all. This question is answered
by a **pure function of the current snapshot list and the current policy**. Nothing
about it needs history.

**2. Are there missing snapshots that suggest something is wrong?**

This is feed/guard health. It must **not** be answered by re-deriving legitimacy from
today's policy, because legitimacy of a *past* decision was determined under whatever
policy was in effect *then* — and the tool has no way to reconstruct that after the
fact by recomputing today's frontier. It needs to consult an authoritative record of
decisions this tool has actually already made, not re-litigate history under new
rules.

Today, both questions are answered by the same code path (guard 1 calls
`ComputeKeepersAndFrontier` — the retention decision's own machinery — to decide what
to excuse). That coupling is the root cause: every time the retention policy changes,
question 2's answer changes even though nothing about the actual health of the feed
changed.

## The fix: a persisted watermark

Introduce a new piece of state that doesn't exist in the tool today: a per-dataset,
monotonically-increasing watermark — e.g. a `<ns>:pruned_through` property on the
**dataset** itself (not a snapshot) — recording *"as of the last successful,
non-`WORRY` `--prune`, everything up to and including this date was already correctly
evaluated by this tool."*

- Advanced only by a successful `--prune`: on a healthy run, `pruned_through' = now`.
  On a `WORRY` run, unchanged (we didn't trust that run's evaluation, so we can't sign
  off on it).
- Never re-derived, never moved backward, never adjusted to match a policy change.
- Guard 1's exclusion becomes: a missing day is excused iff `day <= pruned_through` —
  a fact about history, not a recomputation against current config.

This fully decouples question 2 from question 1. Deepening retention (raising
`minNewerHolds`, extending `dailyKeepDays`) can no longer reopen a settled question
about days that were already correctly handled — there's nothing left in guard 1 that
references policy at all.

## State model

- `Snapshots` — the set of `(date, tier)` pairs that currently exist for a dataset, as
  read from `zfs list` (`tier ∈ {none, weekly, monthly}`).
- `Policy` — `(minNewerHolds, dailyKeepDays, weeklyKeepDays, monthlyKeepDays)`. Can
  change over time — the model treats a policy change as a first-class event, not an
  edge case, since that's exactly what triggered this incident.
- `PrunedThrough` — the new persisted watermark described above.
- `Now` — the current day.

## RetentionDecision(Snapshots, Policy, Now) — question 1, restated formally

Pins down what `ComputeKeepersAndFrontier` + the destroy loop in `zfs-hold-tight.cs`
already do, so the invariants below can be stated unambiguously against it:

- A snapshot is a **keeper** if it's the newest-dated in its ISO-week or calendar-month
  group among `Snapshots`.
- `Frontier` = the date of the `minNewerHolds`-th newest keeper (or "none yet" if fewer
  exist).
- A non-keeper snapshot `s` is **doomed** iff `s.date < Frontier` **and**
  `Now > s.date + Policy.dailyKeepDays`.
- A keeper ages out (also doomed) once `Now` exceeds its own weekly/monthly retention
  window.
- Otherwise: kept.

## FeedHealthy(Snapshots, PrunedThrough, Now, GuardRecentWindowDays) — question 2

- For each day `d` in the last `GuardRecentWindowDays` days ending at `Now`:
  - a snapshot dated `d` exists → fine.
  - else `d <= PrunedThrough` → fine (already-accounted-for by a past decision).
  - else → gap.

Notice `Policy` does not appear in this signature at all. That absence *is* the fix —
there's nothing left to accidentally recouple.

## Invariants

1. **Safety** — a keeper within its own retention window is never doomed; the newest
   daily (replication base) is never doomed; a foreign/defensive held snapshot is
   never doomed. (Existing per-snapshot protections list in `docs/SAFETY.md`, restated
   as a property to check exhaustively rather than reason about by hand.)
2. **Policy independence** — `FeedHealthy`'s verdict never depends on `Policy`. In the
   model below this holds *by construction* (the signature has no `Policy` parameter
   to depend on) — it isn't something to search for, it's a design constraint the
   types enforce. That's deliberate: the goal is to make the original bug class
   *inexpressible*, not merely absent from the cases we thought to test.
3. **No genuine gap goes unnoticed** — the flip side of #2: a real missing day (never
   replicated, not excused by `PrunedThrough`) must still trip the guard. A change that
   satisfied #2 by making `FeedHealthy` unconditionally `TRUE` would be worthless; this
   invariant rules that out.
4. **Watermark monotonicity** — `PrunedThrough` never decreases, and only ever advances
   to `Now` on a run that actually completed healthily — never adjusted retroactively,
   never advanced speculatively.

Where I'd actually point TLC's exhaustive search: not at #2 (true by construction), but
at #1 (Safety) across *arbitrary* sequences of day-advances and policy changes —
including sequences no one would hand-write as a test fixture (`minNewerHolds`
oscillating, `dailyKeepDays` dropping right after being raised, a policy change
mid-backlog). That's precisely the class of scenario that let both real incidents in
this history slip past 63 hand-written test cases.

## Status

**Modeled and validated.** `docs/model/ZfsHoldTight.tla` is a TLA+ encoding of the
above, scoped deliberately: days are abstract integers and week/month grouping is
simplified periodic arithmetic rather than real ISO-8601 semantics, because calendar
edge cases are irrelevant to *this* bug class (it's a pure state-machine/coupling
problem, not a calendar one). Parsed clean with SANY; TLC model-checked it exhaustively
(`ZfsHoldTight.cfg`) at `MaxDay=18`, `minNewerHolds ∈ {1,2,3,8}`,
`dailyKeepDays ∈ {0,2,6}` — 719,579,604 states generated, 51,442,416 distinct states
found, search depth 24, zero invariant/property violations, including every
interleaving of `ChangePolicy` with `Prune`/`MissDay` (the exact "raise `minNewerHolds`
after some history is already pruned" scenario). TLC's own fingerprint-collision
estimate at that scale was ~0.2% — worth naming honestly, not "100% exhaustive," but a
strong result. One real mistake in the model itself was caught and fixed in the
process: an initial `BaseNeverDestroyed == now \in snaps` invariant was wrong (a
genuine `MissDay` legitimately makes that false; the invariant needed to compare
against the newest snapshot that actually exists, not the day-counter) — exactly the
kind of error this exercise exists to catch, caught within seconds of the first run.
Keeper expiry via `weeklyKeepDays`/`monthlyKeepDays` is still not modeled (needs
tracking *when* a day became a keeper, not just whether it currently is one) — flagged
as the next iteration, not hidden.

**Implemented and tested against the real code.** `zfs-hold-tight.cs` now persists
`<ns>:pruned_through` on the dataset, advances it to `now` only on a run that clears
every guard (guards 1-5), and both guard 1 and `--summary`'s Continuity column read it
instead of re-deriving from the current retention policy. `tests/run-tests.sh` T58
reproduces the real incident directly against the implementation (not just the model):
a dataset with history already pruned under the old policy, re-evaluated under
`minNewerHolds ∈ {2, 3, 24}` (the actual production spread), never re-flags the
already-certified gap. T4b tests the watermark's boundary is exact (excused at the
watermark, not the day after); T59/T59b confirm the watermark only ever advances on a
run that was actually healthy.
