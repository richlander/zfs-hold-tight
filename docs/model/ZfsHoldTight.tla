---- MODULE ZfsHoldTight ----
(* First-draft model of the retention/guard coupling bug class described in
   docs/MODEL.md: raising minNewerHolds (or dailyKeepDays) retroactively made guard 1
   flag already-legitimately-pruned history as a gap, because it re-derived "was this
   gap legitimate" from *today's* policy instead of consulting a persisted record of
   what this tool had actually already decided.

   Scope: days are abstract integers, not real calendar dates. Week/month grouping
   below is simplified periodic arithmetic (WeekOf/MonthOf), not ISO-8601 semantics --
   calendar edge cases are irrelevant to this bug class, which is a pure
   state-machine/coupling problem: "does re-deriving legitimacy from current policy
   ever disagree with a persisted record of past decisions." Keeper expiry via
   weeklyKeepDays/monthlyKeepDays is NOT modeled yet (needs tracking when a day became
   a keeper, not just whether it currently is one) -- left for a second iteration.

   Parsed with SANY and model-checked with TLC against ZfsHoldTight.cfg: zero
   violations across 51.4M distinct states (see docs/MODEL.md Status section for the
   full run parameters and result). Implemented in zfs-hold-tight.cs and covered by
   tests/run-tests.sh T58/T4b/T59/T59b against the real code, not just this model.

   Extended with the cross-dataset health latch (docs/MODEL.md "Cross-dataset shock"
   section): `health` is deliberately modeled as a variable shared by the whole pool,
   not scoped to the one dataset the rest of this model already tracks in detail --
   `Shock` is an exogenous, nondeterministic action standing in for "guard 4/5 tripped
   on SOME dataset" (which one, and the shrink/churn byte-threshold arithmetic itself,
   is a mundane numeric check, not the bug-prone part; the latch's behavior is). *)

EXTENDS Integers, FiniteSets, TLC

CONSTANTS
    MaxDay,               \* bound the model so state space is finite
    WeekLen,              \* abstract "week" length, e.g. 7
    MonthLen,             \* abstract "month" length, e.g. 30
    MinNewerHoldsValues,  \* policy values to explore, e.g. {1, 2, 3, 24}
    DailyKeepDaysValues,  \* policy values to explore, e.g. {1, 7, 30, 180}
    GuardRecentWindowDays

ASSUME /\ MaxDay \in Nat
       /\ WeekLen \in Nat \ {0}
       /\ MonthLen \in Nat \ {0}
       /\ GuardRecentWindowDays \in Nat

WeekOf(d)  == d \div WeekLen
MonthOf(d) == d \div MonthLen

\* The unique max of a nonempty finite set of integers.
MaxOfSet(S) == CHOOSE d \in S : \A e \in S : e <= d

\* The n-th largest (1-indexed) element of a finite set of integers: d qualifies iff
\* exactly (n-1) elements of S are strictly greater than it.
NthLargest(S, n) == CHOOSE d \in S : Cardinality({e \in S : e > d}) = n - 1

VARIABLES
    now,             \* current day
    snaps,           \* set of days that currently have a live snapshot
    prunedThrough,   \* persisted watermark: everything <= this was already decided
    minNewerHolds,   \* current policy value -- CAN change mid-run, that's the point
    dailyKeepDays,   \* current policy value -- CAN change mid-run
    lastVerdict,     \* "n/a" | "ok" | "worry" -- last guard verdict, for inspection
    health           \* "starting" | "healthy" | "unhealthy" -- pool-wide, not per-dataset

vars == << now, snaps, prunedThrough, minNewerHolds, dailyKeepDays, lastVerdict, health >>

TypeOK ==
    /\ now \in Nat
    /\ snaps \subseteq Nat
    /\ prunedThrough \in Int
    /\ minNewerHolds \in MinNewerHoldsValues
    /\ dailyKeepDays \in DailyKeepDaysValues
    /\ lastVerdict \in {"n/a", "ok", "worry"}
    /\ health \in {"starting", "healthy", "unhealthy"}

(* ---- Question 1: is it safe to delete this snapshot right now? -------------------
   A pure function of the CURRENT snaps and CURRENT policy. Re-deriving this fresh
   every run is correct, not a bug -- that's what "protection only ever increases" and
   retention tuning both depend on. *)

\* Newest day per period, among periods that have fully elapsed as of `today` (an ISO
\* week's keeper is only fixed once the week is over -- mirrored here abstractly).
KeepersOf(S, today) ==
    LET ClosedWeeks  == { WeekOf(d)  : d \in S } \ { WeekOf(today) }
        ClosedMonths == { MonthOf(d) : d \in S } \ { MonthOf(today) }
        WeekKeeper(w)  == MaxOfSet({ d \in S : WeekOf(d) = w })
        MonthKeeper(m) == MaxOfSet({ d \in S : MonthOf(d) = m })
    IN { WeekKeeper(w) : w \in ClosedWeeks } \union { MonthKeeper(m) : m \in ClosedMonths }

\* The n-th newest keeper's day, or -1 if fewer than n keepers exist yet (population
\* floor -- mirrors guard 8).
Frontier(S, today, n) ==
    LET K == KeepersOf(S, today)
    IN IF Cardinality(K) < n THEN -1 ELSE NthLargest(K, n)

\* Everything this run would destroy, given the CURRENT snaps and CURRENT policy.
Doomed(S, today, n, floorDays) ==
    LET K == KeepersOf(S, today)
        F == Frontier(S, today, n)
    IN { d \in S : /\ d \notin K
                   /\ d < F
                   /\ today > d + floorDays }

(* ---- Question 2: does the observed history look healthy? -------------------------
   Deliberately does NOT take Policy as a parameter. That absence is the fix: there is
   nothing left in this signature to accidentally recouple to a policy change. *)

FeedHealthy(S, watermark, today, windowDays) ==
    \A d \in (today - windowDays)..today : d \in S \/ d <= watermark

(* ---- Actions ---- *)

Init ==
    /\ now = 0
    /\ snaps = {0}
    /\ prunedThrough = -1
    /\ minNewerHolds \in MinNewerHoldsValues
    /\ dailyKeepDays \in DailyKeepDaysValues
    /\ lastVerdict = "n/a"
    /\ health = "starting"

\* A new daily snapshot arrives (replication happened normally).
AdvanceDay ==
    /\ now < MaxDay
    /\ now' = now + 1
    /\ snaps' = snaps \union {now'}
    /\ UNCHANGED << prunedThrough, minNewerHolds, dailyKeepDays, lastVerdict, health >>

\* A day is missed (genuine replication gap) -- included so the model can also check
\* that a REAL gap still trips the guard, not just that legitimate pruning doesn't.
MissDay ==
    /\ now < MaxDay
    /\ now' = now + 1
    /\ UNCHANGED << snaps, prunedThrough, minNewerHolds, dailyKeepDays, lastVerdict, health >>

\* The operator changes policy -- the exact real-world event that triggered the
\* incident this model exists to prevent a recurrence of.
ChangePolicy ==
    /\ minNewerHolds' \in MinNewerHoldsValues
    /\ dailyKeepDays' \in DailyKeepDaysValues
    /\ UNCHANGED << now, snaps, prunedThrough, lastVerdict, health >>

\* Content shock (guard 4/5) detected on SOME dataset -- see the header comment on why
\* this is modeled as an exogenous event rather than derived from this one dataset's
\* own snaps. Can fire from "starting" or "healthy"; a no-op if already "unhealthy"
\* (the latch doesn't need re-tripping, and re-tripping must never look like a fresh
\* event that could confuse "when did this start").
Shock ==
    /\ health # "unhealthy"
    /\ health' = "unhealthy"
    /\ UNCHANGED << now, snaps, prunedThrough, minNewerHolds, dailyKeepDays, lastVerdict >>

\* The ONLY transition back to healthy: an explicit, deliberate operator action
\* (--reset-health in the real tool). Never fires automatically, never bundled with
\* Prune or any other action -- see NoAutoRecoveryStep below.
ResetHealth ==
    /\ health = "unhealthy"
    /\ health' = "healthy"
    /\ UNCHANGED << now, snaps, prunedThrough, minNewerHolds, dailyKeepDays, lastVerdict >>

\* A --prune run: computes the guard verdict from the PERSISTED watermark (never from
\* policy). Holds/tags would still be refreshed in the real tool even while frozen
\* ("protection only ever increases"), but destroying anything and advancing
\* prunedThrough both require mayMutate -- healthy AND not latched unhealthy. A global
\* freeze must block mutation even on a run where THIS dataset's own feed looks
\* perfectly clean; that's the whole point of the cross-dataset invariant. The first
\* ever clean run also transitions health from "starting" to "healthy" -- it never
\* transitions FROM "unhealthy": that's ResetHealth's job alone.
Prune ==
    LET healthy   == FeedHealthy(snaps, prunedThrough, now, GuardRecentWindowDays)
        doomed    == Doomed(snaps, now, minNewerHolds, dailyKeepDays)
        mayMutate == healthy /\ health # "unhealthy"
    IN /\ lastVerdict'    = IF healthy THEN "ok" ELSE "worry"
       /\ snaps'          = IF mayMutate THEN snaps \ doomed ELSE snaps
       /\ prunedThrough'  = IF mayMutate THEN now ELSE prunedThrough
       /\ health'         = IF health = "starting" /\ mayMutate THEN "healthy" ELSE health
       /\ UNCHANGED << now, minNewerHolds, dailyKeepDays >>

Next == AdvanceDay \/ MissDay \/ ChangePolicy \/ Prune \/ Shock \/ ResetHealth

Spec == Init /\ [][Next]_vars

(* ---- Invariants ---- *)

\* #4: watermark monotonicity, checked as a step property (TLC "PROPERTY", not
\* "INVARIANT" -- see ZfsHoldTight.cfg).
WatermarkMonotoneStep == [][prunedThrough' >= prunedThrough]_vars

\* #2, policy independence: true BY CONSTRUCTION here (FeedHealthy's signature above
\* takes no Policy parameter) -- not something TLC needs to search for. Kept as a
\* comment, not a checkable formula: there's nothing to state that isn't already
\* syntactically obvious from FeedHealthy's definition. The design goal is to make the
\* original bug *inexpressible*, not merely untested.

\* #3: the flip side -- a genuine, unexcused gap must still trip the guard. Rules out
\* a degenerate "fix" that satisfies policy-independence by making FeedHealthy always
\* TRUE.
NoGenuineGapGoesUnnoticed ==
    (\E d \in (now - GuardRecentWindowDays)..now : d \notin snaps /\ d > prunedThrough)
        => ~FeedHealthy(snaps, prunedThrough, now, GuardRecentWindowDays)

\* Sanity bound, not a real safety property yet: the watermark can never claim to have
\* certified days that haven't happened.
WatermarkNeverAheadOfNow == prunedThrough <= now

\* #1, Safety -- the one worth TLC's exhaustive search, across arbitrary
\* AdvanceDay/MissDay/ChangePolicy/Prune sequences including ones no one would
\* hand-write as a test fixture. Only the "never destroy the replication base" piece is
\* modeled so far; keeper-expiry-within-retention needs each keeper's own establishment
\* date tracked (not just current membership in KeepersOf), left as a TODO.
\*
\* Checked as a STEP property (TLC "PROPERTY", not "INVARIANT"): the newest snapshot
\* that actually EXISTS is never removed by a step. NOT "now \in snaps" -- MissDay
\* legitimately makes that false (a genuine replication gap, modeled on purpose), which
\* is a bug in the invariant's phrasing, not in Doomed: caught by TLC on the very first
\* run, exactly the kind of mistake this exercise exists to catch.
BaseNeverDestroyedStep == [][ (snaps # {}) => (MaxOfSet(snaps) \in snaps') ]_vars

\* ---- Cross-dataset health latch ----

\* The core property the latch exists to guarantee: while health is unhealthy BEFORE a
\* step and STILL unhealthy AFTER it (i.e. that step wasn't the ResetHealth that clears
\* it), no PRUNE ACTION may mutate anything -- no destroy, no prunedThrough advance --
\* no matter how healthy THIS dataset's own feed looks, while the pool-wide latch is
\* tripped. Deliberately NOT "snaps unchanged": AdvanceDay (replication delivering a new
\* daily) is an EXOGENOUS event this tool doesn't control and shouldn't try to block --
\* an earlier version of this property said snaps' = snaps and TLC immediately found the
\* Shock-then-AdvanceDay counterexample, which is legitimate, expected behavior, not a
\* bug: only DESTROY (snaps shrinking) and a prunedThrough advance are actually
\* forbidden. Exhaustively checked across every interleaving of Shock/ResetHealth with
\* AdvanceDay/MissDay/ChangePolicy/Prune, including sequences no one would hand-write.
FrozenWhileUnhealthyStep ==
    [][ (health = "unhealthy" /\ health' = "unhealthy") =>
            (snaps \subseteq snaps' /\ prunedThrough' = prunedThrough) ]_vars

\* No action other than the explicit ResetHealth ever clears the latch -- true by
\* construction (Prune's health' formula only ever touches the "starting" case; Shock
\* only ever sets "unhealthy"), same as PolicyIndependence above: the design goal is
\* that no OTHER action even has a code path that could clear it, not merely that none
\* currently does. Checked anyway as a cheap regression guard against a future edit
\* accidentally adding one.
NoAutoRecoveryStep == [][ (health = "unhealthy" /\ health' # "unhealthy") => (health' = "healthy") ]_vars

====
