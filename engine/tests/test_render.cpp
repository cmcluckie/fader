// fk-render - put a voice and real feedback through the guard, and listen.
//
//   ./fk-render [--in take.wav] [--out dir] [--budget N] [--duty N] [--plateau]
//
// Every measurement so far has been a number. This writes audio:
//
//   clean.wav      the voice alone, as recorded or synthesised
//   raw.wav        voice + feedback, guard off - what the room would do
//   guarded.wav    the same, through the guard
//
// ...plus the one number no simulator could give us before: how much of the
// SINGER survives. That is measured as the ear would charge for it - the
// difference between clean and guarded, per ERB band, weighted by where hearing
// is sharpest - rather than as a raw spectral distance, which would count a
// 12 kHz notch the same as one at 3 kHz.
//
// With no --in it synthesises a tenor: a glottal pulse train through four
// formants, with vibrato and a little breath. Not a sample - a source-filter
// model - so it is reproducible, free of anyone's copyright, and can be asked
// to sing any phrase. A real take is better evidence; this is what exists at
// 2 a.m. when there is nobody to sing.

#include <juce_audio_formats/juce_audio_formats.h>
#include <juce_dsp/juce_dsp.h>
#include <cstdio>
#include <random>
#include <string>
#include <vector>
#include "../Source/FeedbackDetector.h"
#include "../Source/NotchBank.h"

namespace
{
constexpr double kSR    = 48000.0;
constexpr int    kBlock = 64;

double erbHz (double f) { return 24.7 * (0.00437 * f + 1.0); }

double importance (double f)
{
    const double oct = std::log2 (f / 2500.0) / 1.6;
    return 1.0 / (1.0 + oct * oct);
}

/// A resonator, used here as a formant.
struct Formant
{
    double b0 = 0, a1 = 0, a2 = 0, z1 = 0, z2 = 0;
    void set (double f, double bw)
    {
        const double r = std::exp (-juce::MathConstants<double>::pi * bw / kSR);
        const double c = 2.0 * r * std::cos (2.0 * juce::MathConstants<double>::pi * f / kSR);
        a1 = c; a2 = -r * r; b0 = 1.0 - r;
    }
    double process (double x)
    {
        const double y = b0 * x + a1 * z1 + a2 * z2;
        z2 = z1; z1 = y;
        return y;
    }
};

/// A tenor, by source and filter rather than by sampling.
///
/// Tenor range is roughly C3-C5 (131-523 Hz). The source is a glottal pulse
/// train with -12 dB/octave rolloff; the filter is four formants. The phrase
/// walks up an arpeggio and holds, because held notes are where a guard does
/// its worst work.
std::vector<float> synthTenor (double seconds)
{
    std::vector<float> out ((size_t) (seconds * kSR), 0.0f);
    std::mt19937 rng (7);
    std::uniform_real_distribution<double> noise (-1.0, 1.0);

    // A4=440: C3, E3, G3, C4, E4, G4, C4 - an arpeggio a tenor would warm up on.
    const double notes[] = { 130.81, 164.81, 196.00, 261.63, 329.63, 392.00, 261.63 };
    const int    count   = (int) (sizeof notes / sizeof notes[0]);

    Formant f1, f2, f3, f4;
    double phase = 0.0, last = 0.0;

    for (size_t i = 0; i < out.size(); ++i)
    {
        const double t   = (double) i / kSR;
        const double pos = t / seconds * count;
        const int    n   = juce::jlimit (0, count - 1, (int) pos);
        const double inNote = pos - (double) n;

        // attack and release, so no note starts as a transient
        const double env = juce::jmin (1.0, juce::jmin (inNote / 0.12, (1.0 - inNote) / 0.12)) ;
        if (env <= 0.0) { out[i] = (float) (0.0005 * noise (rng)); continue; }

        // vibrato arrives after the note settles, as a singer's does
        const double vib = 0.018 * juce::jmin (1.0, juce::jmax (0.0, (inNote - 0.25) / 0.3))
                         * std::sin (2.0 * juce::MathConstants<double>::pi * 5.4 * t);
        const double f0  = notes[n] * (1.0 + vib);

        // glottal source: a pulse train, rolled off, plus breath
        phase += f0 / kSR;
        double pulse = 0.0;
        if (phase >= 1.0) { phase -= 1.0; pulse = 1.0; }
        const double src = 0.995 * last + (pulse - 0.5 * 0.02);   // -12 dB/oct-ish
        last = src;
        const double breath = 0.015 * noise (rng);

        // tenor vowel, somewhere around /a/ moving to /e/
        const double open = juce::jlimit (0.0, 1.0, inNote * 1.5);
        f1.set (juce::jmap (open, 700.0, 500.0),  90.0);
        f2.set (juce::jmap (open, 1200.0, 1700.0), 110.0);
        f3.set (2600.0, 160.0);
        f4.set (3200.0, 200.0);   // the singer's formant, which is what carries

        const double v = f1.process (src + breath) * 1.0
                       + f2.process (src + breath) * 0.55
                       + f3.process (src + breath) * 0.28
                       + f4.process (src + breath) * 0.22;

        out[i] = (float) juce::jlimit (-1.0, 1.0, v * env * 0.35);
    }
    return out;
}

bool writeWav (const juce::File& file, const std::vector<float>& data)
{
    file.deleteFile();
    juce::WavAudioFormat wav;
    std::unique_ptr<juce::FileOutputStream> stream (file.createOutputStream());
    if (stream == nullptr) return false;
    std::unique_ptr<juce::AudioFormatWriter> writer (
        wav.createWriterFor (stream.release(), kSR, 1, 24, {}, 0));
    if (writer == nullptr) return false;

    juce::AudioBuffer<float> buf (1, (int) data.size());
    std::copy (data.begin(), data.end(), buf.getWritePointer (0));
    return writer->writeFromAudioSampleBuffer (buf, 0, buf.getNumSamples());
}

/// Ear-weighted spectral difference between two takes, in dB-ERB: what the
/// guard removed from the singer, priced the way the ear charges for it.
double voiceLoss (const std::vector<float>& a, const std::vector<float>& b)
{
    constexpr int fft = 2048;
    juce::dsp::FFT f (11);
    juce::dsp::WindowingFunction<float> win ((size_t) fft, juce::dsp::WindowingFunction<float>::hann);

    std::vector<float> A ((size_t) fft * 2), B ((size_t) fft * 2);
    double total = 0.0; int frames = 0;

    for (size_t pos = 0; pos + fft < a.size(); pos += fft / 2)
    {
        std::fill (A.begin(), A.end(), 0.0f);
        std::fill (B.begin(), B.end(), 0.0f);
        std::copy (a.begin() + (long) pos, a.begin() + (long) pos + fft, A.begin());
        std::copy (b.begin() + (long) pos, b.begin() + (long) pos + fft, B.begin());
        win.multiplyWithWindowingTable (A.data(), (size_t) fft);
        win.multiplyWithWindowingTable (B.data(), (size_t) fft);
        f.performFrequencyOnlyForwardTransform (A.data());
        f.performFrequencyOnlyForwardTransform (B.data());

        const double binHz = kSR / fft;
        double frame = 0.0;
        for (int i = 2; i < fft / 2; ++i)
        {
            const double hz = i * binHz;
            if (hz < 100.0 || hz > 16000.0) continue;
            const double da = juce::Decibels::gainToDecibels ((double) A[(size_t) i], -120.0);
            const double db = juce::Decibels::gainToDecibels ((double) B[(size_t) i], -120.0);
            if (da < -70.0) continue;                       // silence is not loss
            frame += juce::jmax (0.0, da - db) * (binHz / erbHz (hz)) * importance (hz);
        }
        total += frame; ++frames;
    }
    return frames ? total / frames : 0.0;
}

/// Energy inside a band, over a stretch of the take. Broadband RMS cannot tell
/// feedback from singing - the first version of this reported "1.4 dB saved"
/// because the voice dominated both takes.
double bandDb (const std::vector<float>& v, size_t from, size_t to, double loHz, double hiHz)
{
    constexpr int fft = 2048;
    juce::dsp::FFT f (11);
    juce::dsp::WindowingFunction<float> win ((size_t) fft, juce::dsp::WindowingFunction<float>::hann);
    std::vector<float> X ((size_t) fft * 2);

    double sum = 0.0; int frames = 0;
    for (size_t pos = from; pos + fft < to && pos + fft < v.size(); pos += fft)
    {
        std::fill (X.begin(), X.end(), 0.0f);
        std::copy (v.begin() + (long) pos, v.begin() + (long) pos + fft, X.begin());
        win.multiplyWithWindowingTable (X.data(), (size_t) fft);
        f.performFrequencyOnlyForwardTransform (X.data());

        const double binHz = kSR / fft;
        double e = 0.0;
        for (int i = 2; i < fft / 2; ++i)
        {
            const double hz = i * binHz;
            if (hz >= loHz && hz <= hiHz) e += (double) X[(size_t) i] * X[(size_t) i];
        }
        sum += e; ++frames;
    }
    return frames ? juce::Decibels::gainToDecibels (std::sqrt (sum / frames), -120.0) : -120.0;
}

double rmsDb (const std::vector<float>& v, size_t from, size_t to)
{
    double sum = 0.0; size_t n = 0;
    for (size_t i = from; i < to && i < v.size(); ++i) { sum += (double) v[i] * v[i]; ++n; }
    return n ? juce::Decibels::gainToDecibels (std::sqrt (sum / n), -120.0) : -120.0;
}
}

int main (int argc, char* argv[])
{
    std::string inPath, outDir = ".";
    double budget = 0.0, duty = 1.0, periodMs = 12.0, masterDb = 0.0;
    bool plateau = false;
    for (int i = 1; i < argc; ++i)
    {
        const std::string a = argv[i];
        if (a == "--in"     && i + 1 < argc) inPath = argv[++i];
        if (a == "--out"    && i + 1 < argc) outDir = argv[++i];
        if (a == "--budget" && i + 1 < argc) budget = std::atof (argv[++i]);
        if (a == "--duty"   && i + 1 < argc) duty   = std::atof (argv[++i]) / 100.0;
        if (a == "--master" && i + 1 < argc) masterDb = std::atof (argv[++i]);
        if (a == "--plateau") plateau = true;
    }

    // ---- the voice ---------------------------------------------------------
    std::vector<float> voice;
    if (! inPath.empty())
    {
        juce::AudioFormatManager fm; fm.registerBasicFormats();
        std::unique_ptr<juce::AudioFormatReader> rd (fm.createReaderFor (juce::File (inPath)));
        if (rd == nullptr) { std::printf ("could not read %s\n", inPath.c_str()); return 1; }
        juce::AudioBuffer<float> buf ((int) rd->numChannels, (int) rd->lengthInSamples);
        rd->read (&buf, 0, buf.getNumSamples(), 0, true, true);
        voice.assign (buf.getReadPointer (0), buf.getReadPointer (0) + buf.getNumSamples());
        std::printf ("voice   : %s, %.1f s at %.0f Hz\n", inPath.c_str(),
                     (double) voice.size() / rd->sampleRate, rd->sampleRate);
    }
    else
    {
        voice = synthTenor (14.0);
        std::printf ("voice   : synthesised tenor, 14.0 s, C3-G4 arpeggio with vibrato\n");
    }

    // ---- the feedback ------------------------------------------------------
    // Two rings drawn from the physics rather than placed by ear: one spike
    // below Schroeder holding all its excess gain, one plateau above it sharing
    // the gain among several modes. Both start while the singer is singing.
    struct Mode { double f, excess, tau, level, phase; };
    std::vector<Mode> modes;
    // --master is dB ABOVE the level where the room starts to ring, and that is
    // exactly the excess loop gain: every dB of master is a dB more per round
    // trip. At +6 the spike grows at 600 dB/s and nothing can save it, which is
    // the point of being able to ask for it.
    modes.push_back ({ 2740.0, 2.2 + masterDb, 0.010, -62.0, 0.0 });      // fast spike
    for (int k = 0; k < 7; ++k)                                           // slow plateau
        modes.push_back ({ 300.0 + 34.0 * k, 0.22 + masterDb * 0.35, 0.018, -66.0, 0.7 * k });

    const double startSpike = 3.0, startPlate = 7.0;

    std::vector<float> raw (voice.size()), guarded (voice.size());

    fk::FeedbackDetector::Params p;
    p.floorDb = -95.0f;
    p.minFreq = 40.0f;
    if (plateau) p.plateauRiseDb = 10.0f;
    fk::FeedbackDetector det; det.prepare (kSR); det.setParams (p);

    fk::NotchBank<48> bank;
    bank.prepare (kSR, kBlock);
    bank.defaultQ = 12.0; bank.softCapDb = -18.0; bank.hardCapDb = -24.0;
    bank.initialCutDb = -6.0; bank.harmBudget = budget;
    // Pulsing lives in the bank, so the audio written out and the loop model see
    // the SAME suppression. The first version gated only the loop maths while
    // filtering the audio continuously, which flattered pulsing badly.
    bank.dutyCycle = duty; bank.pulseHz = 1000.0 / periodMs;

    auto ringsAt = [&] (double t, std::vector<Mode>& ms, int i) {
        double v = 0.0;
        for (size_t m = 0; m < ms.size(); ++m)
        {
            const double from = (m == 0) ? startSpike : startPlate;
            if (t < from) continue;
            v += std::pow (10.0, ms[m].level / 20.0) * std::sin (ms[m].phase);
            ms[m].phase += 2.0 * juce::MathConstants<double>::pi * ms[m].f / kSR;
        }
        juce::ignoreUnused (i);
        return v;
    };

    auto unguarded = modes;      // the same rings, with nothing fighting them
    for (size_t pos = 0; pos + kBlock < voice.size(); pos += kBlock)
    {
        const double t = (double) pos / kSR;

        // guard off
        for (int i = 0; i < kBlock; ++i)
            raw[pos + (size_t) i] = (float) juce::jlimit (-1.0, 1.0,
                (double) voice[pos + (size_t) i] + ringsAt (t, unguarded, i));
        for (auto& m : unguarded)
            if (t >= (&m == &unguarded[0] ? startSpike : startPlate))
                m.level = juce::jlimit (-100.0, 0.0, m.level + (m.excess / m.tau) * kBlock / kSR);

        // guard on
        float block[kBlock];
        for (int i = 0; i < kBlock; ++i)
            block[i] = (float) juce::jlimit (-1.0, 1.0,
                (double) voice[pos + (size_t) i] + ringsAt (t, modes, i));

        det.push (block, kBlock);
        fk::FeedbackDetector::Event ev;
        while (det.popEvent (ev))
            bank.trigger (ev.freq, t, ev.growing, ev.levelDb,
                          (double) (ev.widthHiHz - ev.widthLoHz), ev.path == 4, ev.runaway);

        bank.process (block, kBlock, false);
        bank.release (t);
        for (int i = 0; i < kBlock; ++i) guarded[pos + (size_t) i] = block[i];

        // the loop closes: each mode grows by what is left of its excess gain
        for (auto& m : modes)
        {
            if (t < (&m == &modes[0] ? startSpike : startPlate)) continue;
            const double cut = bank.effectiveCutAtDb (m.f);
            m.level = juce::jlimit (-100.0, 0.0, m.level + ((m.excess + cut) / m.tau) * kBlock / kSR);
        }
    }

    // ---- write and score ---------------------------------------------------
    const juce::File dir (juce::File::getCurrentWorkingDirectory().getChildFile (outDir));
    dir.createDirectory();
    writeWav (dir.getChildFile ("clean.wav"), voice);
    writeWav (dir.getChildFile ("raw.wav"), raw);
    writeWav (dir.getChildFile ("guarded.wav"), guarded);

    const size_t quiet = (size_t) (2.0 * kSR);                 // before any ring starts
    const size_t late  = voice.size() > (size_t) (2.0 * kSR) ? voice.size() - (size_t) (2.0 * kSR) : 0;

    std::printf ("config  : budget %.0f, duty %.0f%%%s, master %+.1f dB over threshold\n",
                 budget, duty * 100.0, plateau ? ", plateau path on" : "", masterDb);
    std::printf ("          spike grows at %.0f dB/s, plateau modes at %.0f dB/s\n",
                 (2.2 + masterDb) / 0.010, (0.22 + masterDb * 0.35) / 0.018);
    std::printf ("\nwrote %s/{clean,raw,guarded}.wav\n\n", dir.getFullPathName().toRawUTF8());
    // In the ring bands only, over the last stretch: this is the feedback.
    const double spikeRaw  = bandDb (raw,     late, raw.size(),     2640.0, 2840.0);
    const double spikeGrd  = bandDb (guarded, late, guarded.size(), 2640.0, 2840.0);
    const double plateRaw  = bandDb (raw,     late, raw.size(),      290.0,  560.0);
    const double plateGrd  = bandDb (guarded, late, guarded.size(),  290.0,  560.0);
    const double voiceRaw  = bandDb (voice,   late, voice.size(),    290.0,  560.0);

    std::printf ("  spike band 2.64-2.84 kHz   raw %6.1f   guarded %6.1f   SAVED %5.1f dB\n",
                 spikeRaw, spikeGrd, spikeRaw - spikeGrd);
    std::printf ("  plateau band 290-560 Hz    raw %6.1f   guarded %6.1f   SAVED %5.1f dB"
                 "   (clean voice there: %.1f)\n",
                 plateRaw, plateGrd, plateRaw - plateGrd, voiceRaw);
    std::printf ("  voice before any ring      clean %6.1f   guarded %6.1f dB\n",
                 rmsDb (voice, 0, quiet), rmsDb (guarded, 0, quiet));
    std::printf ("  VOICE LOST (dB-ERB, ear-weighted, clean vs guarded): %.2f\n", voiceLoss (voice, guarded));
    return 0;
}
