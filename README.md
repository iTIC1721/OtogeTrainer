# OtogeTrainer

PC 리듬게임(otoge) 플레이어를 위한 **플레이 영상 복기 전용 로컬 영상 뷰어** — Windows 데스크톱 앱.

> **현재 상태: M0 기술 검증 통과.** 본 구현(M1)은 아직 시작하지 않았습니다.
> 설계 결정(D-1 ~ D-7) 확정 완료, **스택은 WPF (.NET 8) + libmpv 로 확정**되었습니다.
> 현재 코드는 검증 하네스(`spike/`)와 입력 게이트(`src/OtogeTrainer.Input`)뿐입니다. ([M0 결과](docs/M0-FINDINGS.md))

## 문서

| 문서 | 내용 |
|---|---|
| [SPEC.md](SPEC.md) | 제품 명세서 — 목표, 기술 스택 결정, 기능 요구사항(FR), 비기능 요구사항, 데이터 모델, 아키텍처 |
| [docs/UI-BLUEPRINT.md](docs/UI-BLUEPRINT.md) | UI 청사진 — 화면 흐름, 와이어프레임, 인터랙션 규칙, 키/마우스 매핑, 비주얼 가이드 |
| [docs/ROADMAP.md](docs/ROADMAP.md) | 마일스톤, 검증 계획, 리스크 레지스터, 배포·라이선스 |
| [docs/DECISIONS.md](docs/DECISIONS.md) | 결정 기록 — 확정된 설계 결정과 그 근거 |
| [docs/M0-FINDINGS.md](docs/M0-FINDINGS.md) | **M0 스파이크 결과** — 측정치와 스택 확정 근거 |

## 한 줄 요약

일반 플레이어와 다른 점은 세 가지입니다.

1. **프레임 단위 정밀 탐색이 1급 기능** — 프레임 스텝, 배속 연동 점프, 북마크 5슬롯
2. **영상별 상태를 영구 기억** — A/V 오프셋, 배속, 마지막 재생 위치, 북마크
3. **재생 중에는 앱 단축키를 받지 않음** — 손을 건반에 올리고 실제 플레이 동작을 따라 해도 재생이 멈추거나 튀지 않음. 예외 없이 `ESC`(일시정지)만 동작. 시스템 전역 키보드 후킹은 사용하지 않으며, 다른 프로그램의 키 입력에는 전혀 관여하지 않는다.

## 개발 환경 준비

```bash
# 1) 검증 클립 생성 (ffmpeg 필요, 저장소에 커밋하지 않음)
bash tools/make-test-clips.sh

# 2) libmpv 배치 — spike/native/libmpv-2.dll (저장소에 커밋하지 않음)
#    없어도 입력 게이트 검증은 동작합니다.

# 3) 빌드 및 테스트
dotnet build OtogeTrainer.sln
dotnet test tests/OtogeTrainer.Input.Tests
```

## M0 검증 실행

```bash
# 재생 엔진 자동 검사 (헤드리스) — M0-2 ~ M0-5, M0-8
spike/OtogeTrainer.M0/bin/Debug/net8.0-windows/OtogeTrainer.M0.exe --selftest

# HwndHost 임베딩 + 렌더링 확인 — M0-1 (m0-window.png 생성)
spike/OtogeTrainer.M0/bin/Debug/net8.0-windows/OtogeTrainer.M0.exe --windowtest

# 수동 확인 하네스 (입력 누출 자동 검출 + 재생 엔진 조작)
dotnet run --project spike/OtogeTrainer.M0
```
