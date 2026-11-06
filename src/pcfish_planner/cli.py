import argparse
import json
from pathlib import Path
import sys

from .engine import Planner, Problem, ValidationError
from .learning import learn_rules


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description='PC FISH read-only planner; never executes game or market actions.')
    parser.add_argument('command', choices=['plan', 'learn'])
    parser.add_argument('input', type=Path, help='plan JSON or confirmed-observation JSONL')
    parser.add_argument('--output', type=Path)
    args = parser.parse_args(argv)
    try:
        raw = args.input.read_text(encoding='utf-8-sig')
        if args.command == 'plan':
            result = Planner(Problem(json.loads(raw))).run()
        else:
            records = [json.loads(line) for line in raw.splitlines() if line.strip()]
            result = learn_rules(records, str(args.input.resolve()))
        encoded = json.dumps(result, ensure_ascii=False, indent=2, allow_nan=False) + '\n'
        if args.output:
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(encoded, encoding='utf-8')
        else:
            if hasattr(sys.stdout, 'reconfigure'):
                sys.stdout.reconfigure(encoding='utf-8')
            sys.stdout.write(encoded)
        return 0
    except (ValidationError, ValueError, TypeError, KeyError, OSError) as exc:
        print(json.dumps({'error': str(exc), 'execution_supported': False}, ensure_ascii=True), file=sys.stderr)
        return 2
