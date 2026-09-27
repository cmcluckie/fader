cmake_minimum_required(VERSION 3.22)
project(fk-engine VERSION 0.1.0 LANGUAGES C CXX)

# Headless audio engine for the X32 tray controller (see docs/adr/0001-...).
# No GUI modules are linked: this process owns Core Audio, the FFT detector, and
# the notch bank, and talks to the C# app over OSC on loopback. The GUI lives in
# the tray app.

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_OSX_DEPLOYMENT_TARGET "11.0" CACHE STRING "" FORCE)

include(FetchContent)
FetchContent_Declare(
    JUCE
    GIT_REPOSITORY https://github.com/juce-framework/JUCE.git
    GIT_TAG        master        # pin to a release tag once a build is proven
    GIT_SHALLOW    TRUE
)
FetchContent_MakeAvailable(JUCE)

juce_add_console_app(fk-engine
    PRODUCT_NAME "fk-engine"
)

target_sources(fk-engine PRIVATE
    Source/EngineMain.cpp
)

target_compile_definitions(fk-engine PRIVATE
    JUCE_WEB_BROWSER=0
    JUCE_USE_CURL=0
    JUCE_STANDALONE_APPLICATION=1
    # No GUI event loop needed.
    JUCE_MODAL_LOOPS_PERMITTED=0
)

# Windows: ASIO for a low-latency insert (a Focusrite Scarlett's own driver),
# with WASAPI still offered beside it. JUCE bundles the ASIO headers, so there
# is nothing to download - but a binary built with them is bound by Steinberg's
# licence (GPLv3, or their proprietary licence by signed agreement). See
# _deps/juce-src/modules/juce_audio_devices/native/asio/LICENSE.txt before
# distributing the engine to anyone else.
if(WIN32)
    target_compile_definitions(fk-engine PRIVATE JUCE_ASIO=1)
endif()

target_link_libraries(fk-engine
    PRIVATE
        juce::juce_audio_devices   # AudioDeviceManager, Core Audio
        juce::juce_audio_formats   # the flight recorder's WAV writer
        juce::juce_dsp             # FFT + windowing used by FeedbackDetector
        juce::juce_osc             # loopback control/telemetry
    PUBLIC
        juce::juce_recommended_config_flags
        juce::juce_recommended_lto_flags
        juce::juce_recommended_warning_flags
)

# ---------------------------------------------------------------------------
# fk-fuzz. A random feedback generator built from the loop equations, and a
# score. Draws modes, not shapes: below Schroeder a draw lands one isolated mode
# (a fast spike), above it the same draw lands several inside one bandwidth (a
# slow plateau). The loop closes through the notch bank, so a filter that lands
# makes the ring decay and one that misses does not.
#
#   cmake --build build --target fk-fuzz
#   ./build/fk-fuzz_artefacts/Release/fk-fuzz 10 1      # 10 minutes, seed 1
# ---------------------------------------------------------------------------
juce_add_console_app(fk-fuzz PRODUCT_NAME "fk-fuzz")

target_sources(fk-fuzz PRIVATE tests/test_fuzz.cpp)

target_compile_definitions(fk-fuzz PRIVATE
    JUCE_WEB_BROWSER=0
    JUCE_USE_CURL=0
    JUCE_STANDALONE_APPLICATION=1
)

target_link_libraries(fk-fuzz
    PRIVATE juce::juce_dsp
    PUBLIC  juce::juce_recommended_config_flags)

# ---------------------------------------------------------------------------
# fk-render. Voice + physics-generated feedback through the guard, written out
# as WAVs you can listen to, plus how much of the SINGER survived - measured in
# the units the ear charges in.
#
#   cmake --build build --target fk-render
#   ./build/fk-render_artefacts/Release/fk-render --out /tmp/take --budget 30 --duty 20
# ---------------------------------------------------------------------------
juce_add_console_app(fk-render PRODUCT_NAME "fk-render")

target_sources(fk-render PRIVATE tests/test_render.cpp)

target_compile_definitions(fk-render PRIVATE
    JUCE_WEB_BROWSER=0
    JUCE_USE_CURL=0
    JUCE_STANDALONE_APPLICATION=1
)

target_link_libraries(fk-render
    PRIVATE juce::juce_dsp juce::juce_audio_formats
    PUBLIC  juce::juce_recommended_config_flags)

# ---------------------------------------------------------------------------
# Detector tests. Synthetic signals through the real FeedbackDetector - no audio
# device, no rig. Every gap in this detector so far was found in a loud room by
# ear; these make the same questions cheap and repeatable.
#
#   cmake --build build --target fk-tests && ./build/fk-tests_artefacts/fk-tests
# ---------------------------------------------------------------------------
juce_add_console_app(fk-tests PRODUCT_NAME "fk-tests")

target_sources(fk-tests PRIVATE tests/test_detector.cpp)

target_compile_definitions(fk-tests PRIVATE
    JUCE_WEB_BROWSER=0
    JUCE_USE_CURL=0
    JUCE_STANDALONE_APPLICATION=1
)

target_link_libraries(fk-tests
    PRIVATE juce::juce_dsp
    PUBLIC  juce::juce_recommended_config_flags)

# ---------------------------------------------------------------------------
# fk-loop — closed-loop feedback simulation.
#
# Every test in fk-tests is open loop: inject a tone, ask whether it was found.
# None of them can answer whether a notch actually STOPS feedback, which is why
# that question kept having to be settled on a stage. This puts the real detector
# and the real notch bank inside a real loop and measures Added Stable Gain.
#
#   cmake --build build --target fk-loop && ./build/fk-loop_artefacts/Release/fk-loop
# ---------------------------------------------------------------------------
juce_add_console_app(fk-loop PRODUCT_NAME "fk-loop")

target_sources(fk-loop PRIVATE tests/test_loop.cpp)

target_compile_definitions(fk-loop PRIVATE
    JUCE_WEB_BROWSER=0
    JUCE_USE_CURL=0
    JUCE_STANDALONE_APPLICATION=1
)

target_link_libraries(fk-loop
    PRIVATE juce::juce_dsp
    PUBLIC  juce::juce_recommended_config_flags)

# Build:
#   brew install cmake            # not present in the dev sandbox
#   cmake -B build -G "Unix Makefiles"   # or -G Xcode if full Xcode is installed
#   cmake --build build --config Release
# Binary: build/fk-engine_artefacts/Release/fk-engine

# MSVC only defines M_PI when asked; the tests use it for their synthetic signals.
if(MSVC)
    foreach(t fk-tests fk-loop fk-fuzz fk-render)
        if(TARGET ${t})
            target_compile_definitions(${t} PRIVATE _USE_MATH_DEFINES)
        endif()
    endforeach()
endif()
