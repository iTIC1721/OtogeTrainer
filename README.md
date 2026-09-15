# OtogeTrainer

PC 리듬게임(otoge) 플레이어를 위한 **플레이 영상 복기 전용 로컬 영상 뷰어** — Windows 데스크톱 앱.

> **현재 상태: M1(MVP) 구현 완료 · 수동 확인 대기.**
> P0 요구사항 전부 구현, 종단 자동 검증 42/42 통과. 실제 플레이 영상으로 30분 사용해 보는 확인이 남았습니다. ([M1 진행 기록](docs/M1-PROGRESS.md))

## 문서

| 문서 | 내용 |
|---|---|
| [SPEC.md](SPEC.md) | 제품 명세서 — 목표, 기술 스택 결정, 기능 요구사항(FR), 비기능 요구사항, 데이터 모델, 아키텍처 |
| [docs/UI-BLUEPRINT.md](docs/UI-BLUEPRINT.md) | UI 청사진 — 화면 흐름, 와이어프레임, 인터랙션 규칙, 키/마우스 매핑, 비주얼 가이드 |
| [docs/ROADMAP.md](docs/ROADMAP.md) | 마일스톤, 검증 계획, 리스크 레지스터, 배포·라이선스 |
| [docs/DECISIONS.md](docs/DECISIONS.md) | 결정 기록 — 확정된 설계 결정과 그 근거 |
| [docs/M0-FINDINGS.md](docs/M0-FINDINGS.md) | M0 스파이크 결과 — 측정치와 스택 확정 근거 |
| [docs/M1-PROGRESS.md](docs/M1-PROGRESS.md) | **M1 진행 기록** — 구현 범위, 검증 결과, 발견한 결함, 남은 확인 |
| [docs/THIRD-PARTY.md](docs/THIRD-PARTY.md) | 서드파티 구성요소와 라이선스 |

## 한 줄 요약

일반 플레이어와 다른 점은 세 가지입니다.

1. **프레임 단위 정밀 탐색이 1급 기능** — 프레임 스텝, 배속 연동 점프, 북마크 5슬롯
2. **영상별 상태를 영구 기억** — A/V 오프셋, 배속, 마지막 재생 위치, 북마크
3. **재생 중에는 앱 단축키를 받지 않음** — 손을 건반에 올리고 실제 플레이 동작을 따라 해도 재생이 멈추거나 튀지 않음. 예외 없이 `ESC`(일시정지)만 동작. 시스템 전역 키보드 후킹은 사용하지 않으며, 다른 프로그램의 키 입력에는 전혀 관여하지 않는다.

## 실행

```bash
# 1) libmpv 배치 — native/libmpv-2.dll (저장소에 커밋하지 않음)
# 2) 빌드
dotnet build OtogeTrainer.sln
# 3) 앱 실행
dotnet run --project src/OtogeTrainer.App
```

데이터는 `%APPDATA%\OtogeTrainer\` 에 저장됩니다 (`--data <폴더>` 로 변경 가능).

## 검증

```bash
# 검증 클립 생성 (ffmpeg 필요, 저장소에 커밋하지 않음)
bash tools/make-test-clips.sh

# 단위 테스트
dotnet test OtogeTrainer.sln

# M1 종단 검증 — 실제 창 · 엔진 · 임시 DB 로 사용자 시나리오 전체 (사용자 라이브러리는 건드리지 않음)
src/OtogeTrainer.App/bin/Debug/net8.0-windows/OtogeTrainer.exe --smoketest

# M0 회귀 — 재생 엔진 정확도 / 입력 가드 / 영상 임베딩·마우스 경로
spike/OtogeTrainer.M0/bin/Debug/net8.0-windows/OtogeTrainer.M0.exe --selftest
spike/OtogeTrainer.M0/bin/Debug/net8.0-windows/OtogeTrainer.M0.exe --inputtest
spike/OtogeTrainer.M0/bin/Debug/net8.0-windows/OtogeTrainer.M0.exe --windowtest
```
