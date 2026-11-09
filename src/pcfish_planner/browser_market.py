"""One anonymous headless Chrome visit for a missing or expired market quote."""
from __future__ import annotations

import json
import os
from pathlib import Path
import shutil
import time
from urllib.parse import unquote, urlsplit

from .market import MarketUnavailable, listing_url, parse_query_data, retry_delay

MAX_QUERY_BYTES = 8_000_000

# Steam removes its initial script element after copying public market queries
# into window.SSR. Read only those queries, never Config/UserConfig or cookies.
READ_PUBLIC_QUERIES = '''
    const initial = document.getElementById('valve-ssr-data');
    const data = initial
        ? JSON.parse(initial.textContent)?.renderContext?.queryData
        : window.SSR?.renderContext?.queryData;
    if (typeof data === 'string') return {query_data: data};
    const text = document.body?.innerText || '';
    return {blocked: /too many requests|access denied|captcha|verify you are human/i.test(text)};
'''


def _selenium():
    try:
        from selenium import webdriver
        from selenium.common.exceptions import WebDriverException
        from selenium.webdriver.support.ui import WebDriverWait
    except ImportError as exc:
        raise MarketUnavailable('Selenium is missing; install this project with the automation extra') from exc
    return webdriver, WebDriverWait, WebDriverException


def chrome_binary():
    candidates = [shutil.which(name) for name in ('google-chrome', 'google-chrome-stable', 'chrome')]
    for variable in ('PROGRAMFILES', 'PROGRAMFILES(X86)', 'LOCALAPPDATA'):
        if os.environ.get(variable):
            candidates.append(str(Path(os.environ[variable]) / 'Google/Chrome/Application/chrome.exe'))
    candidates.append('/Applications/Google Chrome.app/Contents/MacOS/Google Chrome')
    for candidate in candidates:
        if candidate and Path(candidate).is_file():
            return candidate
    raise MarketUnavailable('Google Chrome is missing; install Chrome for market protection')


def create_driver():
    webdriver, _, _ = _selenium()
    options = webdriver.ChromeOptions()
    options.binary_location = chrome_binary()
    options.page_load_strategy = 'eager'
    options.add_experimental_option('prefs', {'profile.managed_default_content_settings.images': 2})
    for argument in ('--headless=new', '--no-first-run', '--no-default-browser-check', '--disable-background-networking'):
        options.add_argument(argument)
    options.set_capability('goog:loggingPrefs', {'performance': 'ALL'})
    # ChromeDriver creates an isolated temporary profile. Never attach to the
    # user's browser or copy its Steam session. Selenium Manager caches drivers.
    return webdriver.Chrome(options=options)


def _same_product(actual, expected):
    a, e = urlsplit(actual), urlsplit(expected)
    return a.scheme == e.scheme and a.netloc == e.netloc and unquote(a.path) == unquote(e.path)


def _check_document_responses(driver, url):
    for entry in driver.get_log('performance'):
        try:
            message = json.loads(entry['message'])['message']
            params = message.get('params', {})
            if message['method'] != 'Network.responseReceived':
                continue
            response = params['response']
            status = response['status']
            host = urlsplit(response['url']).hostname or ''
            steam = host in ('steamcommunity.com', 'steampowered.com') or host.endswith(('.steamcommunity.com', '.steampowered.com'))
            document = params.get('type') == 'Document' and _same_product(response['url'], url)
            driver._pcfish_http_responses = getattr(driver, '_pcfish_http_responses', 0) + 1
        except (KeyError, TypeError, ValueError):
            continue
        if steam and status in (403, 429):
            raise MarketUnavailable(f'Steam browser lookup limited (HTTP {int(status)})',
                                    retry_after_seconds=retry_delay(response.get('headers'), time.time()), rate_limited=True)
        if document and status >= 400:
            raise MarketUnavailable(f'Steam browser lookup failed (HTTP {int(status)})', product_specific=status == 404)


def read_query_data(driver, url, wait_factory):
    def read(browser):
        if not _same_product(browser.current_url, url):
            raise MarketUnavailable('Unexpected Steam redirect; fish remains protected')
        _check_document_responses(browser, url)
        result = browser.execute_script(READ_PUBLIC_QUERIES)
        if not isinstance(result, dict):
            return False
        data = result.get('query_data')
        if isinstance(data, str):
            if len(data.encode('utf-8')) > MAX_QUERY_BYTES:
                raise MarketUnavailable('Steam market response too large')
            return data
        if result.get('blocked'):
            raise MarketUnavailable('Steam browser lookup requires manual verification', retry_after_seconds=3600, rate_limited=True)
        return False
    return wait_factory(driver, 30, poll_frequency=.5).until(read)


def fetch_quote(name):
    _, wait_factory, webdriver_error = _selenium()
    driver = None
    try:
        driver = create_driver()
        driver.set_page_load_timeout(30)
        url = listing_url(name)
        driver.get(url)  # Exactly one navigation; failures never retry here.
        query_data = read_query_data(driver, url, wait_factory)
        quote = parse_query_data(query_data, name, time.time())
        quote['lookup_backend'] = 'selenium_chrome'
        quote['http_responses_observed'] = getattr(driver, '_pcfish_http_responses', 0)
        return quote
    except MarketUnavailable as exc:
        exc.http_responses_observed = getattr(driver, '_pcfish_http_responses', 0)
        raise
    except webdriver_error as exc:
        # Browser exceptions may contain local paths and driver details. Keep
        # the public worker status concise and free of raw browser payloads.
        raise MarketUnavailable(f'Chrome market lookup unavailable ({type(exc).__name__})') from exc
    finally:
        if driver is not None:
            try:
                driver.quit()
            except webdriver_error:
                pass  # ChromeDriver's quit also stops its own driver service.
