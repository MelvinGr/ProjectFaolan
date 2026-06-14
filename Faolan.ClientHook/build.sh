#!/usr/bin/env bash
# Build the 32-bit faolan_hook.dll with pure LLVM (no mingw / Windows SDK).
set -e
cd "$(dirname "$0")"
llvm-dlltool -m i386 -k -d kernel32.def -l kernel32.lib -D kernel32.dll
clang --target=i686-pc-windows-msvc -O2 -ffreestanding -fno-stack-protector -c faolan_hook.c -o faolan_hook.obj
lld-link /dll /nodefaultlib /entry:DllMain /machine:x86 /out:faolan_hook.dll faolan_hook.obj kernel32.lib
echo "built: $(file faolan_hook.dll | cut -d: -f2-)"
