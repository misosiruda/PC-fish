# PC FISH Automation

PC FISH의 교배·일반 합성·시즌 제작·수조 레벨업을 자동으로 진행하는 Windows 모드다. 게임 내부 상태와 기존 게임 기능을 사용하며, 마우스·키보드 입력이나 포커스 전환 없이 백그라운드에서 작동한다. 게임을 실행해 둔 동안에만 진행한다.

**현재 공개 버전: 0.4.32** · [다운로드와 릴리즈 노트](https://github.com/misosiruda/PC-fish/releases/tag/v0.4.32) · [변경 이력](CHANGELOG.md) · [설치·업데이트](docs/installation.md)

## 주요 기능

| 기능 | 동작 |
| --- | --- |
| 자동 교배 | 부모의 준비 상태·남은 횟수·하트·보유 한도를 확인하고 교배 또는 대기를 선택 |
| 일반 합성 | 같은 별 등급, 희귀도 차이 최대 1인 일반 물고기 10마리를 사용. 같은 희귀도 묶음을 우선하고 클라이언트 예측상 희귀도 하락이 가능한 조합은 제외 |
| 시세 보호 | 같은 어종·별 등급의 최고 매수 주문에서 수수료를 뺀 수령액으로 소비 여부를 판단 |
| 시즌 수집 | 현재 시즌의 레시피와 보유 수량을 읽어 재료를 예약하고, 기본 목표인 어종별 1~5성 각 1마리를 수집 |
| 수조 레벨업 | 게임의 경험치 조건을 만족하면 기존 레벨업·보상 기능을 실행 |
| 백그라운드 실행 | 게임 시작 시 함께 실행. 메뉴·목록·교배 성공 화면을 열어둬도 진행하며, 실제 통신·확인창·미확정 요청 중에는 대기 |

자동 판매·구매·출품은 하지 않는다. 게임 자원이나 결과 확률을 임의로 늘리지 않으며, 시세 보호는 수익을 보장하는 기능이 아니다.

## 다운로드와 설치

지원 대상은 **Windows x64 / PC FISH 1.6.2 / Steam build 26315872**다. 설치 스크립트와 플러그인은 검증한 `GameAssembly.dll`의 SHA-256을 확인한다. 게임 업데이트로 파일이 바뀌면 새 호환 버전이 필요하다.

1. [0.4.32 릴리즈](https://github.com/misosiruda/PC-fish/releases/tag/v0.4.32)의 **`PCFish-Automation-v0.4.32-win-x64.zip`**을 내려받아 계속 사용할 폴더에 푼다.
2. [설치 안내](docs/installation.md)에 따라 Python 3.12 이상, Google Chrome, BepInEx IL2CPP 로더를 준비한다.
3. Python 환경과 설정 경로를 맞춘 뒤 게임을 종료하고 모드를 설치한다. **새 설치의 기본값은 Protected 자동 실행**이며, 교배·합성·시즌 제작·레벨업이 켜져 있다.

배포 ZIP에는 빌드된 모드 DLL, Python 소스, 설정 예시, 설치 스크립트와 문서가 들어 있다. BepInEx·Python·Chrome·게임 파일은 별도로 준비한다. GitHub의 **Source code** ZIP/TAR에는 빌드된 DLL이 없으므로 직접 빌드할 때 사용한다.

기존 사용자는 게임을 정상 종료한 뒤 기존 프로젝트 폴더에 새 ZIP을 덮어 풀고 Python 의존성과 DLL을 업데이트한다. 기존 설정은 자동으로 덮어쓰지 않는다. 자세한 순서는 [업데이트 안내](docs/installation.md#기존-버전에서-업데이트)에 있다.

## 켜기·일시정지·상태 확인

아래 명령은 압축을 푼 프로젝트 폴더의 PowerShell 7에서 실행한다. 게임 경로가 기본 Steam 설치 위치와 다르면 `-GameRoot '실제 게임 경로'`를 붙인다.

```powershell
# 현재 상태
& '.\scripts\Set-Automation.ps1' -Mode Status

# 새 작업 일시정지
& '.\scripts\Set-Automation.ps1' -Mode Pause

# 현재 기본 정책으로 실행
& '.\scripts\Set-Automation.ps1' -Mode Protected -Protection Market -MergePriceBasis HighestBuyOrder
```

설정은 재시작 후에도 유지된다. `Pause`는 새 작업을 막고 이미 보낸 요청의 결과는 계속 확인한다. 자동 교배가 게임 교배 화면의 부모 선택을 바꿀 수 있으므로 직접 조작할 때는 먼저 일시정지한다.

## 현재 보호 정책

- **시세:** 최고 매수 주문의 수수료 후 수령액이 **1,000원 이하**이면 다른 조건을 확인해 재료 후보로 허용하고, 초과이면 보호한다. 명시적으로 매수 주문 0건을 확인한 경우의 즉시 판매 가치는 0원이다. 조회 실패·누락·만료는 임시 보호한다.
- **잠금과 예약:** 사용자 잠금·수동 예약·시즌 수집품을 보존한다. 자동 시즌 예약한 일반 재료는 일반 합성에서 제외하지만 교배 조건을 만족하면 부모로 사용할 수 있다. 이미 잠근 개체는 시세가 내려가도 자동으로 잠금을 풀지 않는다.
- **시즌 우선순위:** 도깨비불 해파리 → 누더기 프랑켄피시 → 호박 도깨비아귀 순서로 한 어종의 수집 목표에 집중한다. 후순위 어종의 일반 재료를 미리 묶어 두지 않는다.
- **미등재 신규 일반종:** 같은 희귀도·별 등급의 기존 일반종 최고 매수 주문 수령액 중 최댓값으로 추정한다. 실측 가격과 구분하며, 매물이 관측된 상품의 주문 조회 실패를 미등재로 취급하지 않는다.
- **중복 소비 방지:** 실행 직전에 상태를 다시 검사하고, 결과가 불명확한 소비 요청을 그대로 재전송하지 않는다.

시세 조회는 개인 Chrome 프로필·로그인·API 키 없이 별도 숨김 Chrome으로 진행한다. 어종·별 등급별 자료는 24시간 공유하며 상품 간 기본 조회 간격은 120초다. 서버 제한 때는 대기한다. 상세 조건은 [자동화 안내](docs/automation-mod.md), 시즌 재료는 [시즌 제작 안내](docs/season-crafting.md)를 참고한다.

## 게임 없이 계획기 실행

독립 계획기는 JSON으로 받은 상태·가격·결과 분포에서 다음 행동 한 개와 근거를 출력한다. 아래 예제는 합성 데이터이며 게임이나 Steam 주문을 변경하지 않는다.

```powershell
python -m venv .venv
& '.\.venv\Scripts\python.exe' -m pip install -e .
& '.\run-planner.ps1'
& '.\run-planner.ps1' -InputPath '.\examples\merge-low-value.json'
```

| 예제 | 예상 판단 |
| --- | --- |
| `examples/wait-better-pair.json` | 120초 뒤 더 좋은 부모가 준비될 때까지 대기 |
| `examples/merge-low-value.json` | 저가 재료 10마리 합성 |
| `examples/preserve-high-value.json` | 5성 고가 재료 보존 |
| `examples/missing-outcome-model.json` | 공동 결과 분포가 없어 판단 보류 |

Protected 모드는 보호 정책 안에서 진행을 선택한다. Economic 모드는 별도로 준비한 가격과 어종·별 등급 공동 결과 모델이 필요하며, 입력이 부족하면 임의의 수익성 가정으로 대체하지 않는다. [계획기 계약](docs/planner-contract.md)과 [전략 설명](docs/automation-strategy.md)을 참고한다.

## 개발과 검증

소스 빌드에는 .NET 8 SDK와 해당 게임에서 생성한 BepInEx interop 참조가 추가로 필요하다. [소스 빌드 안내](docs/installation.md#소스에서-빌드)를 따른다.

```powershell
$env:PYTHONPATH = Join-Path (Get-Location) 'src'
& '.\.venv\Scripts\python.exe' -m unittest discover -s tests -v
dotnet run --project tests/ModGuard/ModGuard.csproj -c Release
& '.\scripts\Build-Mod.ps1'
```

공개 저장소와 배포 ZIP에는 게임 파일·추출 에셋·interop DLL·세이브·개인별 실행 기록을 포함하지 않는다. [개발·검증 이력](docs/automation-history.md)의 `work/` 경로는 로컬 기록이며 배포 파일이 아니다. 과거 실사용 결과와 이번 릴리즈 검증 범위는 구분해 기록한다.

문제는 [GitHub Issues](https://github.com/misosiruda/PC-fish/issues)에 모드 버전, 게임 버전, 재현 순서와 오류 메시지를 남긴다. 계정 정보·쿠키·전체 세이브는 첨부하지 않는다.
