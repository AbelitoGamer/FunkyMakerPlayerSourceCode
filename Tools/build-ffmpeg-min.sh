#!/usr/bin/env bash
# Builds a minimal, LGPL, static ffmpeg for osu! background videos (Osu/OsuVideo.cs) into
# Tools/ffmpeg/. It only contains what the game's pipe needs:
#   ffmpeg -ss T -i video -an -vf scale,pad -r 30 -f rawvideo -pix_fmt rgba -
# i.e. the usual osu! video containers/codecs -> scale/pad -> rawvideo on stdout. ~3-6 MB instead
# of the ~130 MB full build.
#
# Windows: run from MSYS2 (UCRT64 toolchain, the script installs make/nasm/diffutils with pacman):
#   C:\msys64\usr\bin\bash.exe -lc "MSYSTEM=UCRT64 source /etc/profile; bash Tools/build-ffmpeg-min.sh"
#   (Tools\fetch-ffmpeg.ps1 does this for you.)
# Linux / macOS: needs gcc/clang, make, nasm:  bash Tools/build-ffmpeg-min.sh
set -euo pipefail

FFMPEG_VERSION="${FFMPEG_VERSION:-8.0}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEST="$HERE/ffmpeg"
WORK="${WORK:-$HERE/.ffmpeg-build}"
JOBS="$(nproc 2>/dev/null || echo 4)"

case "$(uname -s)" in
  MINGW*|MSYS*|UCRT*|CLANG*)
    if command -v pacman >/dev/null; then
      pacman -S --needed --noconfirm make diffutils mingw-w64-ucrt-x86_64-nasm mingw-w64-ucrt-x86_64-gcc
    fi
    EXE=ffmpeg.exe
    EXTRA_LDFLAGS="-static"   # no libwinpthread/libgcc DLLs next to the exe
    ;;
  *)
    EXE=ffmpeg
    EXTRA_LDFLAGS=""
    ;;
esac

mkdir -p "$WORK" "$DEST"
cd "$WORK"
SRC="ffmpeg-$FFMPEG_VERSION"
if [ ! -d "$SRC" ]; then
  echo "Downloading ffmpeg $FFMPEG_VERSION source"
  curl -fL --retry 3 -o "$SRC.tar.xz" "https://ffmpeg.org/releases/$SRC.tar.xz"
  tar -xf "$SRC.tar.xz"
fi
cd "$SRC"

# Containers and codecs seen in osu! beatmap videos (mp4/mov, avi, flv, mkv/webm, wmv, mpg, ogv).
DEMUXERS="mov,avi,flv,matroska,asf,mpegps,mpegts,mpegvideo,ogg,m4v,h264,hevc"
DECODERS="h264,hevc,mpeg4,h263,msmpeg4v1,msmpeg4v2,msmpeg4v3,flv,vp6,vp6a,vp6f,vp8,vp9,\
mpeg1video,mpeg2video,wmv1,wmv2,wmv3,vc1,mjpeg,theora,rawvideo"
PARSERS="h264,hevc,mpeg4video,mpegvideo,h263,vp8,vp9,vc1,mjpeg,vp3"
FILTERS="scale,pad,fps,format,null,buffer,buffersink"

[ -f Makefile ] && make distclean >/dev/null 2>&1 || true
./configure \
  --prefix="$WORK/install" \
  --disable-everything --disable-autodetect --disable-network \
  --disable-doc --disable-debug --disable-ffplay --disable-ffprobe --enable-ffmpeg \
  --disable-avdevice --disable-swresample \
  --enable-small --enable-static --disable-shared \
  --enable-swscale --enable-avfilter \
  --enable-protocol=file,pipe \
  --enable-demuxer="$DEMUXERS" \
  --enable-decoder="$DECODERS" \
  --enable-parser="$PARSERS" \
  --enable-filter="$FILTERS" \
  --enable-encoder=rawvideo --enable-muxer=rawvideo \
  --extra-ldflags="$EXTRA_LDFLAGS"

make -j"$JOBS"
strip "$EXE" 2>/dev/null || true
cp "$EXE" "$DEST/$EXE"
cp COPYING.LGPLv2.1 "$DEST/LICENSE.txt"
{
  echo "Minimal LGPL ffmpeg $FFMPEG_VERSION for FunkyMaker Player (osu! background videos)."
  echo "Source: https://ffmpeg.org/releases/$SRC.tar.xz  (built by Tools/build-ffmpeg-min.sh)"
  echo "Configuration:"
  ./"$EXE" -hide_banner -buildconf 2>/dev/null | sed 's/^/  /' || true
} >> "$DEST/LICENSE.txt"

"$DEST/$EXE" -hide_banner -version | head -1
ls -l "$DEST/$EXE"
echo "ffmpeg ready in $DEST"
