cmake_minimum_required(VERSION 3.16)

if(NOT DEFINED CMAKE_TOOLCHAIN_FILE)
  if(DEFINED ENV{VITASDK})
    set(CMAKE_TOOLCHAIN_FILE "$ENV{VITASDK}/share/vita.toolchain.cmake" CACHE PATH "toolchain file")
  else()
    message(FATAL_ERROR "Set VITASDK to the vitasdk folder.")
  endif()
endif()

project(dfubootmusic C)
include("${VITASDK}/share/vita.cmake" REQUIRED)

add_executable(dfubootmusic main.c)
target_compile_options(dfubootmusic PRIVATE -O2 -Wall)
target_link_libraries(dfubootmusic
  SceLibKernel_stub
  SceIofilemgr_stub
  SceKernelThreadMgr_stub
  SceAudio_stub
  SceSysmem_stub
)
set_target_properties(dfubootmusic PROPERTIES LINK_FLAGS "-nostdlib")

vita_create_self(dfubootmusic.suprx dfubootmusic CONFIG exports.yml UNSAFE)
