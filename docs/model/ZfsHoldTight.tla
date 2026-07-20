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
   tests/run-tests.sh T58/T4b/T59/T59b against the real code, not just this model. *)

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
    lastVerdict      \* "n/a" | "ok" | "worry" -- last guard verdict, for inspection

vars == << now, snaps, prunedThrough, minNewerHolds, dailyKeepDays, lastVerdict >>

TypeOK ==
    /\ now \in Nat
    /\ snaps \subseteq Nat
    /\ prunedThrough \in Int
    /\ minNewerHolds \in MinNewerHoldsValues
    /\ dailyKeepDays \in DailyKeepDaysValues
    /\ lastVerdict \in {"n/a", "ok", "worry"}

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

\* A new daily snapshot arrives (replication happened normally).
AdvanceDay ==
    /\ now < MaxDay
    /\ now' = now + 1
    /\ snaps' = snaps \union {now'}
    /\ UNCHANGED << prunedThrough, minNewerHolds, dailyKeepDays, lastVerdict >>

\* A day is missed (genuine replication gap) -- included so the model can also check
\* that a REAL gap still trips the guard, not just that legitimate pruning doesn't.
MissDay ==
    /\ now < MaxDay
    /\ now' = now + 1
    /\ UNCHANGED << snaps, prunedThrough, minNewerHolds, dailyKeepDays, lastVerdict >>

\* The operator changes policy -- the exact real-world event that triggered the
\* incident this model exists to prevent a recurrence of.
ChangePolicy ==
    /\ minNewerHolds' \in MinNewerHoldsValues
    /\ dailyKeepDays' \in DailyKeepDaysValues
    /\ UNCHANGED << now, snaps, prunedThrough, lastVerdict >>

\* A --prune run: computes the guard verdict from the PERSISTED watermark (never from
\* policy) and, only on a healthy verdict, destroys the doomed set AND signs off
\* through today. A WORRY run changes nothing (protection only ever increases).
Prune ==
    LET healthy == FeedHealthy(snaps, prunedThrough, now, GuardRecentWindowDays)
        doomed  == Doomed(snaps, now, minNewerHolds, dailyKeepDays)
    IN /\ lastVerdict'    = IF healthy THEN "ok" ELSE "worry"
       /\ snaps'          = IF healthy THEN snaps \ doomed ELSE snaps
       /\ prunedThrough'  = IF healthy THEN now ELSE prunedThrough
       /\ UNCHANGED << now, minNewerHolds, dailyKeepDays >>

Next == AdvanceDay \/ MissDay \/ ChangePolicy \/ Prune

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

====
