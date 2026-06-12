cmake_minimum_required(VERSION 3.16)
project(faolan_wireshark C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_C_EXTENSIONS OFF)

option(BUILD_STANDALONE_TEST "Build standalone stream/framer test harness" ON)
option(BUILD_WIRESHARK_PLUGIN "Build Wireshark EPAN plugin module" ON)
option(INSTALL_PLUGIN_TO_PERSONAL_DIR "Install plugin to personal Wireshark plugin directory" ON)

find_package(ZLIB REQUIRED)
find_package(PkgConfig REQUIRED)

pkg_check_modules(WIRESHARK REQUIRED IMPORTED_TARGET wireshark)

if(WIRESHARK_VERSION)
    string(REGEX MATCH "^[0-9]+\\.[0-9]+" WIRESHARK_PLUGIN_ABI "${WIRESHARK_VERSION}")
endif()

if(NOT WIRESHARK_PLUGIN_ABI)
    set(WIRESHARK_PLUGIN_ABI "4.6")
endif()

set(FAOLAN_PERSONAL_PLUGIN_DIR "$ENV{HOME}/.local/lib/wireshark/plugins/${WIRESHARK_PLUGIN_ABI}/epan"
    CACHE PATH "Wireshark personal EPAN plugin directory")

add_library(faolan_stream STATIC
    src/faolan_conan_stream.c
    src/faolan_conan_framer.c
)

target_include_directories(faolan_stream
    PUBLIC
        "${CMAKE_CURRENT_SOURCE_DIR}/src"
)

target_link_libraries(faolan_stream
    PUBLIC
        ZLIB::ZLIB
)

if(BUILD_WIRESHARK_PLUGIN)
    add_library(faolan_gs_zlib MODULE
        wireshark/plugin.c
        wireshark/packet-faolan-gameserver-zlib.c
        src/faolan_conan_stream.c
        src/faolan_conan_framer.c
    )

    set_target_properties(faolan_gs_zlib PROPERTIES
        PREFIX ""
        OUTPUT_NAME "faolan_gs_zlib"
    )

    target_include_directories(faolan_gs_zlib
        PRIVATE
            "${CMAKE_CURRENT_SOURCE_DIR}/src"
            "${CMAKE_CURRENT_SOURCE_DIR}/wireshark"
    )

    target_link_libraries(faolan_gs_zlib
        PRIVATE
            PkgConfig::WIRESHARK
            ZLIB::ZLIB
    )

    if(INSTALL_PLUGIN_TO_PERSONAL_DIR)
        install(TARGETS faolan_gs_zlib
            LIBRARY DESTINATION "${FAOLAN_PERSONAL_PLUGIN_DIR}")

        add_custom_target(install-faolan-plugin
            COMMAND ${CMAKE_COMMAND} -E make_directory "${FAOLAN_PERSONAL_PLUGIN_DIR}"
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
                "$<TARGET_FILE:faolan_gs_zlib>"
                "${FAOLAN_PERSONAL_PLUGIN_DIR}/"
            DEPENDS faolan_gs_zlib
            COMMENT "Copying faolan_gs_zlib plugin to ${FAOLAN_PERSONAL_PLUGIN_DIR}"
        )
    endif()
endif()

if(BUILD_STANDALONE_TEST)
    add_executable(faolan_stream_tests
        src/faolan_stream_tests.c
    )

    target_link_libraries(faolan_stream_tests
        PRIVATE
            faolan_stream
    )
endif()
