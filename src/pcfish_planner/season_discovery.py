"""Collect official season evidence; article contents are data, never commands."""
from concurrent.futures import ThreadPoolExecutor
from hashlib import sha256
import html
import json
from pathlib import Path
import re
from urllib.request import Request, urlopen

from .unlisted_value import NEW_COHORT

NEWS_API = 'https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid=4842670&count=30&maxlength=0&format=json'
NEWS_INDEX = 'https://store.steampowered.com/news/app/4842670'
SEASON2_SOURCE = 'https://store.steampowered.com/news/app/4842670/view/710038327520139217'


def announced_count(text, kind):
    numbers = dict(one=1, two=2, three=3, four=4, five=5, six=6, seven=7, eight=8, nine=9, ten=10)
    found = re.search(r'\b(\d+|one|two|three|four|five|six|seven|eight|nine|ten)\s+(?:(?:new|limited)\s+)*' + kind + r'\s+fish\b', text, re.I)
    return int(found[1]) if found and found[1].isdigit() else numbers.get(found[1].lower()) if found else None


def fetch_official_news():
    request = Request(NEWS_API, headers={'User-Agent': 'PCFishPlanner/season-evidence'})
    with urlopen(request, timeout=15) as response:
        raw = response.read(2_000_001)
    if len(raw) > 2_000_000:
        raise ValueError('Official news response exceeds local evidence limit')
    return parse_news(json.loads(raw))


def parse_news(payload):
    app = payload.get('appnews', {})
    if app.get('appid') != 4842670:
        raise ValueError('Official news app identity mismatch')
    result = []
    for item in app.get('newsitems', []):
        # The fixed Steam API and official announcement feed establish source.
        # Never follow URLs or execute instructions embedded in article text.
        if item.get('feedname') != 'steam_community_announcements':
            continue
        title, body = item.get('title', ''), item.get('contents', '')
        if not isinstance(title, str) or not isinstance(body, str) or not str(item.get('gid', '')).isdigit():
            continue
        text = html.unescape(re.sub(r'\[[^\]]*\]|<[^>]*>', ' ', title + '\n' + body))
        match = re.search(r'\bseason\s*(\d+)\b|시즌\s*(\d+)', text, re.I)
        season = int(next(x for x in match.groups() if x)) if match else 1 if re.search(r'first season|첫\s*시즌', text, re.I) else 0
        if not season or not re.search(r'new\s+(?:regular\s+|seasonal\s+)?fish|신규|새로운', text, re.I):
            continue
        result.append(dict(season=season, gid=str(item['gid']), title=title, published_at=item.get('date', 0),
                           url='https://steamcommunity.com/games/4842670/announcements/detail/' + str(item['gid']),
                           body_sha256=sha256(body.encode()).hexdigest(), text=text,
                           regular_count=announced_count(text, 'regular'), seasonal_count=announced_count(text, 'seasonal'),
                           live=bool(re.search(r'now live|is now live|시작되었|시작됐', text, re.I))
                                and not bool(re.search(r'almost here|soon|planned', title, re.I))))
    return result


class SeasonDiscovery:
    def __init__(self, runtime: Path, fetch=None):
        self.runtime, self.fetch = runtime, fetch
        self.executor = ThreadPoolExecutor(max_workers=1, thread_name_prefix='season-news')
        self.future = None
        self.data = dict(schema_version=1, catalog={}, announcements=[], next_refresh_at=0, last_error='')
        try:
            saved = json.loads((runtime / 'season-discovery.json').read_text(encoding='utf-8'))
            if saved.get('schema_version') == 1 and isinstance(saved.get('catalog'), dict) and isinstance(saved.get('announcements'), list):
                self.data = saved
        except (OSError, ValueError, TypeError):
            pass

    def close(self):
        self.executor.shutdown(wait=False, cancel_futures=True)

    def update(self, snapshot, now):
        from .bridge import atomic
        catalog = snapshot.get('market_catalog', [])
        season = snapshot.get('season', {}).get('id', 0)
        if not catalog or type(season) is not int or season <= 0:
            return dict(state='unavailable', reason='Current native season/catalog unavailable', new_regular_species=[])
        dirty = False
        first = not self.data['catalog']
        for entry in catalog:
            species = entry['species']
            if species in self.data['catalog']:
                continue
            # Existing installation bootstrap is explicitly evidenced, not an
            # inference that every unquoted species must be new.
            bootstrap = first and season == 2 and species in NEW_COHORT
            candidate = bootstrap or not first
            self.data['catalog'][species] = dict(entry, first_seen_at=now, observed_season=season,
                candidate_new=candidate, identification='verified_season2_bootstrap' if bootstrap
                else 'native_catalog_delta' if candidate else 'initial_catalog_baseline')
            dirty = True
        if self.future and self.future.done():
            try:
                self.data['announcements'] = self.future.result()
                self.data['last_error'] = ''
                self.data['last_success_at'] = now
                self.data['next_refresh_at'] = now + 86400
            except Exception as exc:
                self.data['last_error'] = 'Official season news unavailable: ' + type(exc).__name__
                self.data['next_refresh_at'] = now + 3600
            self.future = None
            dirty = True
        if self.data.get('last_requested_season') != season:
            self.data['next_refresh_at'] = min(now, self.data['next_refresh_at'])
        if self.fetch and self.future is None and now >= self.data['next_refresh_at'] and not snapshot.get('request_in_flight') and not snapshot.get('uncertain'):
            self.data['next_refresh_at'] = now + 86400
            self.data['last_requested_season'] = season
            self.future = self.executor.submit(self.fetch)
            dirty = True
        if dirty:
            atomic(self.runtime / 'season-discovery.json', self.data)
        articles = [a for a in self.data['announcements'] if a['season'] == season]
        article = max(articles, key=lambda a: (a['live'], a['published_at'])) if articles else None
        current_ids = {e['species'] for e in catalog}
        candidates = [e for e in self.data['catalog'].values() if e['species'] in current_ids
                      and e['candidate_new'] and e['observed_season'] == season]
        # Explicit first-install mapping was verified from the announcement and
        # native tables during deployment; later additions require fetched news.
        bootstrap = season == 2 and any(e['identification'] == 'verified_season2_bootstrap' for e in candidates)
        regular = [e for e in candidates if not e['seasonal']]
        seasonal = [e for e in candidates if e['seasonal']]
        counts_match = bool(article and article.get('regular_count') is not None
                            and article['regular_count'] == len(regular)
                            and (article.get('seasonal_count') is None or article['seasonal_count'] == len(seasonal)))
        verified = candidates if counts_match else [e for e in candidates if e['identification'] == 'verified_season2_bootstrap']
        confirmed = bool(verified)
        names = []
        for entry in candidates:
            name = entry['market_names'][0].removesuffix(' ★') if entry.get('market_names') else entry['species']
            names.append(dict(species=entry['species'], name=name, level=entry['level'], seasonal=entry['seasonal'],
                name_source='native_game_catalog', identification=entry['identification'],
                confirmed=entry in verified,
                name_also_in_announcement=bool(article and name.casefold() in article['text'].casefold())))
        public_article = {k:v for k,v in article.items() if k != 'text'} if article else None
        report = dict(state='verified' if confirmed else 'awaiting_official_season_announcement', season=season,
            captured_at=now, source_index=NEWS_INDEX, announcement=public_article,
            bootstrap_source=SEASON2_SOURCE if bootstrap else None, refresh_in_progress=self.future is not None,
            next_refresh_at=self.data['next_refresh_at'], last_error=self.data['last_error'],
            new_species=names, announcement_counts_match_catalog_delta=counts_match,
            new_regular_species=sorted(e['species'] for e in verified if not e['seasonal']),
            missing_quote_is_not_new_species_proof=True)
        atomic(self.runtime / 'season-evidence.json', report)
        return report
