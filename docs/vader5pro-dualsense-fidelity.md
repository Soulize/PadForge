# DualSense Fidelity for Vader 5 Pro (amplitude-only HID)

Status: software converter implemented; physical DualSense/Vader motor responses **not measured**. This supersedes the previous experimental P0/P1/P2 default 1ms peak emphasis and 6ms/10ms synthetic pulse holds.

## Goal and known limits

Approximate **relative tactile intensity, source event duration, left/right placement and weak-texture contrast**, not parity with Xbox vibration or upstream PadForge's 1.4x gain.

OpenVader documents native command 0x12 as four independent 8-bit *amplitude* bytes (grip L/R, trigger L/R), not programmable waveform or frequency. A Vader motor cannot be instructed to reproduce arbitrary DualSense voice-coil PCM. A measured motor calibration can improve amplitude/timing fidelity, but cannot guarantee a fully equivalent mechanical sensation.

## Fidelity DSP

- With native four-motor HID enabled, the existing Enhanced PCM toggle is labeled **DualSense Fidelity**. Disabling it retains the original 8ms EMA plus 1.4x RMS path as a comparison.
- **PCM RMS per side** is the default output level. No blanket gain, default peak boost, amplitude-dependent attack, decaying envelope or arbitrary fixed 6/10ms "impact".
- Streaming two-pole IIR crossovers at approximately **120Hz and 400Hz** estimate low, mid, high band energies. The grip level is raw RMS multiplied by the square root of a normalized, weighted band-energy ratio. This makes **weights 100/100/100 exactly neutral** and guarantees a non-boosting band transform for 0..100% attenuation weights. Band weighting is active in grip estimation but cannot be claimed a calibrated psychophysical weighting until compared to reference measurements.
- Per-side continuous **3ms silence detection** triggers explicit stop without interpreting isolated sine zero crossings as silence. Stream gaps >30ms, format changes and channel routing changes reset the filter.
- A short signal that starts and ends inside a single host PCM callback is emitted at its proportional callback RMS for its *counted non-silent samples* (so two separate taps do not turn their intervening silent gap into vibration). The only minimum is the 3ms native HID start/stop edge interval. No fixed 6ms or 10ms pulse is added; if the motor physically cannot reproduce a tiny cue, fidelity favors a weak/missed texture over a fabricated hard kick.
- **Optional transient impact boost** (off by default) re-enables the earlier gated 1ms peak emphasis without changing the measured pulse duration.
- High-frequency supplemental trigger routing remains off by default, because it may invent trigger effects absent from source game design. Native/author-trigger rumble retains priority.

## Four-motor compensation

The original Gain/Gamma/Floor controls remain available, but for fidelity **set all four to Gain=100%, Gamma=100%, Floor=0**.

Each motor has a separate **9-knot monotone response LUT**, mapping desired relative intensity at 0%, 12.5%, ... 100% to native 8-bit HID command at that intensity. Each CSV row has 9 comma-separated integers in 0..255 order. The first value MUST be zero and the sequence must never decrease; a zero command remains zero even with calibrated lower-end drive compensation.

Neutral identity curve:

~~~
0,32,64,96,128,159,191,223,255
~~~

These are **unmeasured defaults**, not magic DualSense coefficients. LUTs apply to native HID reports and individual motor test pulses, after Gain/Gamma/Floor. The motor-test level slider is independently adjustable from **0 to 255**; set the LUT to identity before measuring raw actuator strength at each level, then enter the inverse nine-knot lookup and retest. Nonneutral legacy sliders can still introduce intentional distortion.

All four curves and the three band weights persist in AppSettingsData XML. Settings → Input engine offers an **Export/Import fidelity calibration JSON** button pair. The versioned JSON file includes LUTs, band weights, and the four Gain/Gamma/Floor triplets. It rejects malformed, descending, out-of-range or incompatible presets *before* changing live settings.

### Calibration JSON example

~~~json
{
  "format": "padforge-vader5pro-fidelity",
  "version": 1,
  "motorLuts": [
    [0, 32, 64, 96, 128, 159, 191, 223, 255],
    [0, 32, 64, 96, 128, 159, 191, 223, 255],
    [0, 32, 64, 96, 128, 159, 191, 223, 255],
    [0, 32, 64, 96, 128, 159, 191, 223, 255]
  ],
  "bandWeights": [100, 100, 100],
  "gainGammaFloor": [
    [100, 100, 0],
    [100, 100, 0],
    [100, 100, 0],
    [100, 100, 0]
  ],
  "calibrationNotes": "Unmeasured default unless calibrated with physical vibration measurements."
}
~~~

## Proposed physical calibration (not performed by CI)

Use an original DualSense and a Vader under comparable mounting/grip conditions. Drive reference DualSense with known reproducible haptic PCM windows, then measure local grip acceleration (small accelerometers). Separately step each Vader HID motor through known levels and measure its acceleration/start/stop response. From the measured device response create a monotone inverse command LUT at the 9 desired points. When a reference spectrum is known, attenuate unreproducible bands (without boosting signals absent in the source). Verify both low-level textures and large impacts; test wired and wireless paths separately.

Without a DualSense reference and accelerometers, subjective A/B testing can still guide small changes, but cannot establish objective intensity parity.

## Software acceptance

| Gate | Required |
|---|---|
| 200Hz sustained sine, multiple levels | default output within 3% of raw source RMS |
| Fully-contained 3ms impulse | output within 7% of PCM window RMS, duration 3ms |
| Boost mode | off by default and no peak expansion until opt-in |
| 4ms input pulse | 4ms replay deadline, never forced to 6/10ms |
| Silence and zero crossings | 3ms confirmed stop, no mistaken sine stop |
| Frequency weights | neutral exactly raw RMS; any attenuation cannot increase it |
| Left/right signal mapping | independent |
| Per-motor LUT | monotone, zero-preserving, identity within 1 byte |
| XML/JSON | validation, persistence, backward-compatible defaults |
| Windows CI | all targeted xUnit regressions, published win-x64 artifact |

The original ±3dB tactile-intensity target is an **aspirational hardware measurement**, not a CI-supported claim. Current native motor command values alone do not establish actual accelerations or human perceived similarity.

## Suggested evaluation setup

Enable native four-motor rumble and DualSense Fidelity. Disable transient boost, high-frequency trigger assist, and Audio Rumble. Set all four Gain/Gamma/Floor to 100/100/0, all three frequency weights to 100%, and all four LUTs to identity. Start with the safe 30ms ordinary report interval, and evaluate 20ms/15ms/10ms only if the controller firmware handles them without drops. Compare the same footsteps, cloth effects, collisions, sustained hums and left/right cues on both controllers before editing calibration.
