"""Probe regressions: no model, browser or synthetic desktop input required."""
import copy
import sys
import unittest

from run_probe import Fixture, pointer_passed_through, scene_request_confirmed


class ProbeEvidenceTests(unittest.TestCase):
    def test_body_input_requires_actual_browser_receipt_and_unchanged_reader(self):
        before = {'mouseDown': 0, 'wheel': 0, 'scrollOffset': 0, 'foreground': 42}
        for event_type in ('pointerdown', 'wheel'):
            trial = {'events': [{'type': event_type, 'isTrusted': True}],
                     'counts': {'windowBlur': 0, 'hidden': 0}, 'dropped': 0}
            self.assertTrue(pointer_passed_through(before, before, trial, event_type))
            for events in ([], [{'type': event_type, 'isTrusted': False}]):
                self.assertFalse(pointer_passed_through(before, before, dict(trial, events=events), event_type))
            for key, value in (('mouseDown', 1), ('wheel', 1), ('scrollOffset', 100), ('foreground', 99)):
                self.assertFalse(pointer_passed_through(before, dict(before, **{key: value}), trial, event_type))
            self.assertFalse(pointer_passed_through(before, before, dict(trial, dropped=1), event_type))
            self.assertFalse(pointer_passed_through(before, before,
                dict(trial, counts={'windowBlur': 1, 'hidden': 0}), event_type))

    def test_scene_uses_current_preset_schema_and_resolved_model(self):
        before = {'sceneDefinitions': [{'Id': 'coding', 'SystemPrompt': 'custom instructions'}],
                  'sceneModels': {'coding': 'scene-model'}, 'sceneRequests': [], 'sceneCompletions': 0}
        after = {'sceneRequests': [{'model': 'scene-model', 'messages': [
                    {'role': 'system', 'content': 'custom instructions'},
                    {'role': 'user', 'content': 'Synthetic scene input coding'}]}],
                 'sceneCompletions': 1, 'sceneErrors': [], 'endpointError': None}
        self.assertTrue(scene_request_confirmed('coding', before, after, ['scene:coding']))
        wrong = copy.deepcopy(after); wrong['sceneRequests'][0]['model'] = 'wrong-model'
        self.assertFalse(scene_request_confirmed('coding', before, wrong, ['scene:coding']))
        wrong = copy.deepcopy(after); wrong['sceneRequests'][0]['messages'][1]['content'] = 'template-prefixed input'
        self.assertFalse(scene_request_confirmed('coding', before, wrong, ['scene:coding']))
        self.assertFalse(scene_request_confirmed('coding', before, after, []))

    @unittest.skipUnless(sys.platform == 'win32', 'Requires the production WPF fixture')
    def test_fixture_waits_for_render_and_scroll_before_replying(self):
        fixture = Fixture()
        try:
            fixture.command('show')
            for _ in range(10):
                home = fixture.command('render')
                self.assertEqual(home['scrollOffset'], 0)
                self.assertGreater(home['scrollableHeight'], 0)
                down = fixture.command('pageDown')
                self.assertGreater(down['scrollOffset'], home['scrollOffset'])
                self.assertEqual(fixture.command('state')['scrollOffset'], down['scrollOffset'])
                up = fixture.command('pageUp')
                self.assertLess(up['scrollOffset'], down['scrollOffset'])
        finally:
            fixture.close()


if __name__ == '__main__':
    unittest.main()
