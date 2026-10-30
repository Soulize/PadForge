# Flydigi Vader 5 Pro: experimental four-motor HID output

This fork adds a **disabled-by-default**, single-controller Windows output backend
for the Flydigi Vader 5 Pro (黑武士 5 Pro, VID 37D7, PID 2401).

## Intended signal path

Game's native DualSense haptics and trigger effects
→ PadForge's existing virtual DualSense feedback / PCM-level reducer
→ per-device gain, rumble, macros, adaptive-trigger vibration translation
→ four independent 16-bit levels
→ Vader 5 Pro output worker (8-bit quantization)
→ Flydigi proprietary HID command 0x12
→ left/right grip and left/right trigger motors.

This is **not** waveform-perfect DualSense emulation. The controller exposes
four amplitude-controlled motors, not two DualSense audio haptic actuators.
The software samples and maps the haptic envelope; motor dynamics and output
refresh limit fidelity.

## Windows setup

1. Connect **one** Vader 5 Pro via USB-C or its original 2.4 GHz receiver.
2. Configure PadForge's virtual controller as **DualSense (PS5): Full** and
   assign the physical Vader 5 Pro to that slot.
3. In **Settings → Input engine**, enable
   **Experimental: Vader 5 Pro native four-motor rumble**.
4. If the Flydigi Space Station service holds the HID control interface
   exclusively, quit it for testing; consider turning **Flydigi enhanced
   protocol** off to prevent competing HID writers. Do not change Bluetooth
   or Switch mode to test NewXInput.
5. The adjacent **Motor output interval** slider defaults to 30 ms.
   The range is 10–60 ms. **Below 30 ms is experimental**: it may overrun
   the firmware queue and must not be treated as hardware-validated.
   The four **Test** buttons below it each play a single 400 ms pulse
   at strength 160/255. Test the left/right grips and triggers separately.
6. Select the physical **Vader 5 Pro** in the Pad slot's mapped-device
   dropdown. The **Force Feedback** tab now shows a separate left/right
   trigger-motor section, with overall trigger gain, test buttons and output
   meters. The **Impulse Triggers** tab is also visible even if SDL reports
   no trigger motors: the native HID writer exposes those motors directly.
   Both tabs edit the **same** settings.
7. Turn on **Translate Adaptive Trigger Vibrations** to convert supported
   DualSense vibration-type trigger programs into the two trigger motors.
   DualSense resistance-only programs are deliberately not mapped to rumble.
   The virtual DualSense haptic audio envelope primarily drives the grip
   rumble path; it does not imply that both trigger motors must vibrate in
   every game.
8. Disable the new toggle to revert to PadForge's original SDL output path.

A game's PC version must implement real DualSense haptic feedback. Merely
creating a virtual DualSense cannot add data absent from the game.

## HID contract

Hardware-tested protocol evidence:
[OpenVader PROTOCOL.md](https://github.com/FilipVajgand/openvader/blob/main/docs/PROTOCOL.md),
[OpenVader VaderDevice.rumble](https://github.com/FilipVajgand/openvader/blob/main/Sources/VaderKit/Device/VaderDevice.swift).

- Control collection: VID `37D7`, PID `2401`, usage page `FFA0`,
  usage `0001`.
- NewXInput Windows report ID `06`, followed by a 32-byte `5A A5` frame.
- Frame bytes: `5A A5 12 06 GL GR TL TR SUM ... zero padding`.
- Checksum: low byte of `12 + 06 + GL + GR + TL + TR`.
- GL/GR/TL/TR are four separate unsigned 8-bit motor strengths.
- Flydigi 0x12 is a volatile output command. **Do not write configuration
  or firmware commands** from this path.
- A 16-bit input level converts to an 8-bit output via rounded
  `input * 255 / 65535`.

Some third-party captures contain checksum-less 0x12 packets; this backend
uses OpenVader's checksummed request form. The Windows leading-report-ID
assumption and USB/2.4 GHz operation still require hands-on testing with
the actual firmware revision.

## Software acceptance criteria

| Property | Target / implementation |
|---|---|
| Four distinct outputs | 4 × independently addressable 0–255 |
| Hardware match | Exact VID/PID and usage page/usage |
| Single-pad safety | No output when vendor interface discovery is ambiguous |
| High-frequency polling | No Windows HID I/O on PadForge's input tick |
| Output command rate | Nonzero transitions: configurable 10–60 ms, default 30 ms; individual motor zero edges can use a 3 ms priority gate |
| Lost-input watchdog | Stop demand after 100 ms of no fresh input tick |
| Controller disconnect | Invalidate cached HID path before re-probe |
| Unassignment / app shutdown | Queue/synchronously write all-zero motor command |
| Feature compatibility | Opt-in and XML-persisted, off for prior installations |
| Report scheduler | Absolute last-write deadline, no extra full interval after premature wakeup |
| DualSense haptic PCM envelope | Vader-specific 8 ms RMS follower (other controllers retain 20 ms), 50 ms stale audio level for Vader (other controllers retain 100 ms) |
| Trigger UI | Force Feedback has separate LT/RT controls and meters; Impulse Triggers tab available on native Vader even when SDL trigger capability is false |
| Automated verification | Packet, checksum, motor isolation, selection, timing and XML tests |

**The default normal-update interval is conservative** rather than measured: a 30 ms inter-report
interval was discussed in Linux driver work. The HID controller's true
capacity, minimum sustained pulse and queue behavior are **unverified**
until USB traces and physical measurements are available.

## Required physical acceptance (not automated)

Use wired USB first. Record controller firmware, Windows build, PadForge commit,
Flydigi Space Station process status and (optionally) a USBPcap trace.

- With GL=255 only, verify left grip and no other motor. Repeat GR/TL/TR
  individually, then test 25%, 50% and 100% strengths.
- Hold four nonzero channels simultaneously for ≥30 seconds. Verify no
  disconnect, input latency regression, buzzing after disable, or hot-plug
  mis-routing.
- During a DualSense-enabled PC game, confirm a haptic event generates
  nonzero independent report fields; confirm trigger-vibration-type effects
  reach the trigger fields where enabled.
- Unassign and shut down PadForge during nonzero vibration. Every motor
  should stop. Unplug/replug to verify path re-discovery.
- Compare 30 ms cadence against 20 ms and 10 ms **only with USB trace
  logging and a controlled test**. Changing the slider below 30 ms
  is an explicit experimental override. Never label the input polling rate
  as the motor report rate.

Unsupported: automatic association of multiple simultaneous Vader 5 Pro
controllers, byte-perfect DualSense LRA waveform reproduction, and physical
validation in CI.


## Latency / rumble-tail investigation (2026-10-30)

The first firmware-working build reproduced several software causes for a
noticeable delay relative to a genuine PS5 DualSense:

1. The normal 30 ms output rate was applied to **stop commands** too.
   Stopping just after the last nonzero update could wait until the next
   nonzero-report deadline. One trigger stopping while the other kept
   vibrating also waited.
2. The worker used a fresh `WaitOne(OutputIntervalMs)` on each
   early wake. If the 1 kHz input changed before the report deadline,
   that restarted an entire 30 ms wait instead of sleeping for the
   **remaining** time.
3. No input for 250 ms was required to force zero. When a game/audio
   source disappeared without a final stop, that left old amplitudes in
   the controller.
4. The DualSense audio-to-rumble envelope had a 20 ms mean-square
   follower; even after the PCM samples turned zero, its RMS envelope
   could linger for ~160 ms from a strong cue to the 2% floor.

The subsequent fix uses absolute report deadlines, a 3 ms gap for motor
**zero edges**, a 100 ms input-watchdog, and a separate **8 ms** follower
and **50 ms** stale timeout for the opt-in Vader native motor sink. Other
devices retain the previous audio-envelope behavior.

The 3 ms priority gate is **only for an actual motor reaching zero**.
It is not a claim that the receiver can sustain 333 Hz nonzero feedback.
The normal 10–60 ms slider (30 ms default) still regulates ordinary
nonzero changes.

### Acceptance and interpretation

- Test a short left-grip pulse, a short right-trigger pulse and all
  motors together in a game and in the Pad page.
- In a synthetic 20 ms strong PCM + 80 ms silence test, the native Vader
  8 ms follower must reach zero while the unchanged 20 ms follower has
  not. The 100 ms lost-input and precise scheduler deadlines have
  deterministic unit tests.
- If the controller still feels late with **USB-C wired**, try **15 ms**
  then **10 ms** normal output cadence, checking for disconnects,
  dropped inputs and buzzing. **Do not assume 10 ms is safe** for every
  firmware and receiver.
- With a 2.4 GHz dongle, USB scheduling and dongle/firmware buffering
  can add delay or consume the entire backlog; none of this is measured
  by software-only CI.
- Four conventional amplitude-controlled motors have mechanical
  spin-up and spin-down time. They cannot instantly reproduce the
  native DualSense's voice-coil actuator waveform. The software may
  send the zero immediately and the physical motor may still coast.

## P0/P1/P2 enhanced haptic pipeline (2026-10-30)

Implementation branch: `feat/vader5pro-haptics-p0-p2`. This is intentionally
independent of the original four-motor working branch until Windows CI and
physical feedback are checked.

### P0: stop accuracy and short impacts

- **Enhanced DualSense PCM haptics** is enabled within the opt-in Vader
  four-motor mode by default. Turn it off to restore the previous 8 ms
  mean-square follower. Non-Vader controllers retain the original follower.
- Detect **three continuous milliseconds** of samples below the
  `0.0015` normalized PCM sample floor **per channel**, then discard both
  amplitude and IIR history for that channel. This avoids treating individual
  sinusoidal zero crossings as stop instructions. It also avoids the old
  ~63 ms 8 ms RMS-release tail when the audio actually goes silent.
- A gap of more than **30 ms** between processed PCM chunks, a nonmonotonic
  source timestamp, or a sample format/channel-layout change resets the
  filter state. Silent resumption cannot replay an old amplitude.
- Max **1 ms bucket RMS** and ordinary window RMS are combined for impact
  detection. A **1.5 ms attack / 8 ms non-silent release** gives rapid rise
  without generating a lengthy intentional tail. Left/right state is
  independent. If an entire short impact begins and ends in one host PCM
  callback, publish a **10 ms bounded impulse** and force its channel to
  zero by reader-side expiry even if the audio stream stops sending data.
- A PCM channel starting or stopping bypasses the usual **10 ms publication**
  gate. The writer prioritizes individual motor starts and stops using the
  existing **3 ms minimum inter-report gap**, while ongoing nonzero changes
  remain under the configurable **10-60 ms** rate control. This is a software
  scheduling policy, not a promise of 3 ms end-to-end motor response.

### P1: low/high bands and independent four-motor calibration

- The converter computes low and high energies with a **two-pole 180 Hz IIR
  splitter**, using bounded state and no per-frame heap allocations.
  Ordinary grip vibration continues to use the complete original left/right
  PCM envelope. High-band energy is exposed separately for optional
  supplementation; no arbitrary stereo-to-frequency swap is made.
- **Settings → Input engine** now contains a 4-row calibration table:
  **left grip / right grip / left trigger / right trigger**. Every row has
  its own **Gain (0-200%)**, **Gamma (50-180%, 100%=linear)** and
  **Floor (0-64/255)** sliders.
- Factory/default profile `100,100,0` per motor is **identity**: existing
  motor strength is preserved. Reducing Gamma increases quiet effects;
  increasing Floor helps motors with a hardware start deadzone, but can
  make low events harsher. **A zero command is always zero** regardless of
  Floor, and Gain=0 always silences a channel.
- All 12 parameters are saved to the application's `AppSettingsData` XML
  as `FlydigiVader5ProMotorCalibrationCsv`, a semicolon-separated sequence
  of `gain,gamma,floor` triples in GL/GR/TL/TR order. Invalid data fails to
  four identity curves; out-of-range integers are clamped.
- Calibration also affects the Settings page's four separate motor **Test**
  pulses, so each physical output can be tuned directly. Profile snapshots
  are immutable on the 1 kHz producer; the HID writer never reads an
  incompletely updated four-motor profile.

### P2: multi-source arbitration with native-trigger priority

- Game/native rumble, macros and constant force continue through the
  existing per-device gain/priority stack, including grip max-combine.
- Native impulse triggers, routed trigger feedback and translated
  DualSense adaptive trigger vibration keep full priority.
- **Optional high-frequency PCM trigger assist** is disabled by default.
  When enabled, high-band energy contributes at **35%** through the existing
  per-device trigger strength chain **only where that individual native
  trigger channel is idle**. This assists texture on four amplitude
  motors without overriding an authored trigger event. It never remaps
  left PCM to right motors.
- When the physical pad is not entitled to persona haptics (e.g. another
  device renders them directly), the supplemental PCM route is also
  suppressed. On firmware that cannot cleanly sustain the optional
  spectral route, leave assist off.

### Automated acceptance and hardware qualification

The GitHub workflow `.github/workflows/vader5pro-four-motor.yml` builds
Windows x64 and publishes a portable application artifact after
`Vader5ProEnhancedHapticsTests` plus the old Vader, persona, bass-shaker,
PCM-ownership and trigger-fold regression suites.

| Software criterion | Test / expectation |
|---|---|
| 80 Hz sustained sine | Never stopped by individual zero crossings |
| Per-channel stop | 5 ms all-zero PCM frame independently zeroes motor |
| 5 ms impact | Detectable envelope above 25k/65535 |
| Reopen after >30 ms gap | No stale phantom amplitude |
| PCM sample-rate/layout change | Previous filter state cleared |
| 80 Hz vs 700 Hz | Distinct low/high band-energy ordering |
| Four motor calibration | Identity at defaults, monotone output, zero stays zero |
| Mixed native + spectral triggers | Native force wins per channel; assist opt-in |
| HID start/stop transitions | 3 ms priority gap; steady changes keep normal pacing |
| XML persistence | Round-trip of toggles and 12 calibration numbers |

**Not measurable in GitHub CI:** actual controller spin-up/spin-down
latency, HID/receiver firmware queue capacity, 2.4 GHz jitter, and the
mechanical onset or tail of the four motors. Do not label software timing
targets as achieved physical latency. Qualify **10/15/20/30 ms** cadences
on USB-C with USB tracing and physical response measurements before
changing the conservative 30 ms normal-update default. Compare the same
synthetic waveform through SDL/XInput and the native `0x12` path.


## Quiet-texture fidelity adjustment (2026-10-30)

A real-controller comparison found frequent gentle DualSense cues (footsteps,
cloth/fabric movement) could emerge from Xbox-style amplitude motors as
unpleasant hard, repeated kicks. The prior fast renderer used
`max(full-window RMS, 0.85 * 1ms peak RMS) * 1.4` for **every** event,
classified **every** callback-contained signal as a 10ms impact, and used
1.5ms attack regardless of authored intensity. A low Gamma setting
further magnifies quiet signals.

This update separates the two signal classes **before** motor calibration:

- **Quiet texture:** proportionate full-window RMS; no blanket 1.4x gain or
  peak-RMS boost; no forceful 10ms impact hold. A genuine nonzero short
  texture can receive a bounded 6ms soft pulse; a sustained weak cue has a
  gentle ~12ms attack. Amplitude floor lowered to 0.4% to preserve low-level
  detail without a high minimum motor command.
- **Genuine impact:** optional smoothly gated 1ms peak contribution only
  above a significant absolute peak and crest factor. A callback-contained
  strong impact retains the previous 10ms bounded pulse and fast 1.5ms
  attack. Per-channel 3ms confirmed silence, 30ms gap-reset and urgent
  start/stop HID report rules remain unchanged.
- **Calibration:** reset both grip Gamma sliders to **100-120%** and Floor
  to 0 when assessing gentle texture. Gamma under 100% intentionally boosts
  weak levels and may reintroduce forceful tactile starts. Compare the new
  algorithm to the legacy follower with Enhanced PCM on/off. Do not rely on
  10ms test pulses to infer actual mechanical stopping performance.

Regression tests cover continuous low-amplitude tones, isolated 3ms weak
textures and **30 repeated footsteps**, all with output strictly below 10%
of maximum rumble, plus preserved strong impact and 10ms strong-event hold.
No claim of DualSense LRA waveform equivalence is made: four conventional
amplitude motors may have a physical start threshold and cannot reproduce
fine voice-coil textures exactly.
