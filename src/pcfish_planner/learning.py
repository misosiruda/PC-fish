"""Count confirmed local observations into explicit joint-outcome rules."""
from collections import Counter, defaultdict
from dataclasses import asdict
from hashlib import sha256
import json

from .engine import Fish, ValidationError, flag, text


def learn_rules(records: list[dict], source: str) -> dict:
    groups = defaultdict(Counter)
    seen = {}
    group_evidence = {}
    duplicates = 0
    for record in records:
        if record.get('confirmed') is not True:
            raise ValidationError('only confirmed outcomes can be learned')
        event_id = text(record.get('event_id'), 'event_id')
        kind = record.get('kind')
        if kind not in {'breed', 'merge'}:
            raise ValidationError('observation kind must be breed or merge')
        raw_parents = record.get('parents', [])
        if len(raw_parents) != (2 if kind == 'breed' else 10):
            raise ValidationError('wrong number of observed parents')
        parents = tuple(sorted(Fish.read(x, id='parent').traits() for x in raw_parents))
        evidence = 'synthetic' if flag(record.get('synthetic', False), 'observation.synthetic') else 'observed'
        key = (kind, parents)
        if group_evidence.setdefault(key, evidence) != evidence:
            raise ValidationError('synthetic and observed events cannot be mixed for the same parent multiset')
        result = record.get('result')
        if not isinstance(result, dict) or 'ready_after_seconds' not in result:
            raise ValidationError('observed result requires ready_after_seconds')
        fish = Fish.read(result, id='result', now=0)
        if fish.level == 0 or fish.seasonal:
            raise ValidationError('regular outcomes must be nonstarter, nonseasonal fish')
        normalized = asdict(fish)
        normalized.pop('id')
        normalized.pop('ready_at')
        normalized['ready_after_seconds'] = result['ready_after_seconds']
        outcome = json.dumps(normalized, sort_keys=True)
        identity = (kind, parents, outcome, evidence)
        if event_id in seen:
            if seen[event_id] != identity:
                raise ValidationError(f'conflicting duplicate event: {event_id}')
            duplicates += 1
            continue
        seen[event_id] = identity
        groups[(kind, parents)][outcome] += 1
    rules = []
    for (kind, parents), counts in sorted(groups.items()):
        count = sum(counts.values())
        key = json.dumps([kind, parents])
        templates = [{'species': p[0], 'level': p[1], 'grade': p[2], 'remaining_breeds': p[3],
                      'unlimited_breeding': p[4]} for p in parents]
        rules.append({'id': 'observed-' + sha256(key.encode()).hexdigest()[:12], 'kind': kind,
                      'parents': templates, 'evidence': group_evidence[(kind, parents)], 'sample_count': count,
                      'source': source,
                      'outcomes': [{'probability': n / count, 'fish': json.loads(result)} for result, n in sorted(counts.items())]})
    return {'rules': rules, 'unique_events': len(seen), 'duplicate_events_ignored': duplicates,
            'notes': ['관측 빈도 추정이며, 아직 보지 못한 결과가 발생하지 않는다는 보장은 아닙니다.',
                      '부모 순서는 같은 결과 분포를 갖는다고 가정해 묶습니다. 이 가정은 별도 검증이 필요합니다.',
                      '기본 최소 표본 수 미만의 규칙은 계획기가 사용하지 않습니다.']}
