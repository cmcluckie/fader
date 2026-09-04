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
    # Core Audio only; no GUI event loop needed.
    JUCE_MODAL_LOOPS_PERMITTED=0
)

target_link_libraries(fk-engine
    PRIVATE
        juce::juce_audio_devices   # AudioDeviceManager, Core Audio
        juce::juce_dsp             # FFT + windowing used by FeedbackDetector
        juce::juce_osc             # loopback control/telemetry
    PUBLIC
        juce::juce_recommended_config_flags
        juce::juce_recommended_lto_flags
        juce::juce_recommended_warning_flags
)

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
