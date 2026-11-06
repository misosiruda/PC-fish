# PC FISH 자동화 모드와 계획 알고리즘

게임 내부 기능을 이용하는 Windows IL2CPP 자동화 모드를 추가했다. 마우스·키보드 입력 없이 교배와 소진 재료 10마리 합성을 수행한다. 사용·보호 정책·설치·제거 방법은 [자동화 모드 안내](docs/automation-mod.md)를 참고한다.

0.2.0부터 경험치 조건을 충족한 수조 레벨업도 자동 처리한다. 기본 설정 `auto_level_up=true`로 게임 실행 시 함께 시작하며, 기존 게임의 보상 지급·수조 확장 기능을 사용한다. 이미 열린 레벨업 완료 팝업도 정상 확인 함수로 닫는다.

0.2.1에서는 닫힌 메뉴의 마지막 탭 값 때문에 수조에서 자동화가 멈추던 문제를 수정했다. 실제 열린 창과 통신·확인창만 대기 조건으로 사용하며, 창을 닫으면 자동으로 재개한다.

0.2.2에서는 자동 교배 후 바깥 하트 그림이 사용 전 수치로 남는 문제를 수정했다. 정상 게임 표시 갱신 함수를 호출해 데이터와 표시를 맞추며, 회복·소비 변화도 반영한다.

```powershell
& 'C:\Project\PC-fish\scripts\Set-Automation.ps1' -Mode Protected
& 'C:\Project\PC-fish\scripts\Set-Automation.ps1' -Mode Pause
& 'C:\Project\PC-fish\scripts\Set-Automation.ps1' -Mode Status
```

기본 보호 모드는 일반 1~2성만 사용하며 시세 수익을 보장하지 않는다. 실제 시세·공동 분포를 갖춘 경우 아래 독립 계획기를 실행 어댑터에 연결하는 `Economic` 모드를 사용할 수 있다.

## 공개 저장소에서 시작하기

Python 3.12 이상을 설치한 뒤 PowerShell에서 실행한다. 아래 명령은 게임에 연결하지 않고 합성 데이터로 독립 계획기를 실행한다.

```powershell
git clone https://github.com/misosiruda/PC-fish.git
Set-Location PC-fish
python -m venv .venv
& '.\.venv\Scripts\python.exe' -m pip install -e .
& '.\run-planner.ps1'
```

공개본에는 직접 작성한 Python/C# 소스, 테스트, 문서, 합성 예제와 설치 스크립트를 포함한다. 게임 파일·추출 에셋·어셈블리 덤프·세이브·개인별 실행 기록, `tools/`의 외부 도구와 `build/`·`dist/`의 빌드 결과물은 포함하지 않는다. 조사 문서의 `work/` 경로는 로컬 보관 자료를 가리키며 공개 저장소에서 열 수 없다.

게임 연결 모드는 별도로 BepInEx와 .NET SDK를 준비해야 한다. [자동화 모드의 빌드·설치 안내](docs/automation-mod.md#빌드설치제거)를 따른다. `mod/pcfish-automation.json`의 `python_path`와 `project_root`는 설치 예시이므로 자신의 Python 실행 파일과 실제 저장소 경로로 바꾼 뒤 설치한다. 기본 설정은 Protected 자동 실행이다.

Python 전체 테스트는 게임 없이 실행할 수 있다.

```powershell
$env:PYTHONPATH = Join-Path (Get-Location) 'src'
& '.\.venv\Scripts\python.exe' -m unittest discover -s tests -v
```

## 독립 계획 알고리즘

교배·대기·일반 합성·보존을 비교하는 독립 Python 계획기다. 게임 내부 상태와 가격/결과 모델을 JSON으로 입력하면 **다음 행동 한 개**와 판단 근거를 JSON으로 출력한다. 게임 프로세스·화면 입력·Steam 주문에 접근하는 실행 기능은 없다.

## 실행

Python 3.12 이상이면 외부 패키지 설치 없이 실행할 수 있다. 이 PC에서는 다음 PowerShell 명령으로 기본 예제를 실행한다.

```powershell
& 'C:\Project\PC-fish\run-planner.ps1'

& 'C:\Project\PC-fish\run-planner.ps1' `
    -InputPath 'C:\Project\PC-fish\examples\merge-low-value.json' `
    -OutputPath 'C:\Project\PC-fish\work\planner\merge-result.json'
```

다른 Python을 사용하려면 `-PythonPath`로 실행 파일을 지정한다. 모듈로 직접 실행할 수도 있다.

```powershell
$env:PYTHONPATH = 'C:\Project\PC-fish\src'
python -m pcfish_planner plan 'C:\Project\PC-fish\examples\wait-better-pair.json'
python -m unittest discover -s tests -v
```

`python` 명령은 실제 Python 설치를 가리켜야 한다. 이 컴퓨터의 WindowsApps 별칭은 Python 실행 환경이 아니어서 PowerShell 래퍼는 확인된 번들 런타임을 우선 사용한다.

## 포함된 예제

예제의 물고기 목록, 가격과 어종/Grade 공동 분포는 모두 **합성 데이터**다. 대기 예제의 Level 주변 확률만 조사한 클라이언트 표를 사용한다. 이 예제로 실제 판매 수익을 예측하면 안 된다.

| 파일 | 기대 판단 |
| --- | --- |
| `examples/wait-better-pair.json` | 120초 뒤 좋은 부모가 준비될 때까지 대기 |
| `examples/merge-low-value.json` | 저가 고갈 재료 10마리 합성 |
| `examples/preserve-high-value.json` | 5성 고가 재료 보존 |
| `examples/missing-outcome-model.json` | 공동 결과 분포가 없어 판단 보류 |

## 알고리즘

1. 부모 조합과 모델이 있는 고갈 재료 10마리 조합을 만든다.
2. 다음 부모 준비 시각과 하트 회복 시각을 대기 후보로 만든다.
3. 각 행동 결과를 입력된 공동 확률로 분기한다. 부모 횟수, 준비 시각, 하트, 보유 수를 갱신한다.
4. 같은 계획 종료 시점까지 기대 재고 가치 변화를 비교한다. 기본 범위는 30분, 실제 상태를 바꾸는 행동 최대 3회다.
5. 가장 높은 양의 기대값을 가진 다음 행동만 반환한다. 실제 결과가 오면 새 상태로 다시 계획해야 한다.

가치는 `수수료 후 판매 금액 × 입력된 기간 내 판매 확률`로 평가한다. 교배에서는 부모의 사용 전후 가치 차이를 비용으로 차감하고, 합성에서는 재료 10마리의 전체 가치를 차감한다. 이는 현재 시세를 고정한 기대 재고 가치 변화이며 실현된 매출이 아니다. 거래량만으로 개인의 판매 확률을 자동 추정하지 않는다.

구현은 사건 시점에서 탐색하는 기대값 탐색이다. 후보·노드·대기 사건 수를 제한하므로 전역 최적을 보장하지 않는다. 제한에 걸리면 출력의 `search.limits`에 표시한다. Grade 5 재료, 잠금, 예약 재료, 시즌 재료는 기본적으로 일반 합성에서 보호한다. 합성 잠금은 교배 잠금과 별개다.

하트 타이머는 상한에서 회복을 멈춘다. 추가로 조사한 정상 교배 성공 콜백은 하트가 가득 찬 경우 소비 직전에 회복 기준 시각을 초기화한다. 계획기와 실행 어댑터 모두 이 처리를 반영하므로 상한에서 소비한 하트가 즉시 재충전되는 것으로 계산하지 않는다.

## 입력 계약

전체 구조와 필드는 예제 JSON을 참고한다. 자세한 의미는 [입력 및 판단 계약](docs/planner-contract.md)에 있다.

- `state`: 물고기 ID/어종/Level/Grade/남은 횟수/준비 시각, 현재 하트와 다음 회복 시각.
- `quotes`: 어종·Grade·잔여 횟수별 **수수료 후** 판매 금액, 판매 확률, 수집 시각, 근거. 한 통화만 사용한다.
- `rules`: 입력 부모의 특성 조합에 대해 결과 어종·Level·Grade·잔여 횟수·준비시간이 함께 정의된 공동 분포. 확률 합은 1이어야 한다.
- `config`: 계획 범위와 예산, 보호 조건, 시세 유효기간 등을 지정한다.

별 등급 확률을 어종 희귀도 표에서 만들지 않는다. 결과 하나라도 가격이 없으면 해당 행동 전체를 평가에서 제외한다. 시세가 없는 개체를 0원으로 취급하지 않는다. 변경되지 않는 다른 인벤토리의 미확인 가격은 별개의 평가 가능한 행동을 막지 않는다.

`assumption`/`synthetic` 근거는 기본 비활성이고, `allow_assumptions=true`에서만 사용한다. 출력은 이 경우 `simulation`으로 표시한다. 실제로 관측한 규칙도 기본 30개 표본 미만이면 사용하지 않는다. 30개는 초기 정책값이며 충분한 정확도를 보장하는 통계적 기준은 아니다.

기본종의 무한 교배는 `unlimited_breeding=true`로 명시해야 한다. 기본종은 교배 후 성장 회복 시간이 필요하므로 `breed_recovery_seconds`를 별도로 제공해야 교배 후보로 평가한다. 일반 부모의 기본 대기시간은 조사한 설정값인 180초이며 개체별로 재정의할 수 있다.

## 실제 결과로 분포 갱신

완료가 확인된 이벤트만 JSONL로 기록하면 빈도 기반 공동 분포를 생성할 수 있다.

```powershell
& 'C:\Project\PC-fish\run-planner.ps1' -Mode learn `
    -InputPath 'C:\Project\PC-fish\examples\confirmed-observations.jsonl' `
    -OutputPath 'C:\Project\PC-fish\work\planner\learned-rules.json'
```

중복 이벤트 ID는 한 번만 세고, 같은 ID의 결과가 충돌하면 실패한다. 생성 결과의 `rules`를 계획 입력에 넣으면 된다. 입력 예시는 합성 데이터이며 실제 로그가 아니다. `learn`은 새 이벤트를 자동으로 수집하지 않는다. 부모 순서가 바뀌어도 분포가 같다고 가정해 묶으므로 이 가정도 실제 데이터로 검증해야 한다.

## 현재 검증 범위

단위·시나리오·CLI·실행 브리지 테스트를 포함한 전체 Python 테스트와 C# 실행 보호 테스트를 실행한다. 게임 연결 검증 범위는 [자동화 모드 안내](docs/automation-mod.md)에 기록한다. Grade 분포 검증과 주문·판매 실행은 포함하지 않는다. 기존 조사 자료와 도구는 `docs/`, `work/`, `tools/`에 보존되어 있다.
