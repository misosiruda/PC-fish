# PC FISH 내부 구조 및 백그라운드 자동화 조사

조사일: 2026-11-06 (Asia/Seoul)

## 결론과 사용자 요구

사용자는 PC FISH를 켜 둔 상태에서 다른 작업을 하므로 실제 마우스·키보드 입력을 사용하는 자동화를 원하지 않는다. 설치된 게임을 읽기 전용으로 분석한 결과, 게임 프로세스 안에서 Unity 메인 스레드의 정상 게임 함수를 호출하는 모드가 유력한 구현 경로다. 함수와 상태 모델은 확인했지만, 모드 로더 설치·호환성 검증·실제 자동 교배는 아직 하지 않았다.

이번 요청은 우선 실행 중인 게임을 찾아 내부를 조사하는 범위다. 현재 게임을 재시작하거나 설치 파일·저장 파일을 수정하지 않았다. 게임 입력, 프로세스 메모리 접근, 주입, 서비스 API 직접 호출도 하지 않았다.

## 현재 설치와 관찰

| 항목 | 확인값 | 근거 |
| --- | --- | --- |
| 게임 | PC FISH: Taskbar Aquarium / PCFish.exe | Steam manifest, 실행 프로세스, 창 제목 |
| Steam App ID | 4842670 | appmanifest_4842670.acf |
| Steam build ID | 26315872 | 동일 manifest |
| 게임 버전 | 1.6.2 | PlayerSettings.bundleVersion, 부분 파서 결과 |
| 엔진 | Unity 6000.0.76f1 | Player.log, 실행 파일 버전 |
| 실행 방식 | Windows x64 IL2CPP, metadata v31 | GameAssembly.dll, 메타데이터, Il2CppDumper |
| 설치 경로 | C:\Program Files (x86)\Steam\steamapps\common\PC FISH | 실행 프로세스 경로 |
| 로컬 데이터 | %USERPROFILE%\AppData\LocalLow\NANOO COMPANY Inc_\PCFish | Player.log, SaveFile.es3, Setting.es3 |
| 백그라운드 설정 | runInBackground=true, visibleInBackground=true | PlayerSettings, 아래 파서 한계 참조 |

`agent-desktop`으로 실행 창과 스크린샷을 확인했다. 처음 관찰했을 때 설정 창이 열려 있었다. Unity의 게임 내부 버튼은 UI Automation에 노출되지 않아 접근성 참조 수가 0이었다. 이는 화면 입력을 사용하지 않는 사용자 요구와 별개로, 접근성 버튼 클릭 방식이 적합하지 않음을 보여 준다.

저장 파일은 바이너리 ES3 형식으로 관찰되었다. 저장 내용을 복호화하거나 현재 보유 물고기·실시간 교배 횟수를 읽지는 않았다.

## 확인한 게임 규칙

아래 값은 현재 설치된 바이너리의 `GameConfig` 정적 초기화 코드에서 읽었다. 실행 중인 상태를 직접 읽은 값이나 서버의 규칙 보장값은 아니다.

| 설정 | 값 | 해석 및 확인 범위 |
| --- | --- | --- |
| CF_BREED_MAX_COUNT | 5 | 교배 횟수 보유 상한 |
| CF_BREED_COUNT_INTERVAL | 420.0 | 회복 간격 420초, 즉 7분. BreedTimer가 이 값과 경과 시간을 비교 |
| CF_FISH_PARENT_COOLTIME | 3.0 | 기본 부모 교배 대기 3분. 서버 결과 처리의 비기본종 분기에서 DateTime.AddMinutes에 전달 |
| CF_FISH_CHILD_COOLTIME | 7.0 | 이름상 자식 대기 설정. 이번 조사에서는 값만 확인했으며 적용 경로는 별도 검증 필요 |
| CF_FISH_EAT_COOLTIME | 6.0 | 먹이 섭취 쿨타임 설정 |
| CF_FISH_GROWTH_MID / MAX | 5 / 10 | 성장 단계 기준 |
| CF_SPAWN_PLANKTON_BASIC_COUNT / COUNT | 3 / 10 | 기본 생성 수 및 동시 개수 관련 설정. AddPlankton이 listPlankton.Count와 COUNT를 비교 |
| CF_FISH_MAX_COUNT | 999 | 교배 전 보유 수 검사에 사용되는 상한 |
| CF_TANK_MAX_LEVEL | 10 | 수조 최대 레벨 설정 |
| CF_SAVE_REQUEST_INTERVAL | 60.0 | 저장 요청 간격 설정 |

`GameManager.Update`는 수조 레벨별로 플랑크톤을 자동 생성한다. 네이티브 코드상 간격은 수조 레벨 1–2에서 6초, 3–5에서 3초, 6–9에서 2초, 10 이상에서 1.5초다. `Fish`는 플랑크톤 탐색·추적·섭취를 자체 수행한다. 현재 확인한 코드에는 기본 물고기의 성장 처리가 있으며, 플랑크톤 공급 자체를 수동 작업으로 전제할 근거는 없다.

따라서 몇 분마다 반복되는 조작의 주요 자동화 후보는 교배 횟수 사용과 부모 선택이다. 사용자가 실제로 반복하는 조작의 전부가 교배인지는 이번 조사에서 단정하지 않는다.

## 자동화에 사용할 수 있는 상태와 함수

| 역할 | 상태 / 함수 | 의미 |
| --- | --- | --- |
| 보유 상태 | GameDataManager.fishList, breedCount, breedTimestamp | 물고기 목록, 교배 횟수와 회복 기준 시각 |
| 물고기 조건 | FishModel.Growth, BreedCount, BreedMaxCount, BreedNextDatetime, IsLocked, IsPlaced | 성숙도, 남은 번식 조건, 대기시간, 보호·배치 상태 확인에 사용 가능 |
| 교배 진입 | UIBreed.SetParent(string), UIBreed.Breed() | 부모 선택과 정상 UI 동작의 내부 함수 |
| 교배 요청 | NetworkManager.FishBreed(string[], callback) | 부모 ID를 보내 서버 결과를 받는 기존 게임 경로 |
| 결과 적용 | NetworkManager의 FishBreed 콜백, NewFish, GameDataManager.SaveData | 새 물고기와 부모 상태 갱신, 저장 |
| 배치 | FishUtil.Place(FishModel) | 물고기 배치 경로 후보 |
| 수조 진행 | GameDataManager.IsCheckTankLevelUp(), UpgradeTankLevel() | 수조 레벨 진행 후보 |
| 합성 | UIMerge / NetworkManager.FishMerge | 일반 합성, 별도 재료 선택 정책 필요 |
| 시즌 제작 | NetworkManager.FishCraft | 시즌 재료·등급·보호 정책 필요 |

확인된 주요 RVA는 `UIBreed.Breed=0x60D2F0`, `NetworkManager.FishBreed=0x61FA10`, `GameManager.BreedTimer=0x6193C0`, `GameManager.Update=0x61D070`이다. 이 주소는 해당 빌드의 정적 분석 근거이며, 다른 빌드에 재사용할 고정 주소로 취급하면 안 된다. 구현에서는 가능하면 IL2CPP 타입·메서드 조회를 사용한다.

`UIBreed.Breed()`의 네이티브 코드에서 보유 수, 부모 선택, 교배 횟수 검사를 거쳐 `NetworkManager.FishBreed`로 연결되는 것을 확인했다. 다만 이 UI 함수는 비활성 UI의 내부 참조도 사용하므로, 창을 열지 않고 호출해도 되는지 아직 검증되지 않았다. 내부 함수를 찾은 사실만으로 곧바로 무인 실행이 보장되지는 않는다.

## 로컬 처리와 서버 처리

플랑크톤 섭취와 기본 물고기 성장 처리는 로컬 `Fish.GrowthUp` 및 `CheckBasicFishGrowth`에서 진행된다. `Fish.GrowthUp`에서 성장도를 올리고 먹이 쿨타임 코루틴을 다시 시작하는 흐름을 확인했다.

교배·합성·시즌 제작은 게임의 NetworkManager가 서비스 요청을 보내고 결과를 적용한다. 로컬 저장값을 변경하는 방식은 정상 서버 결과 처리를 대신하지 못한다. 실제 자동화는 게임의 기존 요청·응답 처리를 유지하고, 완료 콜백을 기다린 뒤 다음 행동을 결정하는 구조가 적합하다. 이번 조사에서는 서비스 요청을 직접 보내지 않았다.

## 권장 구현 방향

1. Windows x64 IL2CPP용 모드 로더를 적용하고, 처음에는 상태만 기록하는 플러그인으로 Unity 6000.0.76f1 및 metadata v31 호환성을 검증한다.
2. Unity 메인 스레드에서 저빈도 상태 확인을 수행한다. 마우스 이동, SendInput, 단축키 전송, 창 활성화는 사용하지 않는다.
3. 교배 횟수, 부모 2마리의 성장·쿨타임·교배 가능 횟수, 보유 상한, 요청 진행 상태를 확인한다.
4. 정상 교배 경로를 한 번 실행하고, 결과 콜백과 데이터 반영을 확인한다. 오류·타임아웃에서 같은 작업을 바로 중복 실행하지 않는다.
5. 실행·일시정지는 별도 설정이나 로컬 제어 수단으로 제공한다. 합성·시즌 재료 소비·보호 물고기 처리는 사용자 정책이 정해진 뒤 확장한다.

우선 후보는 BepInEx의 `Unity.IL2CPP-win-x64` 계열이다. 공식 문서는 IL2CPP용 Bleeding Edge 빌드를 안내한다. 이 게임에서 실제 로더 호환성을 확인한 상태는 아니다.

일반 백그라운드 동작을 뒷받침하는 추가 근거로, `TransparentManager.SetDormant`는 휴면 전환 때 프레임 상한을 10으로 낮추고 복귀 때 이전 값을 복원한다. 해당 함수에서는 Time.timeScale 변경을 찾지 못했다. 다른 창에 가린 경우·전체 화면 앱 실행·최소화 상태에서 타이머와 모드 콜백이 실제 계속 동작하는지는 실행 검증이 필요하다.

## 수행한 검증과 한계

- Il2CppDumper 6.7.46으로 메타데이터 v31 및 GameAssembly.dll을 분석했다. 타입/함수 덤프, 네이티브 분석용 인덱스와 DummyDll 생성을 종료 코드 0으로 완료했다.
- 게임 어셈블리의 625개 타입을 분리했고, Unity 에셋 6개 파일에서 데이터 테이블 등 TextAsset 28개를 추출했다. 포함된 과거 버전 테이블을 모두 추출했으므로 파일이 존재한다는 사실을 현재 활성 테이블이라는 뜻으로 해석하면 안 된다.
- 선택한 21개 네이티브 함수의 기계어와 직접 호출 관계를 읽었다. 공유된 제네릭 코드의 덤퍼 이름은 별칭일 수 있어, 개별 게임 함수와 필드 오프셋을 함께 확인했다.
- PlayerSettings 파서는 파일 크기 936바이트에 대해 932바이트를 해석하여 엄격 검사에서 4바이트 차이를 보고했다. `check_read=False`의 부분 해석으로 버전과 백그라운드 설정을 읽었으며, 전체 형식 호환성이 검증됐다는 의미는 아니다.
- 로더 설치·게임 재시작·실행 중 메모리 조작·자동 교배·합성은 수행하지 않았다. 계정별 실제 현재 상태와 서버의 허용 여부도 시험하지 않았다.
- 제품 코드 변경이 없는 조사 작업이므로 제품 런타임 테스트나 전체 테스트 스위트는 실행하지 않았다. 분석 스크립트는 실제 설치 파일로 실행해 산출물 생성과 구문 검사를 확인했다.

최초 덤퍼 실행은 산출물 생성 후 콘솔의 키 입력 대기에서 예외를 냈다. `RequireAnyKey=false`로 설정하고 별도 출력 폴더에 다시 실행하여 성공을 확인했다.

## 산출물

- `work/inspection-20261106/pcfish-initial.png`: 최초 게임 화면
- `work/inspection-20261106/game-signatures.cs`: 해당 게임 어셈블리 타입·필드·함수 시그니처
- `work/inspection-20261106/native-analysis.txt`: 선택 함수의 정적 디스어셈블리
- `work/inspection-20261106/native-call-map.json`: 직접 호출 관계
- `work/inspection-20261106/asset-inventory.json`, `text-assets/`: 에셋 인벤토리와 표 데이터
- `work/inspection-20261106/dump/`: 원본 분석 산출물, 메서드 본문이 복원된 C# 소스가 아님
- `work/inspection-20261106/inspect_assets.py`, `inspect_native.py`: 읽기 전용 재현 스크립트
- `work/inspection-20261106/inspection-manifest.json`: 설치 식별자, 해시, 도구 버전, 검증 범위

분석 도구는 `tools/` 아래에만 두었다. 덤프와 로컬 게임 에셋을 프로젝트 밖으로 전송하거나 게시하지 않았다.

## 공식 참고 자료

- [PC FISH Steam 상점](https://store.steampowered.com/app/4842670/PC_FISH_Taskbar_Aquarium/)
- [BepInEx IL2CPP 설치 문서](https://docs.bepinex.dev/master/articles/user_guide/installation/unity_il2cpp.html)
- [Il2CppDumper 공식 저장소](https://github.com/Perfare/Il2CppDumper)
