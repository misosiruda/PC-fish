from pathlib import Path
import tempfile
import unittest

from pcfish_planner.season_discovery import parse_news, SeasonDiscovery
from pcfish_planner.unlisted_value import reference_products
from test_unlisted_value import entry, NOW, CFG


def payload(season=3):
    return {'appnews': {'appid': 4842670, 'newsitems': [dict(gid='123456789', feedname='steam_community_announcements',
        title=f'Season {season} Is Here!', date=NOW - 100, contents=f'[h1]Season {season} is now live[/h1][p]1 new regular fish[/p]')]}}


class SeasonDiscoveryTests(unittest.TestCase):
    def test_official_feed_app_identity_and_no_untrusted_url_following(self):
        p = payload(); p['appnews']['newsitems'][0]['url'] = 'https://example.invalid/execute'
        article = parse_news(p)[0]
        self.assertEqual(article['season'], 3)
        self.assertTrue(article['live'])
        self.assertEqual(article['url'], 'https://steamcommunity.com/games/4842670/announcements/detail/123456789')
        p['appnews']['appid'] = 1
        with self.assertRaises(ValueError): parse_news(p)

    def test_image_only_names_use_native_catalog_delta_and_official_season(self):
        with tempfile.TemporaryDirectory() as d:
            evidence = SeasonDiscovery(Path(d))
            s = {'season': {'id': 3}, 'market_catalog': [entry('OLD', 'Old Fish')]}
            try:
                initial = evidence.update(s, NOW)
                self.assertEqual(initial['new_regular_species'], [])
                evidence.data['announcements'] = parse_news(payload())
                s['market_catalog'].append(entry('FUTURE', 'Future Fish'))
                report = evidence.update(s, NOW + 1)
                self.assertEqual(report['new_regular_species'], ['FUTURE'])
                self.assertEqual(report['new_species'][0]['identification'], 'native_catalog_delta')
                self.assertFalse(report['new_species'][0]['name_also_in_announcement'])
                restarted = SeasonDiscovery(Path(d))
                try:
                    self.assertEqual(restarted.update(s, NOW + 2)['new_regular_species'], ['FUTURE'])
                finally: restarted.close()
            finally: evidence.close()

    def test_missing_quote_legacy_species_is_not_reclassified_as_new(self):
        with tempfile.TemporaryDirectory() as d:
            evidence = SeasonDiscovery(Path(d))
            try:
                evidence.data['announcements'] = parse_news(payload())
                report = evidence.update({'season': {'id': 3}, 'market_catalog': [entry('OLD', 'Old Fish')]}, NOW)
                self.assertEqual(report['new_regular_species'], [])
                self.assertTrue(report['missing_quote_is_not_new_species_proof'])
            finally: evidence.close()

    def test_discovered_future_species_requests_legacy_comparisons_without_hardcoded_ids(self):
        catalog = [entry('FUTURE', 'Future Fish'), entry('OLD', 'Old Fish')]
        fish = dict(id='f', species='FUTURE', level=1, grade=3, market_hash_name='Future Fish ★★★')
        config = {**CFG, '_discovered_regular_species': ['FUTURE'], '_discovered_cohort': ['FUTURE']}
        self.assertEqual(set(reference_products([fish], catalog, {}, config, NOW, 86400)), {'Old Fish ★★★'})

    def test_daily_news_cache_and_failure_backoff_are_persistent(self):
        calls = []
        def fetch(): calls.append(True); return parse_news(payload())
        with tempfile.TemporaryDirectory() as d:
            s = {'season': {'id': 3}, 'market_catalog': [entry('OLD', 'Old Fish')]}
            evidence = SeasonDiscovery(Path(d), fetch)
            try:
                evidence.update(s, NOW); evidence.future.result(timeout=2)
                evidence.update(s, NOW + 1)
                evidence.update(s, NOW + 500)
                self.assertEqual(len(calls), 1)
                restarted = SeasonDiscovery(Path(d), fetch)
                try:
                    restarted.update(s, NOW + 1000)
                    self.assertIsNone(restarted.future)
                    self.assertEqual(len(calls), 1)
                finally: restarted.close()
            finally: evidence.close()

    def test_seasonal_new_species_never_becomes_ordinary_estimate_target(self):
        with tempfile.TemporaryDirectory() as d:
            evidence = SeasonDiscovery(Path(d))
            try:
                evidence.data['announcements'] = parse_news(payload())
                s = {'season': {'id': 3}, 'market_catalog': [entry('OLD', 'Old Fish')]}
                evidence.update(s, NOW)
                s['market_catalog'].append(entry('SEASONAL', 'Special', seasonal=True))
                report = evidence.update(s, NOW + 1)
                self.assertEqual(report['new_regular_species'], [])
                self.assertEqual(report['new_species'][0]['species'], 'SEASONAL')
            finally: evidence.close()

    def test_catalog_change_not_covered_by_old_article_stays_unconfirmed(self):
        with tempfile.TemporaryDirectory() as d:
            evidence = SeasonDiscovery(Path(d))
            try:
                p = payload(); p['appnews']['newsitems'][0]['contents'] += ' 3 new regular fish'
                # The original first count says 1; two additions do not match.
                evidence.data['announcements'] = parse_news(p)
                s = {'season': {'id': 3}, 'market_catalog': [entry('OLD', 'Old Fish')]}
                evidence.update(s, NOW)
                s['market_catalog'] += [entry('NEW1', 'One'), entry('NEW2', 'Two')]
                report = evidence.update(s, NOW + 1)
                self.assertEqual(report['new_regular_species'], [])
                self.assertFalse(report['announcement_counts_match_catalog_delta'])
            finally: evidence.close()

    def test_failed_news_lookup_waits_one_hour_and_does_not_restart_loop(self):
        calls = []
        def fetch(): calls.append(1); raise OSError('offline')
        with tempfile.TemporaryDirectory() as d:
            evidence = SeasonDiscovery(Path(d), fetch)
            s = {'season': {'id': 3}, 'market_catalog': [entry('OLD', 'Old Fish')]}
            try:
                evidence.update(s, NOW)
                with self.assertRaises(OSError): evidence.future.result(timeout=2)
                evidence.update(s, NOW + 1)
                evidence.update(s, NOW + 100)
                self.assertEqual(calls, [1])
                self.assertEqual(evidence.data['next_refresh_at'], NOW + 3601)
            finally: evidence.close()
