from copy import deepcopy
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

from pcfish_planner.engine import ValidationError
from pcfish_planner.learning import learn_rules
from tests.support import fish, result, wait_example


class LearningTests(unittest.TestCase):
    def observation(self, id='event1'):
        return {'event_id': id, 'confirmed': True, 'kind': 'breed',
                'parents': [fish('a'), fish('b')], 'result': result('child4')}

    def test_deduplicates_confirmed_events_and_counts_joint_outcomes(self):
        first = self.observation()
        second = self.observation('event2')
        second['result'] = result('child5', level=5, grade=3)
        data = learn_rules([first, deepcopy(first), second], 'test')
        self.assertEqual(data['unique_events'], 2)
        self.assertEqual(data['duplicate_events_ignored'], 1)
        self.assertEqual(data['rules'][0]['sample_count'], 2)
        self.assertEqual([x['probability'] for x in data['rules'][0]['outcomes']], [.5, .5])

    def test_rejects_unconfirmed_and_conflicting_duplicate_results(self):
        data = self.observation()
        data['confirmed'] = False
        with self.assertRaises(ValidationError): learn_rules([data], 'test')
        a = self.observation()
        b = self.observation()
        b['result']['grade'] = 5
        with self.assertRaises(ValidationError): learn_rules([a, b], 'test')

    def test_synthetic_observations_keep_their_provenance(self):
        event = self.observation()
        event['synthetic'] = True
        self.assertEqual(learn_rules([event], 'test')['rules'][0]['evidence'], 'synthetic')

    def test_synthetic_and_real_samples_cannot_be_mixed(self):
        simulated = self.observation()
        simulated['synthetic'] = True
        with self.assertRaises(ValidationError):
            learn_rules([simulated, self.observation('event2')], 'test')


class CLITests(unittest.TestCase):
    def test_json_command_and_file_output(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / 'input.json'
            target = Path(directory) / 'result.json'
            source.write_text(json.dumps(wait_example()), encoding='utf-8')
            proc = subprocess.run([sys.executable, '-m', 'pcfish_planner', 'plan', str(source), '--output', str(target)], capture_output=True)
            self.assertEqual(proc.returncode, 0, proc.stderr.decode(errors='replace'))
            self.assertEqual(json.loads(target.read_text(encoding='utf-8'))['action']['kind'], 'wait')

    def test_invalid_json_has_nonzero_exit_and_no_recommendation(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / 'input.json'
            source.write_text('{bad', encoding='utf-8')
            proc = subprocess.run([sys.executable, '-m', 'pcfish_planner', 'plan', str(source)], capture_output=True)
            self.assertEqual(proc.returncode, 2)
            self.assertIn('error', json.loads(proc.stderr))


if __name__ == '__main__':
    unittest.main()
