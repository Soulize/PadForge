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
6. In PadForge's impulse-trigger page, enable
   **Translate Adaptive Trigger Vibrations** if the game uses supported
   DualSense vibration-type trigger programs.
7. Disable the new toggle to revert to PadForge's original SDL output path.

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
| Output command rate | Configurable 10–60 ms, default 30 ms (~33.3 Hz) |
| Lost-input watchdog | Stop demand after 250 ms of no fresh tick |
| Controller disconnect | Invalidate cached HID path before re-probe |
| Unassignment / app shutdown | Queue/synchronously write all-zero motor command |
| Feature compatibility | Opt-in and XML-persisted, off for prior installations |
| Automated verification | Packet, checksum, motor isolation, selection, timing and XML tests |

**The default timing target is conservative** rather than measured: a 30 ms inter-report
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
