from copy import deepcopy
from hashlib import sha256
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch, MagicMock
from pcfish_planner.bridge import run
from test_bridge import snapshot, live_fish, CONFIG

class EndLoop(BaseException): pass

class FreshCaptureTests(unittest.TestCase):
    def test_slow_auxiliary_work_uses_fresh_capture_and_preserves_guards(self):
        for changed in ('fresh', 'busy', 'session', 'config'):
            with self.subTest(changed=changed), tempfile.TemporaryDirectory() as d:
                root=Path(d); config=root/'config.json'
                raw=json.dumps({**CONFIG, 'protection_mode':'market'})
                config.write_text(raw)
                old=snapshot([live_fish('a',level=0),live_fish('b',level=0)]); old['config_digest']=sha256(raw.encode()).hexdigest()
                (root/'snapshot.json').write_text(json.dumps(old))
                fresh=deepcopy(old); fresh['captured_at']=10
                if changed=='busy': fresh['ui_busy']=True
                if changed=='session': fresh['session']='another'
                if changed=='config': fresh['config_digest']='another'
                monitor=MagicMock()
                monitor.update.side_effect=lambda *args: (root/'snapshot.json').write_text(json.dumps(fresh))
                with patch('pcfish_planner.bridge.MarketMonitor',return_value=monitor), patch('pcfish_planner.bridge.time.time',return_value=10), patch('pcfish_planner.bridge.time.sleep',side_effect=EndLoop):
                    try: run(root,config,'test')
                    except EndLoop: pass
                self.assertEqual((root/'command.json').exists(), changed=='fresh')
                if changed in ('fresh','busy'):
                    plan=json.loads((root/'recommendation.json').read_text())
                    self.assertEqual(plan['kind'], 'breed' if changed=='fresh' else 'hold')
                    self.assertNotEqual(plan.get('reason'),'Snapshot stale')
