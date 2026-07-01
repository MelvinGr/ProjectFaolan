# Builds the 32-bit faolan_hook.dll with pure LLVM (clang + lld-link + llvm-dlltool) — no mingw / Windows SDK.
# Usage (matches Faolan.Wireshark):
#   cmake -S Faolan.ClientHook -B Faolan.ClientHook/build
#   cmake --build Faolan.ClientHook/build         # -> build/faolan_hook.dll
#
# LANGUAGES NONE: there is no CRT/SDK for CMake's normal compiler detection to link a test program against, so
# we orchestrate the toolchain directly (the client is freestanding, no-CRT). The same three steps as before.
cmake_minimum_required(VERSION 3.20)
project(faolan_hook LANGUAGES NONE)

find_program(CLANG clang REQUIRED)
find_program(LLD_LINK lld-link REQUIRED)
find_program(LLVM_DLLTOOL llvm-dlltool REQUIRED)

set(SRC ${CMAKE_CURRENT_SOURCE_DIR}/faolan_hook.c)
set(DEF ${CMAKE_CURRENT_SOURCE_DIR}/kernel32.def)
set(IMPLIB ${CMAKE_CURRENT_BINARY_DIR}/kernel32.lib)
set(OBJ ${CMAKE_CURRENT_BINARY_DIR}/faolan_hook.obj)
set(DLL ${CMAKE_CURRENT_BINARY_DIR}/faolan_hook.dll)

# 1) kernel32 import lib from the .def (-k: import by undecorated name, keep the @N stdcall link symbols).
add_custom_command(OUTPUT ${IMPLIB}
        COMMAND ${LLVM_DLLTOOL} -m i386 -k -d ${DEF} -l ${IMPLIB} -D kernel32.dll
        DEPENDS ${DEF}
        COMMENT "llvm-dlltool: kernel32 import lib")

# 2) compile (freestanding, no CRT; -fasm-blocks for the naked detours).
add_custom_command(OUTPUT ${OBJ}
        COMMAND ${CLANG} --target=i686-pc-windows-msvc -O2 -ffreestanding -fno-stack-protector -fasm-blocks
        -c ${SRC} -o ${OBJ}
        DEPENDS ${SRC}
        COMMENT "clang: compile faolan_hook.c (32-bit)")

# 3) link the DLL (no default libs, DllMain entry).
add_custom_command(OUTPUT ${DLL}
        COMMAND ${LLD_LINK} /dll /nodefaultlib /entry:DllMain /machine:x86 /out:${DLL} ${OBJ} ${IMPLIB}
        DEPENDS ${OBJ} ${IMPLIB}
        COMMENT "lld-link: faolan_hook.dll")

add_custom_target(faolan_hook ALL DEPENDS ${DLL})
