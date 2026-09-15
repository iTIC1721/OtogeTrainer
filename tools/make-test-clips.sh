#!/usr/bin/env bash
#
# 검증용 클립 생성 (ROADMAP §3.1 / §3.2)
#
# 클립은 용량이 커서 저장소에 넣지 않는다(.gitignore). 필요할 때 이 스크립트로 다시 만든다.
#
#   bash tools/make-test-clips.sh
#
# 요구: ffmpeg (PATH 에 없으면 FFMPEG 환경변수로 지정)
#
#   FFMPEG="/c/Users/.../ffmpeg.exe" bash tools/make-test-clips.sh
#
set -euo pipefail

FFMPEG="${FFMPEG:-ffmpeg}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$ROOT/tests/assets"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

mkdir -p "$OUT"

# drawtext 는 폰트가 필요하다. 윈도우 경로의 콜론(C:)이 filtergraph 파서와 충돌하므로
# 폰트를 작업 폴더로 복사해 상대 경로로 참조한다.
FONT_SRC="${FONT_SRC:-/c/Windows/Fonts/consola.ttf}"
if [ ! -f "$FONT_SRC" ]; then
  echo "폰트를 찾을 수 없습니다: $FONT_SRC (FONT_SRC 로 지정하세요)" >&2
  exit 1
fi
cp "$FONT_SRC" "$WORK/font.ttf"
cd "$WORK"

# ── §3.1 프레임 번호 번인 클립 ────────────────────────────────────────────────
# 화면의 F 값과 앱이 보고하는 프레임 번호를 사람이 직접 대조할 수 있다.
make_frametest() {
  local rate="$1" tag="$2"
  echo "생성 frametest_${tag}_cfr.mp4  (${rate}fps, 120초)"
  "$FFMPEG" -y -hide_banner -loglevel error \
    -f lavfi -i "testsrc2=size=1280x720:rate=${rate}" \
    -f lavfi -i "sine=frequency=440:sample_rate=48000" \
    -vf "drawtext=fontfile=font.ttf:text='F %{n}    T %{pts\\:hms}':fontsize=54:fontcolor=white:box=1:boxcolor=black@0.65:x=40:y=40" \
    -t 120 -c:v libx264 -preset veryfast -pix_fmt yuv420p -g 60 -c:a aac -shortest \
    "$OUT/frametest_${tag}_cfr.mp4"
}

make_frametest 60     60
make_frametest 30     30
make_frametest 59.94  5994

# ── §3.2 A/V 싱크 클립 ───────────────────────────────────────────────────────
# 1초마다 1프레임 흰 플래시 + 같은 시점에 20ms 클릭음.
# 오프셋 0 에서 플래시와 클릭이 동시에 느껴져야 한다.
echo "생성 avsync_1s_flash_click.mp4  (30초)"
"$FFMPEG" -y -hide_banner -loglevel error \
  -f lavfi -i "color=c=black:s=960x540:r=60:d=30" \
  -f lavfi -i "sine=frequency=1000:sample_rate=48000:duration=30" \
  -vf "drawbox=x=0:y=0:w=iw:h=ih:color=white:t=fill:enable='lt(mod(n\\,60)\\,1)',drawtext=fontfile=font.ttf:text='F %{n}  T %{pts\\:hms}':fontsize=36:fontcolor=#00FF66:box=1:boxcolor=black@0.7:x=30:y=h-80" \
  -af "volume=volume='lt(mod(t\\,1)\\,0.02)':eval=frame" \
  -c:v libx264 -preset veryfast -pix_fmt yuv420p -g 60 -c:a aac \
  "$OUT/avsync_1s_flash_click.mp4"

# ── 생성 결과 자체를 검증한다 ────────────────────────────────────────────────
# 클립이 틀어져 있으면 그 위에서 내린 모든 판정이 무의미해진다.
echo
echo "── 검증: 플래시 위치 (앞 5개, 1.000초 간격이어야 함)"
ffprobe -v error -f lavfi \
  -i "movie=$OUT/avsync_1s_flash_click.mp4,signalstats" \
  -show_entries frame=pts_time:frame_tags=lavfi.signalstats.YAVG \
  -of csv=p=0 2>/dev/null | awk -F, '$2>200 {print "   " $1}' | head -5

echo "── 검증: 오디오 게이팅 (무음 구간 / 클릭 구간)"
printf "   무음 0.2~0.7s  "
"$FFMPEG" -hide_banner -ss 0.2 -t 0.5 -i "$OUT/avsync_1s_flash_click.mp4" \
  -af volumedetect -f null - 2>&1 | grep -oE 'max_volume: [-0-9.]+ dB'
printf "   클릭 0~0.05s   "
"$FFMPEG" -hide_banner -ss 0 -t 0.05 -i "$OUT/avsync_1s_flash_click.mp4" \
  -af volumedetect -f null - 2>&1 | grep -oE 'max_volume: [-0-9.]+ dB'

echo
echo "완료: $OUT"
ls -lh "$OUT"
