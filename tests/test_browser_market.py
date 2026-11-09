"""Browser transport and cache integration without a network or Chrome dependency."""
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from pcfish_planner import browser_market
from pcfish_planner.market import MarketMonitor, MarketUnavailable, listing_url
from pcfish_planner.browser_market import fetch_quote
from test_bridge import snapshot
from test_market import MARKET, NAME, NOW, fish, page


class DriverError(Exception):
    pass


class Wait:
    def __init__(self, driver, timeout, **kwargs):
        self.driver = driver

    def until(self, read):
        for _ in range(3):
            value = read(self.driver)
            if value:
                return value
        raise DriverError('timeout')


class Driver:
    def __init__(self, *, status=200, results=None, redirect=None, fail_get=False):
        self.status = status
        self.results = results if results is not None else [{'query_data': json.dumps({'queries': page()})}]
        self.redirect = redirect
        self.fail_get = fail_get
        self.current_url = ''
        self.navigations = []
        self.closed = False
        self.reads = 0

    def set_page_load_timeout(self, seconds):
        self.timeout = seconds

    def get(self, url):
        self.navigations.append(url)
        self.current_url = self.redirect or url
        if self.fail_get:
            raise DriverError('native browser failure with private details')

    def get_log(self, kind):
        return [{'message': json.dumps({'message': {'method': 'Network.responseReceived', 'params': {
            'type': 'Document', 'response': {'url': self.current_url, 'status': self.status}}}})}]

    def execute_script(self, script):
        value = self.results[min(self.reads, len(self.results) - 1)]
        self.reads += 1
        return value

    def quit(self):
        self.closed = True


class BrowserMarketTests(unittest.TestCase):
    def fetch(self, driver):
        with patch.object(browser_market, '_selenium', return_value=(None, Wait, DriverError)), \
                patch.object(browser_market, 'create_driver', return_value=driver), \
                patch.object(browser_market.time, 'time', return_value=NOW):
            return fetch_quote(NAME)  # Legacy one-page transport remains read-only.

    def test_default_transport_reads_hydrated_data_and_closes_browser(self):
        driver = Driver(results=[{'blocked': False}, {'query_data': json.dumps({'queries': page()})}])
        quote = self.fetch(driver)
        self.assertEqual(quote['lookup_backend'], 'selenium_chrome')
        self.assertEqual(quote['samples'][0]['seller_proceeds_krw'], 1000)
        self.assertEqual(quote['samples'][0]['buyer_total_krw'], 1150)
        self.assertEqual(driver.navigations, [listing_url(NAME)])
        self.assertTrue(driver.closed)

    def test_http_rate_limits_stop_once_and_request_backoff(self):
        for status in (403, 429):
            driver = Driver(status=status)
            with self.subTest(status=status), self.assertRaises(MarketUnavailable) as caught:
                self.fetch(driver)
            self.assertEqual(caught.exception.retry_after_seconds, 3600)
            self.assertEqual(len(driver.navigations), 1)
            self.assertEqual(driver.reads, 0)
            self.assertTrue(driver.closed)

    def test_challenge_redirect_http_error_and_malformed_data_fail_closed(self):
        cases = [Driver(results=[{'blocked': True}]), Driver(redirect='https://steamcommunity.com/login/'),
                 Driver(status=404), Driver(results=[{'query_data': '{}'}]), Driver(results=[{'query_data': 'bad json'}]),
                 Driver(results=[{'query_data': 'x' * (browser_market.MAX_QUERY_BYTES + 1)}])]
        for driver in cases:
            with self.subTest(driver=driver), self.assertRaises(MarketUnavailable):
                self.fetch(driver)
            self.assertEqual(len(driver.navigations), 1)
            self.assertTrue(driver.closed)

    def test_browser_failure_is_sanitized_and_still_closes(self):
        driver = Driver(fail_get=True)
        with self.assertRaises(MarketUnavailable) as caught:
            self.fetch(driver)
        self.assertNotIn('private details', str(caught.exception))
        self.assertTrue(driver.closed)

    def test_missing_or_timed_out_market_data_never_returns_a_quote(self):
        driver = Driver(results=[{'blocked': False}])
        with self.assertRaises(MarketUnavailable):
            self.fetch(driver)
        self.assertTrue(driver.closed)
        self.assertEqual(len(driver.navigations), 1)

    def test_driver_uses_isolated_headless_chrome(self):
        class Options:
            def __init__(self): self.arguments = []; self.capabilities = {}; self.experimental = {}
            def add_argument(self, argument): self.arguments.append(argument)
            def set_capability(self, key, value): self.capabilities[key] = value
            def add_experimental_option(self, key, value): self.experimental[key] = value
        options = Options()
        captured = {}
        def chrome(**kwargs): captured.update(kwargs); return 'owned driver'
        api = SimpleNamespace(ChromeOptions=lambda: options, Chrome=chrome)
        with patch.object(browser_market, '_selenium', return_value=(api, Wait, DriverError)), \
                patch.object(browser_market, 'chrome_binary', return_value='installed-chrome'):
            self.assertEqual(browser_market.create_driver(), 'owned driver')
        self.assertEqual(options.binary_location, 'installed-chrome')
        self.assertIn('--headless=new', options.arguments)
        self.assertEqual(options.experimental['prefs']['profile.managed_default_content_settings.images'], 2)
        self.assertFalse(any('user-data-dir' in v or 'profile-directory' in v for v in options.arguments))

    def test_nested_steam_api_rate_limit_is_global_and_honors_retry_after(self):
        driver = Driver()
        def logs(_):
            return [{'message': json.dumps({'message': {'method': 'Network.responseReceived', 'params': {
                'type': 'Fetch', 'response': {'url': 'https://api.steampowered.com/IMarketService/GetData/v1/',
                                            'status': 429, 'headers': {'retry-after': '7200'}}}}})}]
        driver.get_log = logs
        with self.assertRaises(MarketUnavailable) as caught:
            self.fetch(driver)
        self.assertTrue(caught.exception.rate_limited)
        self.assertEqual(caught.exception.retry_after_seconds, 7200)
        self.assertTrue(driver.closed)

    def test_browser_failure_to_start_leaves_no_partial_quote(self):
        with patch.object(browser_market, '_selenium', return_value=(None, Wait, DriverError)), \
                patch.object(browser_market, 'create_driver', side_effect=DriverError('bad driver')):
            with self.assertRaises(MarketUnavailable):
                fetch_quote(NAME)

    def test_monitor_uses_browser_once_and_reuses_json_after_restart(self):
        driver = Driver()
        with tempfile.TemporaryDirectory() as directory, \
                patch.object(browser_market, '_selenium', return_value=(None, Wait, DriverError)), \
                patch.object(browser_market, 'create_driver', return_value=driver) as create, \
                patch.object(browser_market.time, 'time', return_value=NOW):
            root = Path(directory); monitor = MarketMonitor(root, fetch_quote)
            s = snapshot([fish()])
            try:
                monitor.update(s, MARKET, NOW)
                monitor.future.result(timeout=2)
                report = monitor.update(s, MARKET, NOW + 1)
                self.assertEqual(report['fish'][0]['state'], 'keep')
                stored = json.loads((root / 'market-cache.json').read_text(encoding='utf-8'))
                self.assertEqual(stored['quotes'][NAME]['lookup_backend'], 'selenium_chrome')
                restarted = MarketMonitor(root, fetch_quote)
                try:
                    s['fish'].append(fish('newborn'))
                    report = restarted.update(s, MARKET, NOW + 86399)
                    self.assertEqual(report['fish'][1]['state'], 'keep')
                    self.assertIsNone(restarted.future)
                    create.assert_called_once()
                finally: restarted.close()
            finally: monitor.close()

    def test_browser_429_backoff_is_saved_by_monitor(self):
        driver = Driver(status=429)
        with tempfile.TemporaryDirectory() as directory, \
                patch.object(browser_market, '_selenium', return_value=(None, Wait, DriverError)), \
                patch.object(browser_market, 'create_driver', return_value=driver):
            root = Path(directory); monitor = MarketMonitor(root, fetch_quote)
            s = snapshot([fish()])
            try:
                monitor.update(s, MARKET, NOW)
                with self.assertRaises(MarketUnavailable): monitor.future.result(timeout=2)
                report = monitor.update(s, MARKET, NOW + 1)
                self.assertEqual(report['next_request_at'], NOW + 3601)
                self.assertEqual(report['fish'][0]['state'], 'pending')
                self.assertEqual(json.loads((root / 'market-cache.json').read_text(encoding='utf-8'))['next_request_at'], NOW + 3601)
            finally: monitor.close()
