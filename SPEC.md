# OtogeTrainer — 제품 명세서 (SPEC)

- **버전**: v0.1 (기획 초안)
- **작성일**: 2026-09-15
- **상태**: 구현 착수 전 / 검토 요망
- **관련 문서**: [UI 청사진](docs/UI-BLUEPRINT.md) · [로드맵](docs/ROADMAP.md) · [결정 기록](docs/DECISIONS.md)

---

## 1. 개요

PC 리듬게임 플레이어가 **자신의 플레이 영상을 반복 재생하며 정밀하게 복기**하기 위한 Windows 전용 로컬 영상 뷰어.

모바일의 *Sound Game Training* 앱이 제공하는 "연습용 영상 플레이어" 개념을 PC 환경으로 옮기되, PC 고유의 제약(물리 키보드, 대형/멀티 모니터, 대량의 로컬 캡처 파일)에 맞춰 재설계한다.

### 1.1 일반 영상 플레이어와의 차별점

| 축 | 일반 플레이어 | OtogeTrainer |
|---|---|---|
| 탐색 단위 | 초 단위 | **프레임 단위가 기본**, 정확 시크(exact seek) 강제 |
| 상태 저장 | 마지막 위치 정도 | **영상별로** 북마크 · A/V 오프셋 · 배속 · 가이드까지 영구 저장 |
| 키보드 | 단축키 풍부 | **재생 중에는 앱 단축키를 받지 않음** (ESC 제외) |
| 목적 | 감상 | 구간 반복 연습, 타이밍/운지 확인 |

### 1.2 목표 (Goals)

- **G1.** 로컬 캡처 영상을 빠르게 열고, 프레임 정확도로 앞뒤 탐색할 수 있다.
- **G2.** 저배속(0.25x~)에서 오디오 피치가 깨지지 않고, 영상/소리 싱크가 유지된다.
- **G3.** 캡처 장비/인코딩에서 생긴 영상-소리 지연을 영상별로 보정하고 그 값을 기억한다.
- **G4.** 재생 중 키보드를 마음껏 두드려도 **앱이 반응하지 않는다.** 재생이 멈추거나 위치가 튀는 사고가 없다.
- **G5.** 어제 보던 영상을 두 번의 클릭 안에 같은 위치에서 다시 연다.

### 1.3 비목표 (Non-goals)

- 영상 편집 · 인코딩 · 자막 · 업로드
- 온라인 스트리밍(YouTube 등) 재생 — v1 제외, v2 이후 검토
- **게임 프로세스에 대한 오버레이/후킹** — 안티치트 오탐 위험, **영구 비목표**
- 자동 채보 인식 / 판정 자동 분석 — 장기 아이디어로만 기록
- macOS / Linux 지원

---

## 2. 사용자 및 사용 시나리오

### 2.1 페르소나

**"복기형 플레이어"** — 주 3~5회, 한 번에 20~60분. OBS나 캡처보드로 플레이를 녹화해 `D:\rec\` 같은 폴더에 수십~수백 개 파일을 쌓아둔다. 특정 곡의 특정 구간(예: 라스트 트릴)에서 계속 놓쳐서, 그 구간만 0.5배속으로 수십 번 돌려본다.

### 2.2 핵심 시나리오

**S1. 어제 못 넘긴 구간 다시 보기**
앱 실행 → 홈의 최근 목록 첫 항목이 어제 그 영상, 진행률 바와 함께 표시됨 → 클릭 → 마지막 위치 · 배속 · 오프셋이 그대로 복원된 채 일시정지 상태로 열림.

**S2. 구간 특정하기**
타임라인을 대충 드래그해 근처로 이동 → `←`/`→`로 5초(배속 연동) 단위 탐색 → 프레임 스텝으로 "노트가 판정선에 닿는 정확한 프레임"을 찾음 → 북마크 슬롯 `1`에 저장.

**S3. 반복 연습 (재생 중 단축키 차단)**
북마크 1로 점프 → A-B 구간 지정 → 0.5배속 재생 → 손을 건반에 올리고 영상에 맞춰 실제로 친다. `Space`를 눌러도 재생이 멈추지 않고, 방향키를 눌러도 위치가 튀지 않으며, 포커스가 버튼으로 옮겨가지도 않는다 → 감을 잡았으면 `ESC` → 일시정지, 이때부터 단축키가 다시 살아난다.

**S4. 싱크 보정**
캡처 파일 특성상 소리가 2프레임쯤 빠르게 들림 → 오프셋 패널에서 `+33ms` → 다음에 그 영상을 열면 자동 적용. 같은 폴더의 다른 파일도 같은 장비라면 폴더 기본값으로 상속.

---

## 3. 참고 앱 (Sound Game Training) 반영 방침

> **확정([D-1](docs/DECISIONS.md#d-1-참고-앱sound-game-training-반영-범위)): 개념만 차용하고 UI는 PC 맞춤으로 새로 설계한다.**
> 원본 앱을 1:1로 옮기지 않으므로, 아래는 방향성 참고용이다.

**차용할 개념**

- 영상 라이브러리 + "최근 본 영상" 중심의 홈
- 구간 반복(A-B 루프)과 저배속을 연습의 기본 루틴으로 전제
- 영상별 설정(오프셋 / 배속 / 북마크)의 영구 기억
- 재생 화면은 컨트롤을 최소 노출하고 영상에 집중

**PC라서 달라져야 하는 점**

- 물리 키보드 존재 → **재생 중 단축키 차단이 필수 기능으로 승격** (모바일에는 없는 요구)
- 파일이 수백 개 → 폴더 등록/스캔, 검색 · 태그 · 정렬 필요
- 마우스 정밀 조작 가능 → 타임라인 드래그, 휠 스텝, 사이드 버튼 활용
- 멀티 모니터 / 전체화면 → 화면 배치 · 항상 위 옵션

---

## 4. 기술 스택 결정

### 4.1 재생 엔진 비교

요구 기능 중 **역방향 프레임 스텝**과 **A/V 오프셋**이 엔진 선택을 사실상 결정한다.

| 후보 | 역방향 프레임 스텝 | 배속 + 피치 보정 | A/V 오프셋 | 정확 시크 | WPF 임베딩 | 판정 |
|---|---|---|---|---|---|---|
| WPF `MediaElement` (Media Foundation) | ❌ 불가 | △ `SpeedRatio`, 품질·정확도 낮음 | ❌ 없음 | △ 키프레임 단위 | ◎ 네이티브 | **탈락** |
| **libmpv** (mpv) | ✅ `frame-back-step` | ✅ `speed` + `audio-pitch-correction` | ✅ `audio-delay` 속성 | ✅ `seek … absolute+exact` | ○ HWND `--wid` 임베딩 | **채택** |
| LibVLCSharp (libvlc) | △ `NextFrame()` 정방향만, 역방향은 시크 에뮬레이션 | ✅ `SetRate` | ✅ `SetAudioDelay` | △ 정밀도 편차 | ◎ `VideoView` | **차선** |
| FFME (FFmpeg MediaElement) | ✅ 가능 | ✅ 가능 | △ 직접 구현 | ✅ | ◎ | 유지보수 리스크 |

### 4.2 결정

> **WPF (.NET 8, C#) + libmpv**, MVVM(CommunityToolkit.Mvvm) 구조.
> 재생 엔진은 `IMediaEngine` 인터페이스 뒤에 숨겨 **LibVLCSharp로 교체 가능**하게 설계한다.

**근거**

- `frame-back-step`을 네이티브로 제공하는 유일한 현실적 선택지. 역방향 프레임 스텝을 "현재 시각 − 1/fps 지점으로 시크"로 에뮬레이션하면 VFR · B프레임 영상에서 흔들린다.
- `audio-delay` 속성이 곧 요구사항의 "오프셋" 그 자체 — 재인코딩 없이 실시간 반영.
- `estimated-frame-number`, `hr-seek`, `speed`, `audio-pitch-correction` 등 필요한 제어가 모두 속성/커맨드로 노출되어 있다.
- WPF는 `HwndHost`를 통한 네이티브 창 임베딩이 성숙하고 MVVM 생태계가 가장 두껍다. (WinUI 3는 임베딩·배포 성숙도 면에서 이점이 없음)

**대안 발동 조건**: libmpv 임베딩에서 DPI/멀티모니터/렌더링 문제가 M0에서 해결되지 않으면 LibVLCSharp로 전환하고, 역방향 프레임 스텝은 exact seek 에뮬레이션으로 대체하며 정확도 한계를 UI에 명시한다.

### 4.3 libmpv 초기화 옵션 (핵심)

```
--wid=<HWND>                 # WPF HwndHost 자식 창에 임베딩
--input-default-bindings=no  # mpv 기본 단축키 비활성
--input-vo-keyboard=no       # ★ mpv가 키 입력을 직접 처리하지 않도록 (§6 정책과 충돌 방지)
--osd-level=0                # OSD는 앱이 직접 그림
--keep-open=yes              # 끝에서 자동 종료하지 않음
--hr-seek=yes                # 정확 시크 기본
--audio-pitch-correction=yes # 배속 변경 시 피치 유지
--hwdec=auto-safe            # 하드웨어 디코딩 (문제 시 설정에서 no)
--vo=gpu
--keepaspect=yes
```

### 4.4 기타 스택

| 영역 | 선택 | 비고 |
|---|---|---|
| 런타임 | .NET 8 (LTS), x64 | self-contained 배포 |
| UI | WPF + CommunityToolkit.Mvvm | 다크 테마 자체 구현 |
| 저장소 | SQLite (`Microsoft.Data.Sqlite` + Dapper) | `%APPDATA%\OtogeTrainer\library.db` |
| 설정 | JSON (`settings.json`) | 사람이 편집 가능 |
| 썸네일 | libmpv `screenshot-to-file` | 별도 ffmpeg 의존 회피 |
| 로깅 | Serilog (롤링 파일) | 재생 이벤트 · 오류 중심. 키 입력 내용은 기록하지 않음 |
| 테스트 | xUnit + 엔진 통합 테스트 | 검증용 클립 사용 |

---

## 5. 기능 요구사항

우선순위: **P0** = 사용자가 명시한 필수 / **P1** = v1 포함 권장 / **P2** = 이후

### 5.1 라이브러리 · 최근 재생 (FR-1xx)

| ID | 요구사항 | 우선 |
|---|---|---|
| FR-101 | 로컬 영상 파일을 열기(파일 대화상자 / 드래그&드롭 / 폴더 등록)로 추가한다. 지원 컨테이너: `mp4, mkv, mov, avi, webm, ts, m2ts, flv, wmv` | P0 |
| FR-102 | **최근 재생 목록**을 홈 최상단에 표시한다. 기본 50개, 최근 재생 순. 썸네일 · 제목 · 길이 · 진행률 · 마지막 재생 시각 표시 | P0 |
| FR-103 | 항목 클릭 시 **마지막 재생 위치 / 배속 / 오프셋 / 북마크가 복원된 상태**로 플레이어를 연다 (기본은 일시정지 상태로 시작) | P0 |
| FR-104 | 폴더를 라이브러리로 등록하고 앱 실행 시 변경분을 스캔한다 (실시간 감시는 P2) | P1 |
| FR-105 | 파일이 이동·삭제되면 "찾을 수 없음" 배지를 표시하고, **다시 찾기**로 새 경로에 재연결한다 (기존 북마크/오프셋 보존) | P1 |
| FR-106 | 제목 검색, 태그 필터, 정렬(최근 / 이름 / 길이 / 재생횟수) | P1 |
| FR-107 | 즐겨찾기(핀) 및 태그 부여 | P1 |
| FR-108 | 목록에서 항목 제거(DB에서만) / 파일 위치 열기 | P1 |
| FR-109 | 플레이리스트(세트) 구성 및 연속 재생 | P2 |

**파일 식별 규칙** — 경로 변경에 강인하도록 `(파일 크기, 재생 길이(ms), 선두 1MB + 말미 1MB의 SHA-256)` 조합을 지문으로 사용한다. 경로는 부가 정보로만 취급하며, 지문이 일치하면 같은 영상으로 간주해 기존 메타데이터를 승계한다.

### 5.2 기본 재생 제어 (FR-2xx)

| ID | 요구사항 | 우선 |
|---|---|---|
| FR-201 | 재생 / 일시정지 토글 | P0 |
| FR-202 | 타임라인 시크 바 — 드래그 중 미리보기 시각 표시, 놓으면 exact seek | P0 |
| FR-203 | **배속 조절**: 프리셋 `0.25 / 0.5 / 0.75 / 1.0 / 1.25 / 1.5 / 2.0`, 미세 조절 0.05 단위, 허용 범위 0.10 ~ 4.00 | P0 |
| FR-204 | 배속 변경 시 **오디오 피치 보정 유지**(기본 ON). 설정에서 OFF 가능 | P0 |
| FR-205 | 배속 1.0으로 즉시 복귀 (원클릭 / `Ctrl+0`) | P1 |
| FR-206 | 볼륨 조절 · 음소거 | P1 |
| FR-207 | 전체화면 토글(F11 / 더블클릭), 항상 위(Always on Top) | P1 |
| FR-208 | 영상 줌 · 팬(휠+드래그), 종횡비 고정, 화면 맞춤 리셋 | P2 |
| FR-209 | 좌우 반전(미러) — 상대 플레이 영상 대조용 | P2 |
| FR-210 | 재생 종료 시 동작 선택: 정지 / 처음으로 / A-B 루프 유지 | P1 |

### 5.3 정밀 탐색 (FR-3xx) — 핵심

| ID | 요구사항 | 우선 |
|---|---|---|
| FR-301 | **프레임 단위 앞/뒤 이동**. 엔진의 `frame-step` / `frame-back-step` 사용. 실행 시 **항상 일시정지 상태로 전환**된다 | P0 |
| FR-302 | 프레임 스텝 키를 길게 누르면 반복 실행 (초기 지연 350ms, 이후 60ms 간격, 최대 4프레임/틱까지 가속) | P1 |
| FR-303 | **5초 점프 앞/뒤**. 실제 점프량은 **배속에 연동**: `점프량 = 기준(5.0초) × 현재 배속` | P0 |
| FR-304 | 점프 기준값(기본 5.0초)은 설정에서 변경 가능하며, **"배속 무관 고정(항상 5초)" 모드**도 선택할 수 있다 | P1 |
| FR-305 | 큰 점프(기본 30초 × 배속) 별도 제공 | P2 |
| FR-306 | 모든 점프/시크는 **exact seek**를 사용한다 (키프레임으로 튀지 않음) | P0 |
| FR-307 | 현재 위치를 `HH:MM:SS.mmm`과 **프레임 번호**로 동시에 표시 | P1 |

**FR-303 배속 연동 규칙 근거**
`0.5x → 2.5초`, `1.0x → 5초`, `2.0x → 10초`.
느리게 볼수록 관심 구간이 좁으므로 탐색 간격도 촘촘해야 하고, 빠르게 볼수록 넓게 건너뛰는 것이 자연스럽다. 결과적으로 **한 번 누를 때 넘어가는 체감(실시간) 길이가 배속과 무관하게 일정**해진다. ([D-2 확정](docs/DECISIONS.md#d-2-5초배속에-따라-조정의-해석))

### 5.4 북마크 (FR-32x)

| ID | 요구사항 | 우선 |
|---|---|---|
| FR-321 | 영상별 **북마크 슬롯 5개** (1~5). 각 슬롯은 밀리초 정밀 위치를 저장 | P0 |
| FR-322 | 슬롯에 현재 위치 저장 / 해당 위치로 점프 / 슬롯 비우기 | P0 |
| FR-323 | 점프 시 exact seek. "점프 후 자동 일시정지" 옵션 (기본 ON) | P1 |
| FR-324 | 슬롯별 **라벨(메모)** 입력 (예: "라스트 트릴 진입") | P1 |
| FR-325 | 타임라인에 북마크 마커 표시, 마커 클릭으로 점프, 드래그로 위치 미세 조정 | P1 |
| FR-326 | 북마크는 영상 메타데이터로 영구 저장되며, 파일 경로가 바뀌어도 지문으로 승계된다 | P0 |
| FR-327 | 슬롯 5개를 넘는 **추가 북마크 목록**(무제한). 슬롯은 "빠른 접근용 즐겨찾기" 개념으로 유지 | P2 |

### 5.5 A/V 오프셋 (FR-4xx)

| ID | 요구사항 | 우선 |
|---|---|---|
| FR-401 | 영상별 **오디오 지연(오프셋)** 조절. 범위 **−1000ms ~ +1000ms**, 단위 5ms (미세 1ms) | P0 |
| FR-402 | **부호 정의**: 양수 = 오디오를 **늦춤** (소리가 영상보다 빠르게 들릴 때 사용). UI에 이 설명을 병기 | P0 |
| FR-403 | 재생 중 실시간 반영 (재시작 없음) | P0 |
| FR-404 | 값은 **영상별로 저장**되며 다음에 열 때 자동 적용 | P0 |
| FR-405 | **폴더 기본 오프셋** / **전역 기본 오프셋** 상속 — 같은 캡처 장비로 찍은 파일들을 일괄 보정. 우선순위: 영상 > 폴더 > 전역 | P1 |
| FR-406 | 오프셋 값을 프레임 수로도 병기 (예: `+33ms (≈2 frames @60fps)`) | P1 |
| FR-407 | 보정 도우미 — 1초 주기 플래시+클릭 테스트 클립을 재생하며 값을 맞추는 마법사 | P2 |

### 5.6 재생 중 단축키 차단 (FR-5xx) — 핵심

> **범위: 이 앱 내부에 한정한다.** 시스템 전역 키보드 후킹(`WH_KEYBOARD_LL` 등)은 **사용하지 않는다.**
> 다른 프로그램의 키 입력에는 일절 관여하지 않으며, 앱이 포커스를 잃으면 키보드는 평소 그대로 동작한다.
> 설계 상세는 **§6**.

| ID | 요구사항 | 우선 |
|---|---|---|
| FR-501 | **영상 재생 중**에는 `ESC`를 제외한 모든 키 입력을 앱이 무시한다. 등록된 단축키도 동작하지 않으며, **예외 키는 두지 않는다** — 전체화면 · 음소거 · 볼륨 · 항상 위도 마찬가지다 ([D-4](docs/DECISIONS.md#d-4-재생-중-차단의-예외)) | P0 |
| FR-502 | 차단된 키는 **아무 부작용도 일으키지 않는다.** 재생 중단 · 위치 이동 · 포커스 이동 · 버튼 활성화 · 메뉴 열림이 발생해서는 안 된다 (§6.3) | P0 |
| FR-503 | 차단은 **이 앱의 창이 포커스를 가진 동안만** 적용된다. 시스템 전역 후킹을 사용하지 않으므로 `Win`, `Alt+Tab`, `Ctrl+Esc`, `Ctrl+Alt+Del` 등 Windows 기본 동작은 자연히 그대로 동작한다 | P0 |
| FR-504 | `ESC` = **일시정지 전용**. 이 순간부터 단축키가 다시 동작한다. 일시정지 상태에서 `ESC`는 **아무 동작도 하지 않는다** — `ESC`가 플레이어를 닫는 일은 없다 ([D-3](docs/DECISIONS.md#d-3-esc의-동작)) | P0 |
| FR-511 | 라이브러리 복귀 경로를 `ESC`와 별도로 제공한다: 헤더의 `◀ 라이브러리` 버튼(상시) + `Alt+←`(일시정지 상태에서). 복귀 시 재생 위치 · 배속 · 오프셋을 저장한다 | P0 |
| FR-505 | 현재 단축키가 막혀 있는지 여부를 OSD 배지로 상시 표시한다 | P0 |
| FR-506 | 재생 중에도 **마우스로는 모든 조작이 가능**해야 한다 (온스크린 컨트롤 · 휠 · 사이드 버튼) | P0 |
| FR-507 | 앱이 비활성화되거나 창이 최소화되면 차단 상태를 해제한다 (앱으로 돌아왔을 때 일시정지 상태부터 시작) | P1 |
| FR-508 | 설정에서 **"재생 중에도 단축키 허용"**을 켤 수 있다 (기본 OFF) | P1 |
| FR-509 | 텍스트 입력 중(북마크 라벨, 검색창 등)에는 차단 규칙을 적용하지 않는다. 입력 컨트롤이 포커스를 가진 경우가 최우선 | P0 |
| FR-510 | 게임패드는 차단 대상이 아니며, 재생 중에도 컨트롤러로 조작할 수 있다 | P2 |

### 5.7 연습 보조 기능 (FR-6xx)

| ID | 요구사항 | 우선 |
|---|---|---|
| FR-601 | **A-B 구간 반복** — A/B 지점 지정, 무한 반복, 구간을 타임라인에 하이라이트 | P1 |
| FR-602 | 루프 사이 간격(0~2초)과 리드인(예: A보다 1초 앞에서 시작) 설정 | P1 |
| FR-603 | **연습 사다리(Practice Ladder)** — N회 반복마다 배속 자동 상승 (예: 0.5 → 0.6 → 0.75 → 1.0) | P2 |
| FR-604 | **가이드 라인 오버레이** — 화면에 수평/수직 기준선을 그려 판정선 · 레인 위치를 고정 확인. 색/두께/개수 조절, 영상별 저장 | P1 |
| FR-605 | 스크린샷 저장 (현재 프레임, 원본 해상도, PNG) | P1 |
| FR-606 | 현재 A-B 구간을 클립으로 내보내기 (무손실 컷) | P2 |
| FR-607 | 두 영상 나란히 비교 (분할 화면, 재생 동기화) | P2 |
| FR-608 | 세션 통계 — 영상별 총 연습 시간, 반복 횟수 | P2 |

### 5.8 설정 · 데이터 (FR-7xx)

| ID | 요구사항 | 우선 |
|---|---|---|
| FR-701 | 단축키 커스터마이즈 (충돌 검사 포함) | P1 |
| FR-702 | 기본 배속, 점프 기준값, 자동 일시정지, 재생 중 단축키 허용 여부 등 기본값 설정 | P1 |
| FR-703 | 하드웨어 디코딩 on/off, 출력 드라이버 선택 (문제 발생 시 폴백용) | P1 |
| FR-704 | **포터블 모드** — exe 옆 `data\` 폴더에 DB/설정 저장 | P2 |
| FR-705 | 라이브러리 내보내기/가져오기(JSON) — 재설치 · PC 이전 대비 | P2 |
| FR-706 | 언어: 한국어 / English | P2 |

---

## 6. 재생 중 단축키 차단 상세 설계

### 6.1 범위

사용자는 **손을 실제 게임 키 배열 위에 올리고 영상에 맞춰 친다.** 이때 발생하는 수십 번의 키 입력이 앱의 단축키를 발동시켜 재생이 멈추거나 위치가 튀면 연습이 성립하지 않는다.

**필요한 것은 이것뿐이다 — 이 앱이 재생 중에 키에 반응하지 않는 것.**

| 하는 것 | 하지 않는 것 |
|---|---|
| 앱 창이 포커스를 가진 동안, 재생 중 키 입력을 앱 내부에서 소비하고 무시 | ❌ 시스템 전역 키보드 후킹 (`SetWindowsHookEx`) |
| `ESC`만 예외로 처리 | ❌ 다른 프로그램으로 가는 키 차단 |
| WPF 기본 키 동작(포커스 이동 등) 억제 | ❌ 키 입력 기록 |

앱이 포커스를 잃으면(`Alt+Tab`, `Win`) 키보드는 평소와 완전히 동일하게 동작한다. 별도 처리가 필요 없다 — 애초에 아무것도 후킹하지 않기 때문이다.

### 6.2 WPF에서 실제로 "새는" 경로

`e.Handled = true` 한 줄로 끝나지 않는다. 재생이 멈추거나 화면이 튀는 사고는 대부분 아래에서 나온다.

| # | 누출 경로 | 대응 |
|---|---|---|
| 1 | 앱 단축키(`InputBindings` / `KeyGesture`) | 창 수준 `PreviewKeyDown`에서 먼저 소비 |
| 2 | 포커스된 `Button`을 `Space` / `Enter`가 눌러버림 | 컨트롤 바 버튼은 `Focusable="False"`, 재생 시작 시 포커스를 영상 호스트로 되돌림 |
| 3 | 포커스된 `Slider`(타임라인·볼륨)를 방향키가 움직임 | 위와 동일 + 방향키 소비 |
| 4 | 방향키 → `KeyboardNavigation` 방향 이동 | 플레이어 루트에 `DirectionalNavigation="None"` |
| 5 | `Tab` → 포커스 순회 | 플레이어 루트에 `TabNavigation="None"` |
| 6 | `Alt` / `F10` → 시스템 메뉴 · 액세스 키 | `PreviewKeyDown`에서 `e.Key == Key.System`이면 `e.SystemKey`로 판정 후 소비 |
| 7 | mpv 자체 키 바인딩 | `--input-default-bindings=no`, `--input-vo-keyboard=no` (§4.3) |
| 8 | `KeyUp`만 보는 컨트롤 | `PreviewKeyUp`도 동일하게 소비 |

### 6.3 처리 순서

창 수준 `PreviewKeyDown` / `PreviewKeyUp` 단일 진입점에서 아래 순서로 판정한다.

```
1. 포커스가 텍스트 입력 컨트롤인가?        → 통과 (FR-509)  ※ 북마크 라벨, 검색창
2. 플레이어 화면이 아닌가?                 → 통과
3. ESC 인가?                               → 소비
                                              · 재생 중  → 일시정지
                                              · 일시정지 → 아무 동작 없음 (D-3)
4. 재생 중인가? (설정 FR-508이 OFF일 때)   → 무조건 소비, 아무 동작도 하지 않음
5. 그 외(일시정지 상태)                    → 단축키 맵으로 라우팅
```

4번에서는 **로그도 남기지 않고 조용히 버린다.** 화면 깜빡임이나 소리 피드백도 없다 — 연습 중 방해가 되기 때문.

### 6.4 상태 전이 — 재생 상태에 직결

별도의 "모드"를 두지 않는다. **재생 중 = 단축키 차단**, **일시정지 = 단축키 동작**이 곧 규칙이다. 사용자가 외울 것이 하나뿐이고, 토글을 잊어서 생기는 혼란이 없다.

```
                      Space / 클릭 / 재생 버튼
        ┌──────────────────────────────────────────────┐
        │                                              ▼
┌───────────────────┐                      ┌─────────────────────────┐
│   일시정지         │                      │   재생 중                │
│   🔓 단축키 동작    │ ◀── ESC / 클릭 ───── │   🔒 ESC 외 전부 무시    │
│   프레임 스텝 가능  │      앱 비활성화      │   마우스 조작은 가능      │
└─────────┬─────────┘                      └─────────────────────────┘
          │ Alt+← 또는 ◀ 라이브러리 버튼      ※ ESC는 여기서 아무 동작도 하지 않음
          ▼
   라이브러리로 복귀 (상태 저장)
```

프레임 스텝은 어차피 일시정지 상태에서만 의미가 있으므로(FR-301), 이 모델에서 손해 보는 기능이 없다.

`ESC`에 "닫기"를 얹지 않은 이유는 [D-3](docs/DECISIONS.md#d-3-esc의-동작)에 있다 — 멈추려다 두 번 눌러 플레이어가 닫히는 사고를 막기 위함이다.

### 6.5 예외와 완화책

1. **텍스트 입력이 최우선** — 북마크 라벨을 타이핑하는 중이라면 차단 규칙보다 입력이 우선한다(FR-509).
2. **`ESC`는 절대 차단하지 않는다** — 재생 중 언제든 한 번에 멈출 수 있다. 다만 `ESC`로 플레이어가 닫히지는 않는다(FR-504).
3. **앱 비활성화 시 해제** — 다른 창에 갔다 돌아오면 일시정지 상태에서 시작(FR-507). 돌아오자마자 누른 키가 삼켜지는 일이 없다.
4. **설정으로 끄기** — "재생 중에도 단축키 허용"(FR-508). 일반 플레이어처럼 쓰고 싶은 사용자를 위한 탈출구.
5. **마우스는 언제나 동작** — §6.6.

### 6.6 재생 중의 조작 수단 (마우스)

재생 중에는 키보드가 반응하지 않으므로, **모든 조작이 마우스로 도달 가능해야 한다**(FR-506).

| 입력 | 동작 |
|---|---|
| 마우스 이동 | 온스크린 컨트롤 바 표시 (2.5초 후 자동 숨김) |
| 좌클릭(영상 영역) | 재생 / 일시정지 |
| 더블클릭 | 전체화면 토글 |
| 휠 위 / 아래 | 5초(×배속) 뒤 / 앞 점프 |
| 우클릭 드래그 좌 / 우 | 프레임 스텝 뒤 / 앞 (자동 일시정지) |
| 사이드 버튼 X1 / X2 | 이전 / 다음 북마크로 점프 |
| 컨트롤 바 버튼 | 배속 · 오프셋 · 북마크 · A-B · 음소거 · 볼륨 · 항상 위 · 전체화면 전부 클릭으로 접근 |

D-4에 따라 재생 중 예외 키가 없으므로, **단축키 목록에 있는 모든 기능은 컨트롤 바나 마우스 제스처 중 하나로 반드시 도달 가능해야 한다.** 이 대조 검수를 M1 완료 조건에 포함한다(R7).

---

## 7. 비기능 요구사항

| ID | 항목 | 기준 |
|---|---|---|
| NFR-01 | 앱 콜드 스타트 | 2.0초 이내 (홈 렌더 완료) |
| NFR-02 | 영상 오픈 → 첫 프레임 | 로컬 1080p60 기준 1.0초 이내 |
| NFR-03 | exact seek 지연 | 1080p60 H.264 기준 150ms 이내 |
| NFR-04 | 프레임 스텝 응답 | 60ms 이내 |
| NFR-05 | 키 입력 처리 | 차단 판정은 UI 스레드에서 1ms 이내. 차단된 키가 재생에 어떤 영향도 주지 않음 |
| NFR-06 | 재생 성능 | 4K60 H.264/HEVC 하드웨어 디코딩 시 프레임 드랍 없이 재생 |
| NFR-07 | 메모리 | 1080p 재생 중 RSS 500MB 이하 |
| NFR-08 | 데이터 안정성 | 북마크/오프셋은 변경 즉시 커밋. 비정상 종료로 손실되지 않음 |
| NFR-09 | 대상 OS | Windows 10 (1809+) / Windows 11, x64 |
| NFR-10 | 설치 | 무설치 zip 실행 지원(self-contained). 관리자 권한 불필요 |
| NFR-11 | 접근성 | 다크 테마 기본, 컨트롤 최소 32px 타깃, 일시정지 상태에서 모든 기능에 키보드 경로 존재 |
| NFR-12 | 프라이버시 | 네트워크 통신 없음(v1). 시스템 전역 키보드 후킹을 사용하지 않으며, 키 입력을 저장 · 전송하지 않음 |

---

## 8. 데이터 모델

### 8.1 저장 위치

```
%APPDATA%\OtogeTrainer\
├─ library.db          # SQLite
├─ settings.json       # 앱 설정 · 단축키
├─ thumbs\<hash>.jpg   # 썸네일 캐시
└─ logs\otoge-*.log
```

### 8.2 스키마 (초안)

```sql
CREATE TABLE videos (
  id               INTEGER PRIMARY KEY,
  fingerprint      TEXT NOT NULL UNIQUE,  -- size|duration|sha256(head+tail)
  path             TEXT NOT NULL,         -- 마지막으로 확인된 경로
  title            TEXT NOT NULL,         -- 기본값: 파일명
  duration_ms      INTEGER NOT NULL,
  fps              REAL,                  -- NULL 가능 (VFR)
  is_vfr           INTEGER NOT NULL DEFAULT 0,
  width            INTEGER,
  height           INTEGER,
  file_size        INTEGER NOT NULL,
  added_at         TEXT NOT NULL,
  last_played_at   TEXT,
  play_count       INTEGER NOT NULL DEFAULT 0,
  last_position_ms INTEGER NOT NULL DEFAULT 0,
  av_offset_ms     INTEGER,               -- NULL이면 폴더/전역 기본값 상속
  last_speed       REAL,
  is_favorite      INTEGER NOT NULL DEFAULT 0,
  missing          INTEGER NOT NULL DEFAULT 0,
  note             TEXT
);

CREATE TABLE bookmarks (
  id          INTEGER PRIMARY KEY,
  video_id    INTEGER NOT NULL REFERENCES videos(id) ON DELETE CASCADE,
  slot        INTEGER,                    -- 1..5, NULL이면 슬롯 외 추가 북마크
  position_ms INTEGER NOT NULL,
  label       TEXT,
  created_at  TEXT NOT NULL,
  UNIQUE(video_id, slot)
);

CREATE TABLE ab_loops (
  video_id   INTEGER PRIMARY KEY REFERENCES videos(id) ON DELETE CASCADE,
  a_ms       INTEGER NOT NULL,
  b_ms       INTEGER NOT NULL,
  lead_in_ms INTEGER NOT NULL DEFAULT 0,
  gap_ms     INTEGER NOT NULL DEFAULT 0,
  enabled    INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE guides (              -- 가이드 라인 오버레이 (FR-604)
  id          INTEGER PRIMARY KEY,
  video_id    INTEGER NOT NULL REFERENCES videos(id) ON DELETE CASCADE,
  orientation TEXT NOT NULL,       -- h = 수평, v = 수직
  ratio       REAL NOT NULL,       -- 0.0~1.0 (해상도 독립)
  color       TEXT,
  thickness   INTEGER
);

CREATE TABLE tags        (id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE);
CREATE TABLE video_tags  (video_id INTEGER, tag_id INTEGER, PRIMARY KEY(video_id, tag_id));
CREATE TABLE folders     (id INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE,
                          default_av_offset_ms INTEGER, last_scan_at TEXT);
CREATE TABLE schema_meta (key TEXT PRIMARY KEY, value TEXT);   -- 마이그레이션 버전
```

---

## 9. 아키텍처

### 9.1 프로젝트 구성

```
OtogeTrainer.sln
├─ src/
│  ├─ OtogeTrainer.App/          # WPF: Views, ViewModels, 테마, 진입점
│  ├─ OtogeTrainer.Core/         # 도메인 모델, 서비스 인터페이스, 재생 상태 머신
│  ├─ OtogeTrainer.Playback/     # IMediaEngine 구현 (MpvMediaEngine, HwndHost)
│  ├─ OtogeTrainer.Input/        # 단축키 맵, 입력 게이트(재생 중 차단), 마우스 제스처
│  └─ OtogeTrainer.Data/         # SQLite 리포지토리, 마이그레이션, 설정
└─ tests/
   ├─ OtogeTrainer.Core.Tests/
   └─ OtogeTrainer.Playback.IntegrationTests/   # 검증용 테스트 클립 사용
```

### 9.2 핵심 인터페이스 (초안)

```csharp
public interface IMediaEngine : IDisposable
{
    Task LoadAsync(string path, CancellationToken ct = default);
    void Play();
    void Pause();

    bool     IsPaused        { get; }
    TimeSpan Position        { get; }
    TimeSpan Duration        { get; }
    double?  FrameRate       { get; }   // VFR이면 null
    long?    FrameNumber     { get; }   // mpv estimated-frame-number

    double   Speed           { get; set; }   // 0.10 ~ 4.00
    bool     PitchCorrection { get; set; }
    TimeSpan AudioDelay      { get; set; }   // ★ A/V 오프셋
    double   Volume          { get; set; }

    void SeekAbsolute(TimeSpan position, bool exact = true);
    void SeekRelative(TimeSpan delta,    bool exact = true);
    void StepFrame(int delta);               // +1 / -1 (자동 일시정지)
    void Screenshot(string path);

    event EventHandler<PlaybackState> StateChanged;
    event EventHandler<TimeSpan>      PositionChanged;
    event EventHandler                EndReached;
    event EventHandler<EngineError>   Error;
}

/// 재생 중 앱 단축키를 차단하는 게이트. 시스템 전역 후킹은 사용하지 않는다(§6.1).
/// 창 수준 PreviewKeyDown / PreviewKeyUp에서 단 한 번 호출된다.
public interface IInputGate
{
    /// 현재 이 키 입력을 단축키로 처리해도 되는지 판정한다.
    /// 반환값이 Swallow면 호출자가 e.Handled = true 로 조용히 버린다.
    InputDecision Evaluate(KeyEventContext ctx);
}

public enum InputDecision
{
    Dispatch,   // 단축키 맵으로 라우팅 (일시정지 상태)
    Swallow,    // 소비하고 아무 동작도 하지 않음 (재생 중)
    Escape,     // ESC — 재생 중이면 일시정지, 일시정지면 무동작 (D-3)
    PassThrough // 텍스트 입력 포커스 등 — WPF 기본 처리에 맡김
}

public readonly record struct KeyEventContext(
    Key Key, Key SystemKey, ModifierKeys Modifiers,
    bool IsPlaying, bool IsTextInputFocused, bool AllowShortcutsWhilePlaying);
```

### 9.3 스레딩

- libmpv 이벤트 루프는 전용 백그라운드 스레드에서 `mpv_wait_event` 수행. UI 갱신은 `Dispatcher`로 마샬링.
- 위치 표시는 이벤트 폭주를 막기 위해 **30Hz로 스로틀링**.
- 입력 게이트 판정은 UI 스레드에서 순수 함수로 수행(I/O·로깅 없음) — 재생 중 난타에도 부하가 없어야 한다.
- DB 쓰기는 전부 비동기. 북마크/오프셋 변경은 즉시 커밋(NFR-08).

---

## 10. 기술 리스크

| # | 리스크 | 영향 | 대응 |
|---|---|---|---|
| R1 | **VFR(가변 프레임레이트) 캡처 영상** — OBS/게임바 녹화물에 흔함. "1프레임" 길이가 일정하지 않아 스텝 간격이 들쭉날쭉 | 중 | `frame-back-step`은 실제 프레임 경계로 이동하므로 동작 자체는 정확. UI에 `VFR` 배지 표시, 프레임 번호가 추정치임을 명시 |
| R2 | 긴 GOP 영상에서 `frame-back-step`이 느림 (역방향은 GOP 앞부터 재디코딩) | 중 | 스텝 중 스피너 표시, 연속 역스텝은 배칭. 심하면 "정밀 모드 OFF"(근사 시크) 옵션 제공 |
| R3 | **차단이 새는 경로를 빠뜨림** — 포커스된 버튼을 `Space`가 누르거나 `Alt`가 시스템 메뉴를 여는 등 | 중 | §6.2 누출 경로 표를 체크리스트로 삼아 전수 검증([ROADMAP](docs/ROADMAP.md) §3.3) |
| R4 | libmpv HWND 임베딩의 DPI / 멀티모니터 / 에어로 스냅 이슈 | 중 | M0 스파이크에서 선검증. 실패 시 LibVLCSharp 전환(§4.2) |
| R5 | libmpv 배포 바이너리 라이선스 (GPL vs LGPL 빌드) | 중 | LGPL 빌드 사용 또는 소스 공개 방침 확정 ([ROADMAP](docs/ROADMAP.md) §5) |
| R6 | 하드웨어 디코딩 드라이버별 시크/스텝 이상 동작 | 중 | `--hwdec=no` 폴백 토글(FR-703), 이상 감지 시 자동 권유 |
| R7 | **재생 중 예외 키를 두지 않기로 했으므로(D-4), 마우스 경로에 구멍이 있으면 그 기능은 재생 중 아예 도달 불가** | 중 | 컨트롤 바에 전체화면 · 음소거 · 볼륨까지 전부 노출. 단축키 목록과 마우스 경로를 1:1 대조하는 검수를 M1 완료 조건에 포함 |

---

## 11. 용어

| 용어 | 정의 |
|---|---|
| **입력 게이트** | 재생 중 앱 단축키를 소비해 무시하는 창 수준 판정기 (§6). 앱 내부에만 작용 |
| **오프셋** | 영상 대비 오디오의 재생 지연값(ms). 양수 = 오디오를 늦춤 |
| **exact seek** | 키프레임이 아닌 정확한 시각으로 이동하는 시크 |
| **프레임 스텝** | 정확히 한 프레임 앞/뒤로 이동 |
| **A-B 루프** | 지정한 두 지점 사이를 무한 반복 재생 |
| **지문(fingerprint)** | 경로 변경에 강인한 영상 식별자 (§5.1) |
| **VFR** | 가변 프레임레이트. 화면 녹화물에 흔함 |
