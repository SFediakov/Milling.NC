cmake_minimum_required(VERSION 3.20)
project(miller_serial C)

# Serial ports of the machine connection as one shared library: miller_serial.dll on Windows (static
# C runtime), libmiller_serial.so on Linux. A cluster of its own, separate from the toolpath
# generation in miller_native.

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_C_EXTENSIONS OFF)

if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
  set(CMAKE_BUILD_TYPE Release)
endif()

add_library(miller_serial SHARED ${CMAKE_CURRENT_SOURCE_DIR}/src/ms_serial.c)
target_include_directories(miller_serial PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/include)
target_compile_definitions(miller_serial PRIVATE MS_BUILDING)

set(MILLER_SERIAL_OUTPUT ${CMAKE_CURRENT_BINARY_DIR}/out)
set_target_properties(miller_serial PROPERTIES
  RUNTIME_OUTPUT_DIRECTORY ${MILLER_SERIAL_OUTPUT}
  LIBRARY_OUTPUT_DIRECTORY ${MILLER_SERIAL_OUTPUT}
  RUNTIME_OUTPUT_DIRECTORY_RELEASE ${MILLER_SERIAL_OUTPUT}
  LIBRARY_OUTPUT_DIRECTORY_RELEASE ${MILLER_SERIAL_OUTPUT}
  C_VISIBILITY_PRESET hidden)

if(MSVC)
  set_property(TARGET miller_serial PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded")
  target_compile_options(miller_serial PRIVATE /W4 /WX /O2 /utf-8)
  target_link_libraries(miller_serial PRIVATE advapi32)
else()
  target_compile_options(miller_serial PRIVATE -Wall -Wextra -Werror -O2)
  target_link_options(miller_serial PRIVATE -static-libgcc)
endif()
