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

**The bootstrap case, found the hard way.** A dataset that was already established
under a binary that predates this property has no `pruned_through` at all on its first
run under a new one — defaulting that to `MinValue` (nothing trusted yet) would flag
its entire pre-existing, already-legitimate daily tail as an unexplained gap. This is
not hypothetical: it happened deploying this exact feature. The fix reuses the
cold-start floor's own `everEstablished` signal (real `<ns>:tier` history already
exists): an established dataset with no recorded watermark defaults to `now` instead of
`MinValue`, once, self-limiting (a real run persists a value immediately afterward, so
this branch never applies again), and only ever extends trust to a dataset this tool
was already successfully managing — never to a genuinely fresh one, which still gets
`MinValue`.

Worth naming plainly: on that one bootstrap run, defaulting to `now` doesn't just excuse
the *historical* gap — every day up to and including the newest snapshot is `<= now`,
so guard 1's entire recent-window check is effectively a no-op for that single run (a
brand-new gap appearing to coincide exactly with the bootstrap moment would also go
unflagged by guard 1 specifically). Guards 2/4/5 (staleness, shrink, churn) don't depend
on `pruned_through` at all and still catch anything currently wrong regardless. Judged
an acceptable, narrow, one-time trade-off given the alternative — a real, repeated false
alarm on every established dataset on this session's own deployment.

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

## Cross-dataset shock

Everything above is about a *single* dataset's own timeline. That's not the whole
threat model. The invariant to protect is different in kind:

- One or more datasets shows a **snapshot shock** — guard 4 or 5 tripping (e.g. 50% of
  files disappear or change in one daily).
- The response must be: **no snapshots across ANY dataset are pruned, and no
  `pruned_through` anywhere advances** — not just on the dataset that tripped.

**Why per-dataset isolation is the wrong default here.** The tool's existing operational
guarantee (`docs/SAFETY.md` #17, tested by the old T18) was "a guard tripping on one
dataset HOLDs only that dataset; healthy datasets still prune." That's the right call for
guards 1-3 (a feed gap or staleness on one dataset says little about the others), but
wrong for guards 4-5: if ransomware is on the source machine, it plausibly touched every
dataset, not just the one loud enough — or configured with a tight enough threshold — to
trip its own guard. The others may already be compromised too, just under threshold, or
on a policy that relaxed the churn guard (e.g. `volatile`, which disables it entirely).
Trusting them to keep pruning normally because *their own* number didn't cross a line is
exactly the reasoning this tool exists to distrust.

**The response also can't self-heal.** "Self-functioning, but a shock requires a reset"
means: once flagged, the tool must NOT resume automatically just because a later run's
own numbers look clean again — that's not evidence of safety, it could just mean the
attacker moved on to slower, quieter changes. This needs a **latch**, not a live
recheck: a state that only a human, having actually reviewed the incident, can clear.

**Design, borrowing Docker's health-check vocabulary (not its auto-recovery
behavior):** a new, fleet-wide (not per-dataset) `<ns>:health` property, stored on the
pool/prefix parent dataset (e.g. `archive`), with three values:

- `starting` (property never set — no verdict established yet)
- `healthy` (normal)
- `unhealthy` (latched — frozen)

Unlike Docker's own `unhealthy`, which clears automatically the moment its check passes
again, this one **only** clears via an explicit `--reset-health` command — never via
`--force` (which already means something unrelated: bypass the routine volume cap, not
"I reviewed a possible mass-deletion/encryption event"), and never automatically.

Mechanically: every `--prune`/dry-run first does a pre-pass across **every** configured
dataset (ignoring `--dataset` — a filtered run must not blind the fleet-wide latch to a
shock on a dataset it isn't touching this run), checking guards 4/5 in isolation. If any
dataset shows shock, or the latch is already `unhealthy`, or the health property itself
can't be read (fails toward frozen, not toward assuming it's fine), every dataset this
run holds its keepers defensively and skips destroy + the `pruned_through` advance — the
dataset that actually tripped its own guard still gets its specific reason and
`<ns>:worry` tag; the others fall back to the generic fleet message.

This is modeled as an extension to `docs/model/ZfsHoldTight.tla`: `health` is a variable
shared by the whole fleet (not scoped to the one dataset the rest of the model already
tracks in detail); `Shock` is an exogenous, nondeterministic action standing in for
"guard 4/5 tripped on SOME dataset" (which one, and the actual shrink/churn byte-percent
arithmetic, is a mundane numeric threshold check, not the bug-prone part — the latch's
behavior is); `ResetHealth` is the only transition back. The property that matters:
`FrozenWhileUnhealthyStep` — while `health` is unhealthy before AND after a step, no
`Prune` action may destroy anything or advance `prunedThrough`, no matter how clean that
dataset's own feed looks. An early version of this property said "`snaps` must be
completely unchanged," and TLC immediately found a `Shock`-then-`AdvanceDay`
counterexample — correctly: replication delivering a new daily is an exogenous event
this tool doesn't control and shouldn't try to block. Only destroy (snaps *shrinking*)
and a `pruned_through` advance are actually forbidden; fixed to say exactly that.

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

**Cross-dataset shock latch: modeled, validated, implemented.** The `health`/`Shock`/
`ResetHealth` extension parsed clean with SANY; TLC model-checked it exhaustively at the
same bounds plus the two new actions — 1,919,526,996 states generated, 128,050,380
distinct states found, search depth 25, zero violations of `FrozenWhileUnhealthyStep` or
`NoAutoRecoveryStep` (fingerprint-collision estimate ~1.2% at that scale — noted
honestly, not claimed as 100% exhaustive). Implemented as `<ns>:health` on the pool
dataset, a pre-pass in `zfs-hold-tight.cs` that checks guards 4/5 across every configured
dataset before any dataset commits to anything, and a new `--reset-health` command that
is the only way to clear it. `tests/run-tests.sh`: T18 (rewritten — a shock on one
dataset now freezes the entire fleet, reversing what it used to check), T60 (the latch
does not auto-heal across a second, individually-clean run), T61 (`--reset-health`
clears it and a subsequent prune proceeds normally), T62 (`--reset-health` is a safe
no-op when not latched), T63 (`--force` does not clear it), T64 (`--dataset` filtering
doesn't blind the pre-pass to a shock on a different dataset), T65 (a clean first run
explicitly writes `health=healthy`, not left implicit).
