"""Exercise the actual worker process against isolated synthetic files."""
from hashlib import sha256
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest

from pcfish_planner.bridge import atomic, read_json, read_text
from test_bridge import CONFIG, live_fish, snapshot


class WorkerIPCTests(unittest.TestCase):
    def test_market_worker_holds_unknown_and_emits_only_after_native_review(self):
        # Seed only a synthetic cache: this subprocess test never contacts Steam.
        with tempfile.TemporaryDirectory() as d:
            root = Path(d); cfg_path = root / 'config.json'
            atomic(cfg_path, {**CONFIG, 'protection_mode': 'market'})
            name = 'Example Fish ★★★★★'
            fish = [live_fish(id, grade=5) for id in ('a', 'b')]
            for f in fish:
                f.update(maximum_breeds=2, market_hash_name=name, market_state='pending', market_valid_until=0)
            now = time.time()
            atomic(root / 'market-cache.json', {'schema_version': 1, 'next_request_at': now + 3600, 'quotes': {
                name: {'market_hash_name': name, 'checked_at': now, 'currency_id': 16, 'complete': True,
                       'trades_30d': 10, 'samples': [{'remaining_breeds': 1, 'maximum_breeds': 2,
                                                   'seller_proceeds_krw': 500, 'buyer_total_krw': 580}]}}})
            s = snapshot(fish)

            def publish():
                s['captured_at'] = time.time()
                s['config_digest'] = sha256(cfg_path.read_bytes()).hexdigest()
                atomic(root / 'snapshot.json', s)

            def wait_for(check):
                deadline = time.monotonic() + 10
                while time.monotonic() < deadline:
                    publish()
                    if check(): return
                    self.assertIsNone(proc.poll())
                    time.sleep(.1)
                self.fail('market worker did not reach expected state')

            publish()
            proc = subprocess.Popen([sys.executable, '-m', 'pcfish_planner.bridge', '--runtime', str(root), '--config', str(cfg_path),
                                     '--pid', str(os.getpid()), '--session', 'test'], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            try:
                wait_for(lambda: (root / 'recommendation.json').exists())
                report = read_json(root / 'market-review.json')
                self.assertEqual([r['state'] for r in report['fish']], ['release', 'release'])
                self.assertEqual(read_json(root / 'recommendation.json')['kind'], 'hold')
                self.assertFalse((root / 'command.json').exists())
                # Simulate the next native Capture after it independently verifies
                # identity, evidence freshness, thresholds and the config digest.
                for f in fish: f.update(market_state='release', market_valid_until=now + 3600)
                s['revision'] = 'native-reviewed'
                wait_for(lambda: (root / 'command.json').exists())
                command = read_json(root / 'command.json')
                self.assertEqual(command['kind'], 'breed')
                self.assertEqual(command['revision'], 'native-reviewed')
                self.assertEqual(command['config_digest'], report['config_digest'])
                s['session'] = 'replacement'; publish()
                self.assertEqual(proc.wait(timeout=5), 0)
            finally:
                if proc.poll() is None: proc.terminate()
                _, stderr = proc.communicate(timeout=5)
                self.assertEqual(stderr, b'')

    def test_config_hash_matches_native_reader_with_bom_and_crlf(self):
        with tempfile.TemporaryDirectory() as d:
            path = Path(d) / 'config.json'
            raw = b'{\r\n  "enabled": true\r\n}'
            path.write_bytes(b'\xef\xbb\xbf' + raw)
            self.assertEqual(sha256(read_text(path).encode('utf-8')).hexdigest(), sha256(raw).hexdigest())
            self.assertTrue(read_json(path)['enabled'])

    def test_worker_observes_then_emits_session_bound_command_and_respects_journal(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            cfg_path = root / 'config.json'
            atomic(cfg_path, {**CONFIG, 'mode': 'observe'})
            s = snapshot([live_fish('a'), live_fish('b'), live_fish('c')]
                         + [live_fish(f'm{i}', level=1, breeds=0) for i in range(10)])
            s['hearts'] = 5
            (root / 'rejected-events').mkdir()
            atomic(root / 'rejected-events' / 'failed-pair.json', {'kind': 'breed', 'outcome': 'rejected',
                   'consumed': False, 'completed_at': time.time(), 'cmd': {'fish_ids': ['a', 'b']}})

            def publish():
                s['captured_at'] = time.time()
                s['config_digest'] = sha256(cfg_path.read_bytes()).hexdigest()
                atomic(root / 'snapshot.json', s)

            def wait_for(check):
                deadline = time.monotonic() + 10
                while time.monotonic() < deadline:
                    publish()
                    if check():
                        return
                    self.assertIsNone(proc.poll(), (root / 'worker-status.json').read_text() if (root / 'worker-status.json').exists() else '')
                    time.sleep(.1)
                self.fail('worker did not reach expected state')

            publish()
            proc = subprocess.Popen([sys.executable, '-m', 'pcfish_planner.bridge', '--runtime', str(root), '--config', str(cfg_path),
                                     '--pid', str(os.getpid()), '--session', 'test'], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            try:
                wait_for(lambda: (root / 'worker-status.json').exists())
                self.assertFalse((root / 'command.json').exists())
                atomic(cfg_path, CONFIG)
                wait_for(lambda: (root / 'command.json').exists())
                command = read_json(root / 'command.json')
                self.assertEqual(command['session'], 'test')
                self.assertEqual(command['revision'], s['revision'])
                self.assertEqual(command['config_digest'], s['config_digest'])
                self.assertEqual(command['kind'], 'breed')
                self.assertNotEqual(set(command['fish_ids']), {'a', 'b'})
                self.assertTrue(set(command['fish_ids']) <= {'a', 'b', 'c'})
                self.assertLessEqual(command['expires_at'] - time.time(), 5)
                atomic(root / 'pending.json', {'request_id': command['request_id']})
                (root / 'command.json').unlink()
                old = read_json(root / 'worker-status.json')['captured_at']
                wait_for(lambda: read_json(root / 'worker-status.json')['captured_at'] > old)
                self.assertFalse((root / 'command.json').exists())
                s['session'] = 'replacement-game-session'
                publish()
                self.assertEqual(proc.wait(timeout=5), 0)
            finally:
                if proc.poll() is None:
                    proc.terminate()
                stdout, stderr = proc.communicate(timeout=5)
                self.assertEqual(stderr, b'')
