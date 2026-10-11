#!/bin/sh
# Rebuilds faultprobe.o with the Android NDK (set ANDROID_NDK_ROOT). Output goes next to libdobby.a.
set -e
cd "$(dirname "$0")"
CLANG="$ANDROID_NDK_ROOT/toolchains/llvm/prebuilt/linux-x86_64/bin/clang"
"$CLANG" --target=aarch64-linux-android21 -O2 -fPIC -mno-outline-atomics -Wall -Wextra -c faultprobe.c -o ../linux-bionic-arm64/faultprobe.o
