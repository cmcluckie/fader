namespace Fader.Bridge;

/// <summary>
/// Conversion between the FaderPort's 14-bit MCU fader position and the X32's
/// normalised fader float.
///
/// These are NOT linearly equivalent in dB. The X32's fader float maps onto its
/// own taper, where roughly:
///     0.0  = -inf dB      0.50 ≈ -10 dB
///     0.75 ≈   0 dB       1.0  = +10 dB
/// The FaderPort's physical throw is close to a standard console taper, so a
/// straight linear position map tracks acceptably and is what ships here.
/// </summary>
public static class FaderScaling
{
    public const int McuMax = 16383;

    /// <summary>MCU 14-bit fader position (0-16383) to X32 fader float (0.0-1.0).</summary>
    public static float McuToX32(int mcu)
    {
        // ---- TUNE HERE ----------------------------------------------------
        // Straight linear position map. If the FaderPort's printed dB markings
        // don't line up with the console's readout, shape the curve here (and
        // invert the same shaping in X32ToMcu below so the round trip stays
        // stable). Everything downstream works in X32 float, so this function
        // and its inverse are the only places the taper is defined.
        return Math.Clamp(mcu / (float)McuMax, 0f, 1f);
    }

    /// <summary>X32 fader float (0.0-1.0) back to an MCU 14-bit motor position.</summary>
    public static int X32ToMcu(float x32)
    {
        // Must remain the exact inverse of McuToX32, or a fader will creep every
        // time a value round-trips through the console and back to the motor.
        return (int)Math.Round(Math.Clamp(x32, 0f, 1f) * McuMax);
    }
}
