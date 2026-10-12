# Native side of Atmos Visualizer: libear (ITU-R BS.2127) plus a small extern "C" wrapper
# (ear_shim.dll) that Unity calls through P/Invoke.
#
# Build with build.cmd (sets up the MSVC environment and runs CMake + Ninja).

cmake_minimum_required(VERSION 3.21)

# libear declares cmake_minimum_required(3.5), which would leave newer policies OFF inside it.
# CMP0091 lets CMAKE_MSVC_RUNTIME_LIBRARY apply to libear too, so every object file uses the
# same (static) C runtime and the DLL doesn't depend on the VC++ redistributable.
set(CMAKE_POLICY_DEFAULT_CMP0091 NEW)

project(AtmosNative LANGUAGES CXX)

set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

set(EXTERNAL_DIR ${CMAKE_CURRENT_SOURCE_DIR}/../external)

if(NOT EXISTS ${EXTERNAL_DIR}/libear/CMakeLists.txt)
  message(FATAL_ERROR "libear is missing: run  git submodule update --init --recursive")
endif()

# libear needs Boost headers only (Optional, Variant): nothing to build. CMake downloads the
# release once into the build folder and checks it against its SHA-256. A copy already unpacked
# in external/boost-1.92.0 is used instead, without downloading.
include(FetchContent)
set(BOOST_VERSION 1.92.0)
if(EXISTS ${EXTERNAL_DIR}/boost-${BOOST_VERSION}/boost/version.hpp AND NOT DEFINED FETCHCONTENT_SOURCE_DIR_BOOST_HEADERS)
  set(FETCHCONTENT_SOURCE_DIR_BOOST_HEADERS ${EXTERNAL_DIR}/boost-${BOOST_VERSION})
endif()
FetchContent_Declare(boost_headers
  URL https://github.com/boostorg/boost/releases/download/boost-${BOOST_VERSION}/boost-${BOOST_VERSION}-b2-nodocs.tar.xz
  URL_HASH SHA256=ea7b982002cc9dfbe59b0b217b206f470dc75f3de0bb2973d844118934d82411
  DOWNLOAD_EXTRACT_TIMESTAMP TRUE
  SOURCE_SUBDIR headers-only)   # no CMakeLists.txt there, so Boost itself isn't configured or built
FetchContent_MakeAvailable(boost_headers)
set(BOOST_ROOT ${boost_headers_SOURCE_DIR})
set(Boost_NO_SYSTEM_PATHS ON)

# Deterministic builds: no timestamps in the objects or the DLL, so the same source always gives
# the identical ear_shim.dll and a rebuild doesn't show up as a change in git.
if(MSVC)
  add_compile_options(/Brepro)
  add_link_options(/Brepro)
endif()

set(EAR_UNIT_TESTS OFF CACHE BOOL "" FORCE)
set(EAR_EXAMPLES OFF CACHE BOOL "" FORCE)
set(EAR_PACKAGE_AND_INSTALL OFF CACHE BOOL "" FORCE)
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
add_subdirectory(${EXTERNAL_DIR}/libear libear EXCLUDE_FROM_ALL)

# libear's generated export header marks symbols dllimport unless this is defined, which is
# wrong when it is linked statically into our DLL.
target_compile_definitions(ear PUBLIC EAR_STATIC_DEFINE)

add_library(ear_shim SHARED src/ear_shim.cpp src/allocentric.cpp src/allocentric.hpp include/ear_shim.h)
target_include_directories(ear_shim PUBLIC include)
target_link_libraries(ear_shim PRIVATE ear)
target_compile_features(ear_shim PRIVATE cxx_std_17)
target_compile_definitions(ear_shim PRIVATE EAR_SHIM_BUILD)

# Record which libear commit was built, so es_version() can report it.
find_package(Git QUIET)
if(GIT_FOUND)
  execute_process(COMMAND ${GIT_EXECUTABLE} rev-parse --short=12 HEAD
                  WORKING_DIRECTORY ${EXTERNAL_DIR}/libear
                  OUTPUT_VARIABLE LIBEAR_COMMIT OUTPUT_STRIP_TRAILING_WHITESPACE)
  target_compile_definitions(ear_shim PRIVATE LIBEAR_COMMIT="${LIBEAR_COMMIT}")
endif()
if(MSVC)
  target_compile_options(ear_shim PRIVATE /W4 /EHsc /utf-8)
endif()
