#!/usr/bin/env python3
"""Multi-run regression coverage for the Sep-Nov 2026 latched-tagging incident.
Uses fake-zfs only; never connects to a real pool or backup appliance.
"""
import datetime as dt
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
import uuid

ROOT = Path(__file__).resolve().parents[1]
BINARY = os.environ.get('ZFS_HOLD_TIGHT_TEST_BINARY')
ANCHOR = dt.datetime.now(dt.timezone.utc).replace(microsecond=0) - dt.timedelta(hours=2)
LAST_MONTH = ANCHOR.date().replace(day=1) - dt.timedelta(days=1)
START = LAST_MONTH.replace(day=1)
BEFORE = START.replace(day=23)


class LatchedTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.base = Path(self.tmp.name)
        self.fz = self.base / 'fz'
        self.fz.mkdir()
        for name in ['holds', 'ops.log', 'dsprops']: (self.fz / name).touch()
        self.cfg = self.base / 'config.json'
        self.cfg.write_text(json.dumps({
            'logDir': str(self.base / 'logs'), 'zfsPath': str(ROOT / 'tests/fake-zfs'),
            'guardMaxDeletePerRun': 10000, 'guardRecentWindowDays': 3,
            'guardMaxDailyAgeHours': 720, 'minNewerHolds': 2,
            'retentionPolicies': {'baseline': {'dailyKeepDays': 7, 'weeklyKeepDays': 1825, 'monthlyKeepDays': 1825}},
            'datasets': {'archive/home/rich': 'baseline'}}))
        self.feed(BEFORE)
        # Captured pre-incident state: a provisional monthly exists, but this
        # dataset has only reconciled metadata and has never completed a prune.
        snaps = self.snaps()
        old = self.name(BEFORE)
        expiry = dt.datetime.combine(BEFORE, ANCHOR.timetz()) + dt.timedelta(days=1825)
        snaps[old][1:3] = [str(int(expiry.timestamp())), 'monthly']
        (self.fz / 'snaps').write_text('\n'.join('\t'.join(f) for f in snaps.values()) + '\n')
        (self.fz / 'holds').write_text(old + '\tzht:monthly\t-\n')
        (self.fz / 'dsprops').write_text('archive/home/rich\tzht:established\tno\n')
        self.latch()

    def tearDown(self):
        self.tmp.cleanup()

    def name(self, date):
        return 'archive/home/rich@autosnap_' + date.isoformat() + '_' + ANCHOR.strftime('%H:%M:%S') + '_daily'

    def feed(self, end):
        previous = self.snaps() if (self.fz / 'snaps').exists() else {}
        lines = []
        day = START
        while day <= end:
            name = self.name(day)
            fields = previous.get(name, [name, '-', '-', '1000000', '0', '-'])
            lines.append('\t'.join(fields))
            day += dt.timedelta(days=1)
        (self.fz / 'snaps').write_text('\n'.join(lines) + '\n')

    def snaps(self):
        return {p[0]: p for p in (line.split('\t') for line in (self.fz / 'snaps').read_text().splitlines())}

    def props(self):
        return {(p[0], p[1]): p[2] for p in (line.split('\t', 2) for line in (self.fz / 'dsprops').read_text().splitlines())}

    def latch(self, reason='intentional worktree deletion'):
        props = self.props()
        prior = props.get(('archive','zht:latch_evidence'))
        if prior:
            evidence=json.loads(prior)
            # If the prior incident is acknowledged, this fixture represents a
            # genuinely NEW warning, with fresh exact evidence like BeginWarning.
            if ('archive','zht:false_positive_'+evidence['id']) in props:
                tip=max(self.snaps())
                fields=self.snaps()[tip]
                evidence={'id':uuid.uuid4().hex,'dataset':'archive/home/rich','snapshot':tip,
                          'reason':reason,'referenced':int(fields[3]),'written':int(fields[4])}
                props[('archive','zht:latch_evidence')]=json.dumps(evidence)
                props[('archive','zht:latch_id')]=evidence['id']
        elif ('archive','zht:latch_id') in props and ('archive','zht:false_positive_'+props[('archive','zht:latch_id')]) in props:
            tip=max(self.snaps()); fields=self.snaps()[tip]
            evidence={'id':uuid.uuid4().hex,'dataset':'archive/home/rich','snapshot':tip,
                      'reason':reason,'referenced':int(fields[3]),'written':int(fields[4])}
            props[('archive','zht:latch_evidence')]=json.dumps(evidence)
            props[('archive','zht:latch_id')]=evidence['id']
        props[('archive', 'zht:health')] = 'unhealthy'
        props[('archive', 'zht:health_reason')] = reason
        (self.fz / 'dsprops').write_text(''.join('\t'.join((*key, value)) + '\n' for key, value in props.items()))

    def run_tool(self, *args, expected=0, fail_property=None, fail=None, fail_release=None, fail_get=None, fail_after_receipt=None, fail_clear=False):
        env = dict(os.environ, FAKE_ZFS_DIR=str(self.fz), ZFS_HOLD_TIGHT_CONFIG=str(self.cfg))
        if fail_property: env['FAKE_ZFS_FAIL_PROPERTY'] = fail_property
        if fail: env['FAKE_ZFS_FAIL'] = fail
        if fail_release: env['FAKE_ZFS_FAIL_RELEASE_TARGET'] = fail_release
        if fail_get: env['FAKE_ZFS_FAIL_GET_PROPERTY'] = fail_get
        if fail_after_receipt: env['FAKE_ZFS_FAIL_AFTER_RECEIPT'] = fail_after_receipt
        if fail_clear: env['FAKE_ZFS_FAIL_HEALTH_VALUE'] = 'healthy'
        command = ([BINARY, '--no-hc', *args] if BINARY else
                   ['dotnet', 'run', str(ROOT / 'src/zfs-hold-tight.cs'), '--', '--no-hc', *args])
        out = subprocess.run(command,
                             cwd=ROOT, env=env, text=True, capture_output=True)
        if expected is not None:
            self.assertEqual(out.returncode, expected, out.stdout + out.stderr)
        return out.stdout

    def holds(self):
        return {(p[0], p[1]) for p in (line.split('\t') for line in (self.fz / 'holds').read_text().splitlines())}

    def reset_ops(self):
        (self.fz / 'ops.log').write_text('')

    def ops(self):
        return (self.fz / 'ops.log').read_text().splitlines()

    def test_monthly_moves_while_all_incident_snapshots_remain_protected(self):
        old = self.name(BEFORE)
        self.feed(ANCHOR.date())
        before = set(self.snaps())
        self.reset_ops()
        self.run_tool('--prune', '--force', expected=1)
        self.assertEqual(set(self.snaps()), before)
        self.assertFalse(any(x.startswith('destroy ') for x in self.ops()))
        self.assertFalse(any('zht:pruned_through=' in x for x in self.ops()))
        month_end = self.name(LAST_MONTH)
        self.assertEqual(self.snaps()[month_end][2], 'monthly')
        self.assertNotEqual(self.snaps()[old][2], 'monthly')
        self.assertNotIn((old, 'zht:monthly'), self.holds())
        self.assertTrue(any(n == old and t.startswith('zht:latched:') for n,t in self.holds()))
        for name, fields in self.snaps().items():
            self.assertTrue(any(n == name and t.startswith('zht:latched:') for n,t in self.holds()))
            self.assertEqual(len(json.loads(fields[5])), 1)
        # Stable identity, append-only history, and no repeat writes/releases.
        history = {n: f[5] for n, f in self.snaps().items()}
        self.reset_ops()
        self.run_tool('--prune', expected=1)
        self.assertFalse(any('zht:latch_history=' in x for x in self.ops()))
        self.assertEqual(history, {n: f[5] for n, f in self.snaps().items()})
        doctor = self.run_tool('--doctor')
        self.assertIn('0 stale managed holds, 0 stale tier records', doctor)
        self.assertIn('latch-held', doctor)
        self.assertIn('## Incident protection', self.run_tool('--summary'))

    def test_reset_reconcile_first_prune_and_new_incident_history(self):
        self.feed(ANCHOR.date())
        self.run_tool('--prune', expected=1)
        first_id = self.props()[('archive', 'zht:latch_id')]
        self.run_tool('--acknowledge-false-positive')
        self.assertEqual(first_id, self.props()[('archive', 'zht:latch_id')])
        self.assertIn(('archive','zht:false_positive_' + first_id), self.props())
        before = set(self.snaps())
        self.reset_ops()
        out = self.run_tool('--reconcile')
        self.assertIn('RECONCILED (no deletions)', out)
        self.assertEqual(set(self.snaps()), before)
        self.assertTrue(any(t.startswith('zht:latched:') for _, t in self.holds()))
        self.assertEqual(self.props()[('archive/home/rich', 'zht:established')], 'no')
        self.reset_ops()
        out = self.run_tool('--prune', expected=1)
        self.assertIn('cold start', out)
        self.assertFalse(any(x.startswith('destroy ') for x in self.ops()))
        self.reset_ops()
        self.run_tool('--prune', '--force')
        self.assertTrue(any(x.startswith('destroy ') for x in self.ops()))
        self.assertFalse(any(t.startswith('zht:latched:') for _, t in self.holds()))
        self.assertEqual(self.props()[('archive/home/rich', 'zht:established')], 'yes')
        self.assertTrue(all(json.loads(f[5])[0]['id'] == first_id for f in self.snaps().values()))
        self.assertNotIn('FAILED', self.run_tool('--prune'))
        self.run_tool('--doctor')
        reason = 'intentional deletion — "reviewed"'
        self.latch(reason)
        self.run_tool('--prune', expected=1)
        for f in self.snaps().values():
            history = json.loads(f[5])
            self.assertEqual(len(history), 2)
            self.assertEqual(history[0]['id'], first_id)
            self.assertNotEqual(history[1]['id'], first_id)
            self.assertEqual(history[1]['reason'], reason)

    def test_failed_history_write_is_protected_and_retryable(self):
        self.feed(ANCHOR.date())
        before = set(self.snaps())
        old_tier = self.snaps()[self.name(BEFORE)][2]
        self.reset_ops()
        self.run_tool('--prune', expected=1, fail_property='zht:latch_history')
        self.assertEqual(set(self.snaps()), before)
        self.assertEqual(self.snaps()[self.name(BEFORE)][2], old_tier)
        self.assertTrue(all(any(path == n and tag.startswith('zht:latched:') for path,tag in self.holds()) for n in before))
        self.assertFalse(any(x.startswith('release ') for x in self.ops()))
        self.run_tool('--prune', expected=1)
        self.assertEqual(self.snaps()[self.name(LAST_MONTH)][2], 'monthly')

    def test_partial_tier_failure_never_loses_incident_protection(self):
        self.feed(ANCHOR.date())
        before = set(self.snaps())
        self.run_tool('--prune', expected=1, fail_property='zht:tier')
        self.assertEqual(set(self.snaps()), before)
        self.assertTrue(all(any(path == n and tag.startswith('zht:latched:') for path,tag in self.holds()) for n in before))
        self.run_tool('--prune', expected=1)
        self.run_tool('--doctor')

    def test_protective_hold_failure_does_not_demote_or_destroy(self):
        self.feed(ANCHOR.date())
        before = self.snaps()
        self.reset_ops()
        self.run_tool('--prune', expected=1, fail='hold')
        self.assertEqual(self.snaps(), before)
        self.assertFalse(any(x.startswith(('release ', 'destroy ')) for x in self.ops()))

    def test_dry_run_and_malformed_history_fail_closed(self):
        self.feed(ANCHOR.date())
        self.reset_ops()
        self.run_tool(expected=1)
        self.assertEqual(self.ops(), [])
        self.run_tool('--prune', expected=1)
        name = self.name(BEFORE)
        snaps = self.snaps()
        snaps[name][5] = 'not-json'
        (self.fz / 'snaps').write_text('\n'.join('\t'.join(f) for f in snaps.values()) + '\n')
        before = set(snaps)
        self.reset_ops()
        self.run_tool('--prune', expected=1)
        self.assertEqual(set(self.snaps()), before)
        self.assertFalse(any(x.startswith(('release ', 'destroy ')) for x in self.ops()))
        self.run_tool('--doctor', expected=1)
        self.run_tool('--acknowledge-false-positive', expected=1)
        self.reset_ops()
        self.run_tool('--prune', '--force', expected=1)
        self.assertEqual(set(self.snaps()), before)
        self.assertFalse(any(x.startswith('destroy ') for x in self.ops()))


    def test_legacy_demotion_is_released_exactly_once_before_destroy(self):
        self.feed(ANCHOR.date())
        self.run_tool('--acknowledge-false-positive')
        # Legacy established dataset, with a stale monthly hold on a now-plain
        # daily: the exact demotion->destroy path observed in the real prune.
        props = self.props(); props[('archive/home/rich','zht:established')] = 'yes'
        (self.fz / 'dsprops').write_text(''.join('\t'.join((*key,value)) + '\n' for key,value in props.items()))
        day = START + dt.timedelta(days=2)
        while day.weekday() == 6: day += dt.timedelta(days=1)
        name = self.name(day)
        snaps = self.snaps(); snaps[name][1:3] = ['9999999999','monthly']
        (self.fz / 'snaps').write_text('\n'.join('\t'.join(f) for f in snaps.values()) + '\n')
        with (self.fz / 'holds').open('a') as f:f.write(name + '\tzht:monthly\t-\n')
        self.reset_ops()
        out = self.run_tool('--prune', '--force')
        self.assertNotIn('FAILED',out)
        self.assertNotIn(name,self.snaps())
        self.assertEqual(self.ops().count('release zht:monthly ' + name),1)
        self.run_tool('--doctor')

    def test_failed_establishment_record_cannot_write_misleading_tiers(self):
        self.feed(ANCHOR.date())
        # Strip the new explicit property to simulate upgrading legacy state.
        lines = (self.fz / 'dsprops').read_text().splitlines()
        (self.fz / 'dsprops').write_text('\n'.join(x for x in lines if 'zht:established' not in x)+'\n')
        before = self.snaps()
        self.reset_ops()
        self.run_tool('--prune',expected=1,fail_property='zht:established')
        self.assertEqual(self.snaps(),before)
        self.assertFalse(any(x.startswith(('release ','destroy ')) for x in self.ops()))

    def test_doctor_explains_legacy_stale_monthly_holds(self):
        self.feed(ANCHOR.date())
        out=self.run_tool('--doctor',expected=1)
        self.assertIn('STALE MANAGED HOLD',out)
        self.assertIn('legacy provenance unknown',out)
        self.assertIn('STALE METADATA',out)
        self.assertFalse(any('zht:latch_history=' in x for x in self.ops()))

    def test_reconcile_cannot_be_combined_with_destructive_flags(self):
        before=self.snaps();self.reset_ops()
        self.run_tool('--reconcile','--force',expected=2)
        self.run_tool('--reconcile','--prune',expected=2)
        self.assertEqual(self.snaps(),before)
        self.assertEqual(self.ops(),[])


    def test_second_incident_cannot_borrow_history_and_ack_repairs_before_clear(self):
        self.feed(ANCHOR.date()); self.run_tool('--prune', expected=1)
        self.run_tool('--acknowledge-false-positive'); self.run_tool('--prune','--force')
        first_id=self.props()[('archive','zht:latch_id')]
        self.latch('another legitimate change')
        self.run_tool('--prune',expected=1,fail_property='zht:latch_history')
        second_id=self.props()[('archive','zht:latch_id')]
        self.assertNotEqual(first_id,second_id)
        self.assertTrue(all(all(i['id']!=second_id for i in json.loads(f[5])) for f in self.snaps().values()))
        # Simulate an external/legacy health reset. It must not authorize release
        # of the newer hold using the valid history of incident A.
        props=self.props();props[('archive','zht:health')]='healthy'
        (self.fz/'dsprops').write_text(''.join('\t'.join((*k,v))+'\n' for k,v in props.items()))
        before=set(self.snaps());self.reset_ops()
        self.run_tool('--prune','--force',expected=1,fail_property='zht:latch_history')
        self.assertEqual(set(self.snaps()),before)
        self.assertFalse(any(x.startswith(('destroy ','release zht:latched:')) for x in self.ops()))
        self.assertIn('UNVERIFIED',self.run_tool('--summary',expected=1))
        self.latch('another legitimate change')
        self.run_tool('--acknowledge-false-positive',expected=1,fail_property='zht:latch_history')
        self.assertEqual(self.props()[('archive','zht:health')],'unhealthy')
        self.reset_ops();self.run_tool('--acknowledge-false-positive')
        self.assertEqual(set(self.snaps()),before)
        self.assertFalse(any(x.startswith(('destroy ','release zht:latched:')) for x in self.ops()))
        self.assertTrue(all(any(i['id']==second_id for i in json.loads(f[5])) for f in self.snaps().values()))
        self.run_tool('--prune','--force')

    def test_malformed_history_without_a_hold_blocks_prune_and_reconcile(self):
        self.feed(ANCHOR.date());props=self.props();props[('archive','zht:health')]='healthy'
        (self.fz/'dsprops').write_text(''.join('\t'.join((*k,v))+'\n' for k,v in props.items()))
        snaps=self.snaps();snaps[self.name(START+dt.timedelta(days=2))][5]='not-json'
        (self.fz/'snaps').write_text('\n'.join('\t'.join(f) for f in snaps.values())+'\n')
        before=set(snaps);self.reset_ops()
        self.run_tool('--prune','--force',expected=1);self.run_tool('--reconcile',expected=1)
        self.assertEqual(set(self.snaps()),before)
        self.assertFalse(any(x.startswith(('destroy ','release ')) for x in self.ops()))
        self.assertIn('UNVERIFIED',self.run_tool('--summary',expected=1))

    def test_summary_finds_all_missing_and_partially_missing_history(self):
        self.feed(ANCHOR.date());self.run_tool('--prune',expected=1,fail_property='zht:latch_history')
        self.assertIn('UNVERIFIED',self.run_tool('--summary',expected=1))
        snaps=self.snaps();name=next(iter(snaps));identity=self.props()[('archive','zht:latch_id')]
        snaps[name][5]=json.dumps([{'id':identity,'reason':'legitimate change','observed_utc':ANCHOR.isoformat()}])
        (self.fz/'snaps').write_text('\n'.join('\t'.join(f) for f in snaps.values())+'\n')
        self.assertIn('UNVERIFIED',self.run_tool('--summary',expected=1))

    def test_actual_shock_false_positive_acceptance_and_new_shock(self):
        self.feed(ANCHOR.date());snaps=self.snaps();tip=self.name(ANCHOR.date())
        snaps[tip][3:5]=['100000','90000']
        (self.fz/'snaps').write_text('\n'.join('\t'.join(f) for f in snaps.values())+'\n')
        props=self.props();props[('archive','zht:health')]='healthy'
        (self.fz/'dsprops').write_text(''.join('\t'.join((*k,v))+'\n' for k,v in props.items()))
        before=set(snaps);self.reset_ops();self.run_tool('--prune','--force',expected=1)
        self.assertEqual(self.props()[('archive','zht:health')],'unhealthy')
        self.assertIn('shrank',self.props()[('archive','zht:health_reason')])
        self.assertEqual(set(self.snaps()),before)
        self.reset_ops();self.run_tool('--acknowledge-false-positive')
        self.assertEqual(set(self.snaps()),before)
        self.assertFalse(any(x.startswith('destroy ') for x in self.ops()))
        accepted_id=self.props()[('archive','zht:latch_id')]
        # Same accepted change must not immediately re-latch on the next run.
        self.run_tool('--prune','--force')
        newer=ANCHOR+dt.timedelta(minutes=1)
        name='archive/home/rich@autosnap_'+newer.strftime('%Y-%m-%d_%H:%M:%S')+'_daily'
        with (self.fz/'snaps').open('a') as f:f.write('\t'.join([name,'-','-','1000','0','-'])+'\n')
        before=set(self.snaps());self.reset_ops();self.run_tool('--prune',expected=1)
        self.assertEqual(self.props()[('archive','zht:health')],'unhealthy')
        self.assertNotEqual(self.props()[('archive','zht:latch_id')],accepted_id)
        self.assertEqual(set(self.snaps()),before)
        self.assertFalse(any(x.startswith('destroy ') for x in self.ops()))

    def test_failed_acknowledgment_keeps_latch_and_protection(self):
        self.feed(ANCHOR.date());self.run_tool('--prune',expected=1)
        identity=self.props()[('archive','zht:latch_id')];before=set(self.snaps());self.reset_ops()
        self.run_tool('--acknowledge-false-positive',expected=1,fail_property='zht:false_positive_'+identity)
        self.assertEqual(self.props()[('archive','zht:health')],'unhealthy')
        self.assertEqual(set(self.snaps()),before)
        self.assertFalse(any(x.startswith(('destroy ','release zht:latched:')) for x in self.ops()))
        self.run_tool('--acknowledge-false-positive')

    def test_partial_safety_release_failure_retries_without_deleting(self):
        self.feed(ANCHOR.date());self.run_tool('--prune',expected=1)
        self.run_tool('--acknowledge-false-positive')
        before=set(self.snaps());self.reset_ops();target=sorted(before)[len(before)//2]
        self.run_tool('--prune','--force',expected=1,fail_release=target)
        self.assertEqual(set(self.snaps()),before)
        self.assertFalse(any(x.startswith('destroy ') for x in self.ops()))
        self.run_tool('--prune','--force');self.run_tool('--doctor')

    def test_generic_reset_and_attack_cleanup_are_not_supported(self):
        before=self.snaps();self.reset_ops()
        for flags in [('--reset-health',),('--reset-health','--prune'),('--prune-latched','--prune'),
                      ('--acknowledge-false-positive','--prune'),('--acknowledge-false-positive','--dataset','archive/home/rich')]:
            self.run_tool(*flags,expected=2)
        self.assertEqual(self.snaps(),before);self.assertEqual(self.ops(),[])


    def write_props(self, props):
        (self.fz/'dsprops').write_text(''.join('\t'.join((*k,v))+'\n' for k,v in props.items()))

    def test_collateral_new_warning_is_not_silently_accepted(self):
        self.feed(ANCHOR.date())
        cfg=json.loads(self.cfg.read_text());cfg['datasets']['archive/home/annie']='baseline';self.cfg.write_text(json.dumps(cfg))
        snaps=self.snaps();rich_tip=self.name(ANCHOR.date());snaps[rich_tip][3]='100000'
        for n,f in list(snaps.items()):
            a=n.replace('home/rich','home/annie');snaps[a]=[a,'-','-','1000000','0','-']
        (self.fz/'snaps').write_text('\n'.join('\t'.join(f) for f in snaps.values())+'\n')
        props=self.props();props[('archive','zht:health')]='healthy';self.write_props(props)
        self.run_tool('--prune','--force',expected=1)
        first_id=self.props()[('archive','zht:latch_id')]
        newer=ANCHOR+dt.timedelta(minutes=1)
        annie='archive/home/annie@autosnap_'+newer.strftime('%Y-%m-%d_%H:%M:%S')+'_daily'
        with (self.fz/'snaps').open('a') as f:f.write('\t'.join([annie,'-','-','1000000','900000','-'])+'\n')
        before=set(self.snaps());self.reset_ops()
        out=self.run_tool('--acknowledge-false-positive',expected=1)
        self.assertIn('NEW WARNING',out);self.assertIn('churn',out);self.assertIn('archive/home/annie',out)
        self.assertEqual(set(self.snaps()),before)
        self.assertFalse(any(x.startswith('destroy ') for x in self.ops()))
        self.assertNotIn(('archive/home/annie','zht:accepted_content'),self.props())
        self.assertEqual(self.props()[('archive/home/rich','zht:accepted_content')],first_id+'|'+rich_tip)
        evidence=json.loads(self.props()[('archive','zht:latch_evidence')])
        self.assertNotEqual(evidence['id'],first_id);self.assertEqual(evidence['snapshot'],annie)
        self.reset_ops();self.run_tool('--prune','--force',expected=1)
        self.assertFalse(any(x.startswith('destroy ') for x in self.ops()))
        # Only a separate explicit acceptance of Annie's now-visible warning
        # can clear it, and that acknowledgment also deletes nothing.
        self.reset_ops();self.run_tool('--acknowledge-false-positive')
        self.assertEqual(set(self.snaps()),before)
        self.assertFalse(any(x.startswith('destroy ') for x in self.ops()))

    def test_new_tip_on_same_dataset_requires_separate_review(self):
        self.feed(ANCHOR.date());snaps=self.snaps();tip=self.name(ANCHOR.date());snaps[tip][3]='100000'
        (self.fz/'snaps').write_text('\n'.join('\t'.join(f) for f in snaps.values())+'\n')
        props=self.props();props[('archive','zht:health')]='healthy';self.write_props(props)
        self.run_tool('--prune',expected=1);first_id=self.props()[('archive','zht:latch_id')]
        newer=ANCHOR+dt.timedelta(minutes=1);name='archive/home/rich@autosnap_'+newer.strftime('%Y-%m-%d_%H:%M:%S')+'_daily'
        with (self.fz/'snaps').open('a') as f:f.write('\t'.join([name,'-','-','1000','0','-'])+'\n')
        self.reset_ops();out=self.run_tool('--acknowledge-false-positive',expected=1)
        self.assertIn('NEW WARNING',out)
        self.assertEqual(self.props()[('archive/home/rich','zht:accepted_content')],first_id+'|'+tip)
        self.assertEqual(json.loads(self.props()[('archive','zht:latch_evidence')])['snapshot'],name)
        self.assertFalse(any(x.startswith('destroy ') for x in self.ops()))

    def test_interrupted_clear_keeps_same_incident_and_receipt(self):
        self.feed(ANCHOR.date());self.run_tool('--prune',expected=1)
        identity=self.props()[('archive','zht:latch_id')];tip=max(self.snaps());fields=self.snaps()[tip]
        props=self.props();props[('archive','zht:latch_evidence')]=json.dumps({'id':identity,'dataset':'archive/home/rich','snapshot':tip,'reason':'legitimate worktree deletion','referenced':int(fields[3]),'written':int(fields[4])});self.write_props(props)
        before=set(self.snaps());self.run_tool('--acknowledge-false-positive',expected=1,fail_clear=True)
        receipt=self.props()[('archive','zht:false_positive_'+identity)]
        self.run_tool('--prune',expected=1)
        self.assertEqual(self.props()[('archive','zht:latch_id')],identity)
        self.assertTrue(all(len(json.loads(f[5]))==1 for f in self.snaps().values()))
        self.reset_ops();self.run_tool('--acknowledge-false-positive')
        self.assertEqual(self.props()[('archive','zht:false_positive_'+identity)],receipt)
        self.assertEqual(set(self.snaps()),before)
        self.assertFalse(any(x.startswith('destroy ') for x in self.ops()))

    def test_new_warning_evidence_survives_failed_identity_and_health_mirrors(self):
        self.feed(ANCHOR.date());self.run_tool('--prune',expected=1);self.run_tool('--acknowledge-false-positive')
        first_id=self.props()[('archive','zht:latch_id')]
        newer=ANCHOR+dt.timedelta(minutes=1);name='archive/home/rich@autosnap_'+newer.strftime('%Y-%m-%d_%H:%M:%S')+'_daily'
        with (self.fz/'snaps').open('a') as f:f.write('\t'.join([name,'-','-','1000','0','-'])+'\n')
        before=set(self.snaps());self.reset_ops()
        self.run_tool('--prune',expected=1,fail_property='zht:latch_id')
        evidence=json.loads(self.props()[('archive','zht:latch_evidence')]);second_id=evidence['id']
        self.assertNotEqual(second_id,first_id);self.assertEqual(self.props()[('archive','zht:latch_id')],first_id)
        # Simulate interruption before compatibility health is written. The
        # evidence sentinel alone must remain a latch, never reuse old approval.
        props=self.props();props[('archive','zht:health')]='healthy';self.write_props(props)
        self.reset_ops();out=self.run_tool('--prune','--force',expected=1,fail_property='zht:health')
        self.assertEqual(set(self.snaps()),before);self.assertFalse(any(x.startswith('destroy ') for x in self.ops()))
        self.assertIn('archive: WORRY',self.run_tool('--summary'))
        self.assertEqual(json.loads(self.props()[('archive','zht:latch_evidence')])['id'],second_id)
        self.assertNotIn(('archive','zht:false_positive_'+second_id),self.props())

    def test_missing_trigger_evidence_snapshot_cannot_be_accepted(self):
        self.feed(ANCHOR.date());self.run_tool('--prune',expected=1)
        identity=self.props()[('archive','zht:latch_id')];props=self.props()
        props[('archive','zht:latch_evidence')]=json.dumps({'id':identity,'dataset':'archive/home/rich','snapshot':self.name(ANCHOR.date()),'reason':'archive/home/rich: newest daily shrank 90%','kind':'shrink','referenced':42,'written':0});self.write_props(props)
        self.reset_ops();self.run_tool('--acknowledge-false-positive',expected=1)
        self.assertNotIn(('archive','zht:false_positive_'+identity),self.props())
        self.assertFalse(any(x.startswith('destroy ') for x in self.ops()))


    def seed_content_shock(self):
        self.feed(ANCHOR.date());snaps=self.snaps();tip=self.name(ANCHOR.date());snaps[tip][3]='100000'
        (self.fz/'snaps').write_text('\n'.join('\t'.join(f) for f in snaps.values())+'\n')
        props=self.props();props[('archive','zht:health')]='healthy';self.write_props(props)
        return tip

    def test_verification_error_never_authorizes_content_exception(self):
        tip=self.seed_content_shock()
        self.run_tool('--prune',expected=1,fail_get='zht:accepted_content')
        old=json.loads(self.props()[('archive','zht:latch_evidence')])
        self.assertEqual(old['kind'],'verification')
        self.reset_ops()
        out=self.run_tool('--acknowledge-false-positive',expected=1)
        self.assertIn('NEW WARNING',out);self.assertIn('shrank',out)
        new=json.loads(self.props()[('archive','zht:latch_evidence')])
        self.assertEqual(new['kind'],'shrink');self.assertNotEqual(new['id'],old['id']);self.assertEqual(new['snapshot'],tip)
        self.assertNotIn(('archive/home/rich','zht:accepted_content'),self.props())
        self.assertFalse(any(x.startswith('destroy ') for x in self.ops()))
        self.reset_ops();self.run_tool('--prune','--force',expected=1)
        self.assertFalse(any(x.startswith('destroy ') for x in self.ops()))

    def test_unresolved_verification_fault_cannot_be_acknowledged(self):
        self.seed_content_shock();self.run_tool('--prune',expected=1,fail_get='zht:accepted_content')
        identity=json.loads(self.props()[('archive','zht:latch_evidence')])['id'];self.reset_ops()
        self.run_tool('--acknowledge-false-positive',expected=1,fail_get='zht:accepted_content')
        self.assertNotIn(('archive','zht:false_positive_'+identity),self.props())
        self.assertFalse(any(x.startswith('destroy ') for x in self.ops()))

    def test_receipt_requires_durable_unhealthy_fallback_and_failed_scan_stays_frozen(self):
        self.seed_content_shock();self.run_tool('--prune',expected=1,fail_property='zht:health')
        identity=json.loads(self.props()[('archive','zht:latch_evidence')])['id']
        self.assertEqual(self.props()[('archive','zht:health')],'healthy')
        self.reset_ops();self.run_tool('--acknowledge-false-positive',expected=1,fail_property='zht:health')
        self.assertNotIn(('archive','zht:false_positive_'+identity),self.props())
        self.assertIn('archive: WORRY',self.run_tool('--summary'))
        self.reset_ops();self.run_tool('--acknowledge-false-positive',expected=1,fail_after_receipt='list')
        self.assertIn(('archive','zht:false_positive_'+identity),self.props())
        self.assertEqual(self.props()[('archive','zht:health')],'unhealthy')
        self.assertIn('archive: WORRY',self.run_tool('--summary'))
        before=set(self.snaps());self.reset_ops();self.run_tool('--prune','--force',expected=1)
        self.assertEqual(set(self.snaps()),before);self.assertFalse(any(x.startswith('destroy ') for x in self.ops()))
        self.run_tool('--acknowledge-false-positive')

    def test_resolved_history_survives_config_removal_but_active_scope_cannot_disappear(self):
        self.seed_content_shock();self.run_tool('--prune',expected=1)
        # An active incident cannot be acknowledged without its dataset in scope.
        cfg=json.loads(self.cfg.read_text());cfg['datasets']={'archive/home/annie':'baseline'};self.cfg.write_text(json.dumps(cfg))
        before=set(self.snaps());self.run_tool('--acknowledge-false-positive',expected=1)
        self.assertEqual(set(self.snaps()),before)
        cfg['datasets']={'archive/home/rich':'baseline'};self.cfg.write_text(json.dumps(cfg));self.run_tool('--acknowledge-false-positive')
        cfg['datasets']={'archive/home/annie':'baseline'};self.cfg.write_text(json.dumps(cfg))
        self.assertIn('archive: healthy',self.run_tool('--summary'))


if __name__ == '__main__':
    unittest.main(verbosity=2)
