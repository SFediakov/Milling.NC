cmake_minimum_required(VERSION 3.20)
project(miller_native C)

# The whole toolpath generation of Miller as one shared library: miller_native.dll on Windows (static
# C runtime, no redistributable needed), libmiller_native.so on Linux. Floating point stays strict IEEE
# single precision without contraction so that the output matches the C# implementation bit for bit.

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_C_EXTENSIONS OFF)

if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
  set(CMAKE_BUILD_TYPE Release)
endif()

file(GLOB MILLER_NATIVE_SOURCES CONFIGURE_DEPENDS ${CMAKE_CURRENT_SOURCE_DIR}/src/*.c)
add_library(miller_native SHARED ${MILLER_NATIVE_SOURCES})
target_include_directories(miller_native PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/include ${CMAKE_CURRENT_SOURCE_DIR}/src)
target_compile_definitions(miller_native PRIVATE MN_BUILDING)

set(MILLER_NATIVE_OUTPUT ${CMAKE_CURRENT_BINARY_DIR}/out)
set_target_properties(miller_native PROPERTIES
  RUNTIME_OUTPUT_DIRECTORY ${MILLER_NATIVE_OUTPUT}
  LIBRARY_OUTPUT_DIRECTORY ${MILLER_NATIVE_OUTPUT}
  RUNTIME_OUTPUT_DIRECTORY_RELEASE ${MILLER_NATIVE_OUTPUT}
  LIBRARY_OUTPUT_DIRECTORY_RELEASE ${MILLER_NATIVE_OUTPUT}
  C_VISIBILITY_PRESET hidden)

if(MSVC)
  set_property(TARGET miller_native PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded")
  target_compile_options(miller_native PRIVATE /W4 /WX /fp:precise /O2 /utf-8)
else()
  target_compile_options(miller_native PRIVATE -Wall -Wextra -Werror -O2 -ffp-contract=off -fno-fast-math)
  find_package(Threads REQUIRED)
  target_link_libraries(miller_native PRIVATE m Threads::Threads)
  target_link_options(miller_native PRIVATE -static-libgcc)
endif()
