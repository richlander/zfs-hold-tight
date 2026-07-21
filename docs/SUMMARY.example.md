<!--
Illustrative example only -- these are not real dataset names or numbers. This is what
`zfs-hold-tight --summary` (or `--summary | push-summary-git.sh`, see docs/TRUENAS.md)
actually prints: a two-section markdown report, safe to paste into a gist/PR/chat or push
to a dedicated log repo for a quick daily look without SSHing into the backup machine.
-->
## Snapshots

| Dataset | Monthlies | Weeklies | Dailies | Start | End | Continuity |
|---|---|---|---|---|---|---|
| archive/appdata | 3 | 8 | 6 | 2026-05-24 | 2026-07-20 | ✓ |
| archive/home/alice | 3 | 8 | 6 | 2026-05-24 | 2026-07-20 | ✓ |
| archive/home/bob | 3 | 8 | 6 | 2026-05-24 | 2026-07-20 | ✓ |
| archive/home/carol | 3 | 8 | 6 | 2026-05-24 | 2026-07-20 | ✓ |
| archive/home/dave | 3 | 8 | 6 | 2026-05-24 | 2026-07-20 | ✓ |
| archive/home/eve | 3 | 8 | 6 | 2026-05-24 | 2026-07-20 | ✓ |
| archive/scratch | 3 | 8 | 6 | 2026-05-24 | 2026-07-20 | ✓ |
| archive/media/family | 2 | 6 | 6 | 2026-06-14 | 2026-07-20 | ✓ |
| archive/media/movies | 2 | 6 | 6 | 2026-06-14 | 2026-07-20 | ✓ |
| archive/media/music | 2 | 6 | 6 | 2026-06-14 | 2026-07-20 | ✓ |
| archive/media/shows | 2 | 6 | 6 | 2026-06-14 | 2026-07-20 | ✓ |
| archive/memories | 3 | 8 | 52 | 2026-05-19 | 2026-07-20 | ✓ |

## Health

archive: healthy

<!--
If the pool-wide latch is unhealthy, this section instead names the dataset and guard
that tripped it, e.g.:

archive: WORRY - archive/scratch: newest daily shrank 82% (18MiB referenced vs ~102MiB
baseline, > 33% drop) — possible mass deletion on source (run `--reset-health` after
review)

See docs/SAFETY.md guard 20 and docs/DESIGN.md "Cross-dataset shock" for what this means
and why it doesn't clear itself.
-->
