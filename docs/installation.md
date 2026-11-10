# 설치·업데이트·제거

이 안내는 **PC FISH Automation 0.4.32 / Windows x64** 기준이다. PowerShell 명령은 **PowerShell 7**에서 실행한다.

## 지원 환경과 다운로드

| 구성 | 요구 사항 |
| --- | --- |
| 게임 | PC FISH 1.6.2, Steam build 26315872, Unity 6000.0.76f1 IL2CPP x64 |
| Python | 3.12 이상. 프로젝트 폴더의 `.venv` 권장 |
| 시세 조회 | Google Chrome, Python의 Selenium 자동화 의존성 |
| 로더 | BepInEx `6.0.0-be.793+dbc82f0`, **Unity.IL2CPP-win-x64** |
| 명령 실행 | PowerShell 7 |
| 소스 빌드만 | .NET 8 SDK, 해당 게임에서 생성한 BepInEx interop 참조 |

[GitHub 릴리즈](https://github.com/misosiruda/PC-fish/releases/tag/v0.4.32)에서 `PCFish-Automation-v0.4.32-win-x64.zip`과 `SHA256SUMS.txt`를 받는다. ZIP에는 빌드된 DLL과 계획기 소스가 함께 들어 있다. DLL만 복사하면 Python 계획기가 실행되지 않는다.

다운로드한 ZIP의 체크섬을 `SHA256SUMS.txt`와 비교한다.

```powershell
Get-FileHash '.\PCFish-Automation-v0.4.32-win-x64.zip' -Algorithm SHA256
```

Python은 [공식 다운로드](https://www.python.org/downloads/windows/), Chrome은 [공식 다운로드](https://www.google.com/chrome/), PowerShell은 [Microsoft 설치 안내](https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows)를 따른다. BepInEx는 [공식 빌드 목록](https://builds.bepinex.dev/projects/bepinex_be)의 **#793 / Unity.IL2CPP-win-x64**를 사용한다. Mono·x86용을 선택하지 않는다. 로더 초기 실행 과정은 [공식 IL2CPP 설치 안내](https://docs.bepinex.dev/master/articles/user_guide/installation/unity_il2cpp.html)에도 나와 있다.

## 처음 설치

1. 모드 ZIP을 계속 사용할 폴더에 푼다. 아래 예시는 `C:\Games\PCFish-Automation`이다. 이 폴더는 설치 후에도 Python 계획기가 읽으므로 삭제하거나 이동하지 않는다.
2. PowerShell 7에서 해당 폴더로 이동하고 Python 환경을 만든다.

```powershell
Set-Location 'C:\Games\PCFish-Automation'
python -m venv .venv
& '.\.venv\Scripts\python.exe' -m pip install -e '.[automation]'
```

3. BepInEx #793 ZIP을 프로젝트의 `tools\BepInEx-IL2CPP-be793`에 푼다. 그 폴더 바로 아래에 `BepInEx` 폴더와 `winhttp.dll` 등 로더 파일이 있어야 한다.
4. Steam에서 게임을 정상 종료한 뒤 로더를 설치한다. 처음 모드를 쓰는 경우 게임 세이브도 별도로 백업한다. 설치 스크립트는 발견한 `SaveFile.es3`·`Setting.es3`의 초기 백업과 설치 파일 기록을 남긴다.

```powershell
$fishGameRoot = 'C:\Program Files (x86)\Steam\steamapps\common\PC FISH'
& '.\scripts\Install-Mod.ps1' -LoaderOnly -GameRoot $fishGameRoot
```

5. Steam에서 게임을 한 번 실행하고 BepInEx 초기 준비가 끝나면 정상 종료한다. 게임 폴더에 `BepInEx\interop`가 생성됐는지 확인한다.
6. 설정 예시의 두 실행 경로를 실제 압축 해제 폴더에 맞춘다.

```powershell
$fishConfig = Get-Content '.\mod\pcfish-automation.json' -Raw | ConvertFrom-Json
$fishConfig.project_root = (Get-Location).Path
$fishConfig.python_path = (Resolve-Path '.\.venv\Scripts\python.exe').Path
$fishConfig | ConvertTo-Json -Depth 10 | Set-Content '.\mod\pcfish-automation.json' -Encoding utf8NoBOM
```

**새 설정은 `enabled=true`, `mode=protected`이며 교배·합성·시즌 제작·레벨업이 켜져 있다.** 먼저 상태만 확인하려면 설치 전에 `$fishConfig.enabled = $false`를 넣고 저장한다. 기본 시세 정책은 최고 매수 주문 수령액 1,000원 이하 허용, 시즌 목표는 어종별 1~5성 각 1마리다.

7. 빌드된 플러그인을 설치하고 Steam에서 게임을 실행한다. 배포 ZIP 사용 시 직접 빌드할 필요가 없다.

```powershell
& '.\scripts\Install-Mod.ps1' -GameRoot $fishGameRoot
# 게임 실행 후:
& '.\scripts\Set-Automation.ps1' -Mode Status -GameRoot $fishGameRoot
```

플러그인은 `BepInEx\plugins\PCFish.Automation\PCFish.Automation.dll`에, 실제 설정은 `BepInEx\config\pcfish-automation.json`에 설치된다. 설정 파일이 이미 있으면 덮어쓰지 않으므로 이 경우 실제 설정의 두 경로를 직접 확인한다. 처음 시세를 조회할 때 Selenium Manager가 ChromeDriver를 준비할 수 있으며, 미확인 물고기는 조회가 끝날 때까지 보호한다.

## 기존 버전에서 업데이트

1. `Set-Automation.ps1 -Mode Pause`로 새 작업을 멈추고, 상태의 `in_flight=false`, `uncertain=false`를 확인한 뒤 게임을 정상 종료한다. 미확정 요청이 있으면 기록을 지우지 말고 [문제 확인](#문제-확인)을 따른다.
2. 기존 프로젝트 폴더와 설치된 DLL·설정을 별도로 백업한다. `var\mod-installation.json` 및 이 파일이 가리키는 백업 폴더를 보존한다.
3. 새 ZIP을 **기존 프로젝트 폴더에** 덮어 푼다. ZIP은 `.venv`, `var`, `work`, 게임 폴더의 실행 기록을 포함하지 않는다.
4. 아래 명령으로 Python 의존성과 DLL을 업데이트한다. 이미 설치된 로더는 다시 설치하지 않는다.

```powershell
& '.\.venv\Scripts\python.exe' -m pip install -e '.[automation]'
& '.\scripts\Install-Mod.ps1' -GameRoot $fishGameRoot
```

5. 게임의 `BepInEx\config\pcfish-automation.json`에서 `python_path`와 `project_root`를 확인한다. 프로젝트 위치를 바꿨다면 이 경로도 변경해야 한다. 기존 보호 정책·시즌 목표·잠금은 자동 변경하지 않는다.
6. 게임을 실행하고 `Set-Automation.ps1 -Mode Status`에서 `version=0.4.32`와 계획기 상태를 확인한다. 현재 기본 시세 정책으로 재개하려면 다음 명령을 사용한다.

```powershell
& '.\scripts\Set-Automation.ps1' -Mode Protected -Protection Market -MergePriceBasis HighestBuyOrder -GameRoot $fishGameRoot
```

`$fishGameRoot`는 처음 설치 예시처럼 실제 게임 경로로 먼저 지정한다. 시즌 목표까지 바꾸려면 [시즌 설정](season-crafting.md#목표-설정)을 참고한다. DLL 변경은 게임 재시작이 필요하고, `Set-Automation.ps1`의 정책 변경은 실행 중 반영된다.

## 제거와 되돌리기

게임을 종료한 뒤 설치에 사용한 프로젝트 폴더에서 실행한다.

```powershell
& '.\scripts\Remove-Mod.ps1'
```

제거는 `var\mod-installation.json`에 기록한 파일만 삭제하거나 설치 전 백업으로 복원한다. 이 프로젝트에서 로더도 설치했다면 기록된 로더 파일도 대상이다. 게임 진행 상황과 생성된 진단 기록은 되돌리지 않는다.

버전만 되돌릴 때는 게임을 종료하고 **업데이트 전에 백업한 프로젝트 소스·DLL·설정을 함께** 복원한다. 이미 서버에 반영된 교배·합성·시즌 제작은 코드 복원으로 취소되지 않는다. `BepInEx\PCFishAutomation`의 미확정 요청과 결과 기록을 임의로 삭제하지 않는다.

## 소스에서 빌드

GitHub Source code 또는 Git 복제본에는 DLL이 없다. 위 로더 설치와 첫 실행을 끝낸 뒤 .NET 8 SDK를 설치하고 프로젝트 루트에서 실행한다.

```powershell
& '.\scripts\Build-Mod.ps1' -GameRoot $fishGameRoot
& '.\scripts\Install-Mod.ps1' -GameRoot $fishGameRoot
```

빌드 스크립트는 `tools\dotnet\dotnet.exe`가 있으면 사용하고, 없으면 PATH의 `dotnet`을 사용한다. 출력은 `mod\PCFish.Automation\bin\Release\net6.0\PCFish.Automation.dll`이다. 소스와 버전이 확정된 깨끗한 Git 체크아웃에서는 `scripts\Build-Release.ps1`로 DLL·소스·체크섬·커밋 정보를 묶은 배포 ZIP을 만들 수 있다.

## 문제 확인

| 증상 | 확인할 항목 |
| --- | --- |
| 게임 빌드가 검증되지 않았다는 오류 | 현재 `GameAssembly.dll` 해시. 게임 업데이트 후에는 호환 버전 필요 |
| 모드가 로드되지 않음 | BepInEx IL2CPP x64 설치, `BepInEx\LogOutput.txt`, DLL 설치 경로 |
| 계획기가 시작되지 않음 | 실제 설정의 `python_path`, `project_root`, `.venv`와 Selenium 설치 |
| 시세 대기가 계속됨 | `market-review.json`, Chrome 설치, 서버 제한에 따른 대기. 자료가 없으면 소비하지 않음 |
| 미확정 요청으로 대기 | `status.json`의 메시지와 요청 기록. 재전송하거나 기록을 삭제하지 않고 원래 결과·복구를 확인 |

상태 파일은 게임의 `BepInEx\PCFishAutomation` 아래에 있다. 지원 요청은 [GitHub Issues](https://github.com/misosiruda/PC-fish/issues)에 버전·오류·재현 순서를 남긴다.

검증한 `GameAssembly.dll` SHA-256: `F5F98F82FE2FED5F177B440DF07C9084C814EB8C5C9A141661A67FF184149F93`.
