cmake_minimum_required(VERSION 3.22)
project(fk-engine VERSION 0.1.0 LANGUAGES CXX)

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

# Build:
#   brew install cmake            # not present in the dev sandbox
#   cmake -B build -G "Unix Makefiles"   # or -G Xcode if full Xcode is installed
#   cmake --build build --config Release
# Binary: build/fk-engine_artefacts/Release/fk-engine
