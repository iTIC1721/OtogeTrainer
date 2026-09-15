# 서드파티 구성요소

[D-6](DECISIONS.md#d-6-라이선스--공개-방침)에 따라 **의존성을 추가할 때마다 이 문서를 갱신한다.** 나중에 소급 정리하는 것이 훨씬 비싸다.

- **갱신**: 2026-09-16 (M1)
- **배포 상태**: 배포하지 않음 (개인 사용). 아래 의무는 **배포 시점**에 발생한다.

## 앱과 함께 배포되는 것

| 구성요소 | 버전 | 라이선스 | 용도 | 비고 |
|---|---|---|---|---|
| **libmpv** (`libmpv-2.dll`) | v0.41.0-1023-g69e63f425 | ⚠ **구성 미확인** — GPLv2+ 또는 LGPLv2.1+ | 재생 엔진 | 아래 참고 |
| .NET 8 런타임 | 8.0 | MIT | 런타임 | self-contained 배포 시 동봉 |
| Microsoft.Data.Sqlite | 8.0.8 | MIT | 라이브러리 DB | |
| SQLitePCLRaw (bundle_e_sqlite3) | Microsoft.Data.Sqlite 전이 의존 | Apache-2.0 | SQLite 바인딩 | |
| SQLite (`e_sqlite3.dll`) | 번들 | Public Domain | DB 엔진 | |
| CommunityToolkit.Mvvm | 8.2.2 | MIT | MVVM 소스 생성기 | |

## 개발 · 검증에만 쓰는 것 (배포하지 않음)

| 구성요소 | 라이선스 | 용도 |
|---|---|---|
| xUnit · xunit.runner.visualstudio | Apache-2.0 | 단위 테스트 |
| Microsoft.NET.Test.Sdk | MIT | 테스트 실행 |
| ffmpeg 9.0.1 (gyan.dev full build) | GPLv3 | 검증 클립 생성 (`tools/make-test-clips.sh`) — 앱에 포함되지 않음 |

## libmpv 라이선스 — 배포 전 필수 확인

현재 사용 중인 빌드: shinchiro/mpv-winbuild-cmake 릴리스 `20260903`, `mpv-dev-x86_64-20260903-git-69e63f425a.7z`

- 이 계열의 일반 배포 빌드는 **GPL 구성**인 경우가 많다. **이 빌드의 구성은 확인하지 않았다.**
- **개인 사용 중에는 문제가 없다.** GPL 의무는 배포할 때 생긴다.
- **배포를 결정하면** 다음 중 하나가 필요하다.
  - 오픈소스(GPL 호환)로 공개 → 현재 빌드 그대로 사용 가능, 소스 공개
  - 비공개 배포 → **LGPL 구성 libmpv 를 직접 빌드**하거나 LGPL 빌드를 확보해 교체
- 엔진은 `IMediaEngine` 뒤에 있으므로 교체 시 앱 코드는 바뀌지 않는다.

앱 `설정 → 정보` 탭(M2)에 이 표의 라이선스 전문을 포함한다.
