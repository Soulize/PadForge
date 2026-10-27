using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Xml.Serialization;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Common.Input.Falcon;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Common.ForceDimension;
using PadForge.Engine.Data;
using PadForge.Engine.RemoteLink;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Novint Falcon (issue #521): the feels and the rumble against
    /// FalconJoystick's and FalconJoyForce's formulas, the game's effects and
    /// their directions, the caps and the fade, the row's encoding and
    /// default mapping, the device's close on every way out, the settings,
    /// the driver package check and the test host's fences.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class FalconTests
    {
        private const double Tol = 1e-9;

        // ── The row keeps its number ──

        [Fact]
        public void TheFalconRow_IsDeviceType43_AndAnswersNoAnyDeviceSource()
        {
            Assert.Equal(43, InputDeviceType.NovintFalcon);
            Assert.False(InputDeviceType.AnswersAnyDeviceSources(InputDeviceType.NovintFalcon));
        }

        // ── The Joystick feel (FalconJoystick.cpp) ──

        [Fact]
        public void TheJoystickStick_IsFullAt85PercentOfTheReach()
        {
            var (x, y) = FalconJoystickFeel.Stick(0.425, 0);
            Assert.Equal(0.5, x, 12);
            Assert.Equal(0.0, y, 12);
            (x, y) = FalconJoystickFeel.Stick(0, 0.85);
            Assert.Equal(1.0, y, 12);
            (x, y) = FalconJoystickFeel.Stick(0.6, 0.8);
            Assert.Equal(0.6, x, 12);
            Assert.Equal(0.8, y, 12);
            Assert.Equal((0.0, 0.0), FalconJoystickFeel.Stick(0, 0));
        }

        [Fact]
        public void TheJoystickSpring_Pulls3NewtonsAtFull_AndTheEndStopBuildsPastIt()
        {
            // Half deflection: half of 3 N, toward center.
            var (fy, fz) = FalconJoystickFeel.Force(0.425, 0, 0, 0, 1.0, centering: true);
            Assert.Equal(-1.5, fy, 12);
            Assert.Equal(0.0, fz, 12);
            // Past 85%: the full 3 N plus ((r - 0.85) / 0.15)^2.3 of the 5 N
            // left under FORCE_MAX_N.
            (fy, _) = FalconJoystickFeel.Force(0.925, 0, 0, 0, 1.0, centering: true);
            Assert.Equal(-(3.0 + Math.Pow(0.5, 2.3) * 5.0), fy, 9);
            // Spring at 200%: 6 N, and only 2 N left for the end stop.
            (fy, _) = FalconJoystickFeel.Force(0.925, 0, 0, 0, 2.0, centering: true);
            Assert.Equal(-(6.0 + Math.Pow(0.5, 2.3) * 2.0), fy, 9);
            // A game's spring in place of the centering spring: the end stop
            // alone.
            (fy, _) = FalconJoystickFeel.Force(0.925, 0, 0, 0, 1.0, centering: false);
            Assert.Equal(-Math.Pow(0.5, 2.3) * 5.0, fy, 9);
        }

        [Fact]
        public void TheJoystickDamping_Resists_ButNeverPushesOutward()
        {
            // Moving out at 0.1 m/s adds 0.4 N of pull.
            var (fy, _) = FalconJoystickFeel.Force(0.425, 0, 0.1, 0, 1.0, centering: true);
            Assert.Equal(-1.9, fy, 12);
            // Moving in fast takes the pull to zero, never past it.
            (fy, _) = FalconJoystickFeel.Force(0.425, 0, -10.0, 0, 1.0, centering: true);
            Assert.Equal(0.0, fy, 12);
        }

        // ── The Aim feel (FalconJoyForce.cpp) ──

        /// <summary>FalconJoyForce's velocity curve, the <c>curve</c> lambda
        /// and its <c>evalZone</c> (FalconJoyForce.cpp:1542-1573), ported
        /// line for line with its constants (:66-75).</summary>
        private static double ReferenceCurve(double v)
        {
            static double EvalZone(double v, double sens, double crv)
            {
                double scaled = Math.Abs(v) * sens;
                if (scaled > 1.0) scaled = 1.0;
                return Math.Pow(scaled, crv);
            }
            if (Math.Abs(v) < 0.0001) return 0.0;
            double sign = v > 0.0 ? 1.0 : -1.0;
            double speed = Math.Abs(v);
            double ramp = (speed - 0.0001) / 0.004;
            ramp = ramp < 0.0 ? 0.0 : (ramp > 1.0 ? 1.0 : ramp);
            ramp = ramp * ramp * (3.0 - 2.0 * ramp);
            double vlow = EvalZone(v, 22.0, 0.80);
            double lo = EvalZone(v, 18.0, 0.88);
            double hi = Math.Pow(Math.Min(speed * 12.0, 1.0), 0.95);
            if (hi > 1.0) hi = 1.0;
            double tVlow = (speed - 0.004) / (0.025 - 0.004);
            tVlow = tVlow < 0.0 ? 0.0 : (tVlow > 1.0 ? 1.0 : tVlow);
            tVlow = tVlow * tVlow * (3.0 - 2.0 * tVlow);
            double lowBlended = vlow + (lo - vlow) * tVlow;
            double tHi = (speed - 0.025) / (0.15 - 0.025);
            tHi = tHi < 0.0 ? 0.0 : (tHi > 1.0 ? 1.0 : tHi);
            tHi = tHi * tHi * (3.0 - 2.0 * tHi);
            return sign * (lowBlended * (1.0 - tHi) + hi * tHi) * ramp;
        }

        [Fact]
        public void TheVelocityZones_AreFalconJoyForcesCurve()
        {
            Assert.Equal(0.0, FalconAimFeel.ZoneCurve(0.0));
            Assert.Equal(0.0, FalconAimFeel.ZoneCurve(FalconAimAxis.VelDeadzone / 2));
            Assert.Equal(1.0, FalconAimFeel.ZoneCurve(1.0), 12);
            for (double v = -0.3; v <= 0.3; v += 0.00037)
                Assert.Equal(ReferenceCurve(v), FalconAimFeel.ZoneCurve(v), 12);
        }

        [Fact]
        public void ThePush_LatchesAtTheEdge_SnapsToACardinal_AndHolds150Milliseconds()
        {
            var push = new FalconAimPush();
            double x = 0, y = 0;
            // Inside the entry radius: no push.
            Assert.False(push.Update(0.5, 0.1, 0.001, ref x, ref y));
            push.Reset();
            // At 0.85, mostly right, from rest: the push snaps to right.
            x = y = 0;
            Assert.True(push.Update(0.8, 0.2915, 0.001, ref x, ref y));
            double r = Math.Sqrt(0.8 * 0.8 + 0.2915 * 0.2915);
            double excess = (r - FalconAimPush.EnterRadius) / (1.0 - FalconAimPush.EnterRadius);
            Assert.Equal(FalconAimPush.SpeedBase + excess * (FalconAimPush.SpeedMax - FalconAimPush.SpeedBase), x, 9);
            Assert.Equal(0.0, y, 12);
            // Back inside the exit radius at once: held for 150 ms.
            x = y = 0;
            Assert.True(push.Update(0.5, 0, 0.1, ref x, ref y));
            Assert.True(push.InPush);
            x = y = 0;
            Assert.False(push.Update(0.5, 0, 0.06, ref x, ref y));
            Assert.Equal(0.0, x);
        }

        [Fact]
        public void APushUpward_RunsAtHalfSpeed()
        {
            var push = new FalconAimPush();
            double x = 0, y = 0;
            Assert.True(push.Update(0, 0.9, 0.001, ref x, ref y));
            double excess = (0.9 - FalconAimPush.EnterRadius) / (1.0 - FalconAimPush.EnterRadius);
            double mag = FalconAimPush.SpeedBase + excess * (FalconAimPush.SpeedMax - FalconAimPush.SpeedBase);
            Assert.Equal(0.0, x, 12);
            Assert.Equal(mag * FalconAimPush.VerticalScale, y, 9);
        }

        [Fact]
        public void TheWall_Builds8NewtonsBy95Percent_AndTheRidgeMarksThePushBorder()
        {
            // Full wall at 95%, popped past the ridge, at rest.
            var aim = new FalconAimFeel();
            var (fy, fz) = aim.Force(0.95, 0, 0.001);
            Assert.Equal(-8.0, fy, 9);
            Assert.Equal(0.0, fz, 12);

            // At 75%, under the wall, on the ridge: smoothstep(0.5) of 1.4 N.
            aim = new FalconAimFeel();
            (fy, _) = aim.Force(0.75, 0, 0.001);
            Assert.Equal(-0.7, fy, 9);

            // Popped, the ridge stays released until the handle is back
            // 2% inside the border, then the nudge fires once.
            aim = new FalconAimFeel();
            aim.Force(0.85, 0, 0.001);
            (fy, _) = aim.Force(0.80, 0, 0.001);
            double wallAt80 = Math.Pow((0.80 - FalconAimFeel.WallStart) / (FalconAimFeel.WallFull - FalconAimFeel.WallStart), 2.3) * 8.0;
            Assert.Equal(-wallAt80, fy, 9);
            (fy, _) = aim.Force(0.75, 0, 0.001);
            Assert.Equal(-(0.7 + FalconAimFeel.NudgePeakN), fy, 9);
        }

        // ── Rumble (FalconJoyForce.cpp) ──

        [Fact]
        public void TheKick_LandsOn8NewtonsAtFullScale_AndWeakShotsFadeFaster()
        {
            Assert.Equal(8.0, FalconRumble.KickN(FalconRumble.Fullscale), 12);
            Assert.Equal(8.0 * Math.Pow(0.25, 0.65), FalconRumble.KickN(1.0), 12);
            Assert.Equal(FalconRumble.DecayMin, FalconRumble.DecayFor(0), 12);
            Assert.Equal(FalconRumble.DecayMax, FalconRumble.DecayFor(FalconRumble.Fullscale), 12);
            // The floor holds a live force up, and lets a dying tail through.
            Assert.Equal(4.5, FalconRumble.Band(1.0, FalconRumble.KickMinN, 0), 12);
            Assert.Equal(0.03, FalconRumble.Band(0.03, FalconRumble.KickMinN, 0), 12);
            Assert.Equal(9.0, FalconRumble.Band(12.0, FalconRumble.ShakeMinN, FalconRumble.ShakeMaxN), 12);
        }

        [Fact]
        public void HeldKickButton_TurnsRumbleIntoAPushTowardThePlayer()
        {
            var rumble = new FalconRumble(1);
            var (x, _, _) = rumble.Step(1.0, 0.0, kickHeld: true, nowMs: 1000, dt: 0.001);
            Assert.Equal(FalconRumble.KickN(FalconRumble.RecoilLargeScale), x, 9);
            Assert.True(x > 0, "the kick pushes along +X, toward the player");
        }

        [Fact]
        public void RumbleWithoutTheKickButton_ShakesTheHandleInsideItsBand()
        {
            var rumble = new FalconRumble(2);
            double x = 0, y = 0, z = 0;
            for (int i = 0; i < 3000; i++)
                (x, y, z) = rumble.Step(0.2, 0.0, kickHeld: false, nowMs: i, dt: 0.001);
            Assert.Equal(0.0, x);
            // 0.2 of a motor is 1.1 N before the band, which holds it at 3.2.
            Assert.Equal(FalconRumble.ShakeMinN, Math.Sqrt(y * y + z * z), 3);
        }

        // ── A game's effects ──

        [Fact]
        public void AConstantForce_PushesAwayFromWhereItComesFrom()
        {
            // From the east (90 degrees): the handle is pushed left.
            var east = new FalconGameForce(0, 0, true, FfbEffectTypes.Const, 1.0, Math.PI / 2, 0, null, 1.0);
            var (fy, fz) = FalconEffects.Render(east, 0, 0, 0, 0, 0, 0, 8.0, 0);
            Assert.Equal(-8.0, fy, 9);
            Assert.Equal(0.0, fz, 9);
            // From the north: pushed down.
            var north = new FalconGameForce(0, 0, true, FfbEffectTypes.Const, 1.0, 0, 0, null, 1.0);
            (fy, fz) = FalconEffects.Render(north, 0, 0, 0, 0, 0, 0, 8.0, 0);
            Assert.Equal(0.0, fy, 9);
            Assert.Equal(-8.0, fz, 9);
        }

        private static ConditionAxisData Full() => new()
        {
            PositiveCoefficient = 10000, NegativeCoefficient = 10000,
            PositiveSaturation = 10000, NegativeSaturation = 10000,
        };

        [Fact]
        public void AGamesSpring_PullsBackToCenter_OnBothAxes()
        {
            var spring = new FalconGameForce(0, 0, true, FfbEffectTypes.Spring, 0, 0, 0, new[] { Full(), Full() }, 1.0);
            Assert.True(spring.IsCondition);
            Assert.True(spring.GameSpring);
            // Right of center: pushed left. Above center: pushed down, though
            // DirectInput's Y grows toward the player.
            var (fy, fz) = FalconEffects.Render(spring, 0.5, 0, 0, 0, 0, 0, 8.0, 0);
            Assert.Equal(-4.0, fy, 9);
            Assert.Equal(0.0, fz, 9);
            (fy, fz) = FalconEffects.Render(spring, 0, 0.5, 0, 0, 0, 0, 8.0, 0);
            Assert.Equal(0.0, fy, 9);
            Assert.Equal(-4.0, fz, 9);
            // One axis acts on both.
            var single = new FalconGameForce(0, 0, true, FfbEffectTypes.Spring, 0, 0, 0, new[] { Full() }, 1.0);
            (fy, fz) = FalconEffects.Render(single, -0.25, -0.25, 0, 0, 0, 0, 8.0, 0);
            Assert.Equal(2.0, fy, 9);
            Assert.Equal(2.0, fz, 9);
        }

        [Fact]
        public void AGamesDamper_ResistsMotion_At20NewtonsPerMeterPerSecond()
        {
            var damper = new FalconGameForce(0, 0, true, FfbEffectTypes.Damper, 0, 0, 0, new[] { Full(), Full() }, 1.0);
            var (fy, _) = FalconEffects.Render(damper, 0, 0, 0.1, 0, 0, 0, 8.0, 0);
            Assert.Equal(-2.0, fy, 9);
            // Saturation holds it, as the wheel path holds it.
            var held = Full();
            held.PositiveSaturation = 2500;
            held.NegativeSaturation = 2500;
            damper = new FalconGameForce(0, 0, true, FfbEffectTypes.Damper, 0, 0, 0, new[] { held, held }, 1.0);
            (fy, _) = FalconEffects.Render(damper, 0, 0, 1.0, 0, 0, 0, 8.0, 0);
            Assert.Equal(-2.0, fy, 9);
        }

        [Fact]
        public void TheMotors_ComeBackFromOverall_SoTheForceModelAppliesItOnce()
        {
            var game = FalconGameForce.From(new Vibration(), 16384, 0, 0.5);
            Assert.Equal(16384 / 65535.0 / 0.5, game.Heavy, 9);
            Assert.Equal(0.0, game.Light);
            Assert.False(game.HasEffect);
            Assert.Same(FalconGameForce.Silent, FalconGameForce.From(null, 1, 1, 1));
        }

        // ── The controller: unassigned, the fade, the caps ──

        private static readonly FalconReach Reach = FalconReach.Default;

        private static FalconSample At(double offY, double offZ, uint buttons = 0)
            => new(0, offY * Reach.Y, offZ * Reach.Z, buttons);

        private static FalconSettings Joystick(double cap = 8) => FalconSettings.Unassigned with { ForceCap = cap, Overall = 1.0 };

        [Fact]
        public void AFalconNoSlotDrives_PutsNoForceOnTheHandle()
        {
            var c = new FalconController(1);
            var game = new FalconGameForce(1, 1, true, FfbEffectTypes.Const, 1.0, 0, 0, null, 1.0);
            for (int i = 0; i < 600; i++)
            {
                var o = c.Step(At(0.9, 0), Reach, FalconSettings.Unassigned, game, false, 0.001, i);
                Assert.Equal(0.0, o.Fx);
                Assert.Equal(0.0, o.Fy);
                Assert.Equal(0.0, o.Fz);
            }
        }

        [Fact]
        public void Forces_FadeInOverHalfASecond_AndAgainWhenTheFeelChanges()
        {
            var c = new FalconController(1);
            var o = c.Step(At(0.425, 0), Reach, Joystick(), FalconGameForce.Silent, true, 0.001, 0);
            Assert.Equal(-1.5 * 0.002, o.Fy, 9);
            for (int i = 1; i < 600; i++)
                o = c.Step(At(0.425, 0), Reach, Joystick(), FalconGameForce.Silent, true, 0.001, i);
            Assert.Equal(1.0, c.Fade, 12);
            Assert.Equal(-1.5, o.Fy, 9);
            c.Step(At(0.425, 0), Reach, Joystick() with { Feel = FalconFeel.Aim }, FalconGameForce.Silent, true, 0.001, 600);
            Assert.Equal(0.002, c.Fade, 9);
        }

        [Fact]
        public void TheForceLimit_CapsTheWholeForce()
        {
            var c = new FalconController(1);
            var game = new FalconGameForce(0, 0, true, FfbEffectTypes.Const, 1.0, Math.PI / 2, 0, null, 1.0);
            FalconOutput o = default;
            for (int i = 0; i < 600; i++)
                o = c.Step(At(0.95, 0), Reach, Joystick(cap: 2), game, true, 0.001, i);
            Assert.True(Math.Sqrt(o.Fx * o.Fx + o.Fy * o.Fy + o.Fz * o.Fz) <= 2.0 + Tol);
            // At the 8 N limit the plane still stops at 7.8 N.
            c = new FalconController(1);
            for (int i = 0; i < 600; i++)
                o = c.Step(At(0.95, 0), Reach, Joystick(), game, true, 0.001, i);
            Assert.Equal(FalconAimFeel.PlaneCapN, Math.Sqrt(o.Fy * o.Fy + o.Fz * o.Fz), 9);
        }

        [Fact]
        public void APreload_PushesInOut_OnlyInTheAimFeel()
        {
            var aim = FalconSettings.Unassigned with { Feel = FalconFeel.Aim, PreloadButton = 2, Overall = 1.0 };
            var c = new FalconController(1);
            FalconOutput o = default;
            for (int i = 0; i < 600; i++)
                o = c.Step(At(0, 0, buttons: 0b10), Reach, aim, FalconGameForce.Silent, true, 0.001, i);
            Assert.Equal(FalconController.PreloadN, o.Fx, 9);
            c = new FalconController(1);
            for (int i = 0; i < 600; i++)
                o = c.Step(At(0, 0, buttons: 0b10), Reach, aim with { Feel = FalconFeel.Joystick }, FalconGameForce.Silent, true, 0.001, i);
            Assert.Equal(0.0, o.Fx, 12);
        }

        // ── The settings ──

        [Fact]
        public void TheSettings_ParseAndClamp()
        {
            var defaults = FalconSettings.From(new PadSetting());
            Assert.Equal(FalconFeel.Joystick, defaults.Feel);
            Assert.Equal(1.0, defaults.Spring);
            Assert.Equal(1, defaults.KickButton);
            Assert.Equal(1.0, defaults.Kick);
            Assert.Equal(1.0, defaults.Shake);
            Assert.Equal(0, defaults.BraceButton);
            Assert.Equal(0, defaults.PreloadButton);
            Assert.Equal(1.0, defaults.Gravity);
            Assert.Equal(8.0, defaults.ForceCap);

            var wild = FalconSettings.From(new PadSetting
            {
                FalconFeel = "1", FalconSpring = "900", FalconKickButton = "7", FalconKick = "-5",
                FalconShake = "abc", FalconBraceButton = "4", FalconPreloadButton = "-1",
                FalconGravity = "150", FalconForceCap = "20", ForceOverall = "40",
            });
            Assert.Equal(FalconFeel.Aim, wild.Feel);
            Assert.Equal(2.0, wild.Spring);
            Assert.Equal(4, wild.KickButton);
            Assert.Equal(0.0, wild.Kick);
            Assert.Equal(1.0, wild.Shake);
            Assert.Equal(4, wild.BraceButton);
            Assert.Equal(0, wild.PreloadButton);
            Assert.Equal(1.5, wild.Gravity);
            Assert.Equal(8.0, wild.ForceCap);
            Assert.Equal(0.4, wild.Overall, 12);
        }

        [Fact]
        public void TheFalconFields_RideTheChecksumAndTheCopy()
        {
            var ps = new PadSetting();
            string before = ps.ComputeChecksum();
            ps.FalconPreloadButton = "3";
            Assert.NotEqual(before, ps.ComputeChecksum());
            ps.FalconFeel = "1";
            ps.FalconForceCap = "5";
            var copy = ps.CloneDeep();
            Assert.Equal("1", copy.FalconFeel);
            Assert.Equal("3", copy.FalconPreloadButton);
            Assert.Equal("5", copy.FalconForceCap);
        }

        [Fact]
        public void TheSwitch_IsOffByDefault_AndFeedsTheEngine()
        {
            var vm = new SettingsViewModel();
            Assert.False(vm.NovintFalconEnabled);
            vm.NovintFalconEnabled = true;
            try
            {
                Assert.True(FalconRuntime.Enabled);
                vm.ResetSettingCommand.Execute(nameof(SettingsViewModel.NovintFalconEnabled));
                Assert.False(vm.NovintFalconEnabled);
                Assert.False(FalconRuntime.Enabled);
            }
            finally
            {
                vm.NovintFalconEnabled = false;
            }
            string settings = File.ReadAllText(RepoFile("PadForge.App", "Services", "SettingsService.cs"));
            Assert.Contains("vm.NovintFalconEnabled = appSettings.NovintFalconEnabled;", settings);
            Assert.Contains("NovintFalconEnabled = vm.NovintFalconEnabled,", settings);
            Assert.Contains("FalconLibraryPath = string.IsNullOrEmpty(vm.FalconLibraryPath) ? null : vm.FalconLibraryPath,", settings);
            Assert.Contains("vm.FalconLibraryPath = appSettings.FalconLibraryPath;", settings);
        }

        [Fact]
        public void TheSwitchAndThePickedLibrary_RoundTripThroughTheSettingsXml()
        {
            var data = new AppSettingsData { NovintFalconEnabled = true, FalconLibraryPath = "C:\\SDK\\bin\\dhd64.dll" };
            var serializer = new XmlSerializer(typeof(AppSettingsData));
            using var writer = new StringWriter();
            serializer.Serialize(writer, data);
            var back = (AppSettingsData)serializer.Deserialize(new StringReader(writer.ToString()));
            Assert.True(back.NovintFalconEnabled);
            Assert.Equal("C:\\SDK\\bin\\dhd64.dll", back.FalconLibraryPath);
        }

        [Fact]
        public void ResetAll_PutsEveryFalconSettingBack()
        {
            var vm = new PadViewModel(0)
            {
                FalconFeel = 1, FalconSpring = 50, FalconKickButton = 3, FalconKick = 10, FalconShake = 20,
                FalconBraceButton = 2, FalconPreloadButton = 4, FalconGravity = 0, FalconForceCap = 2,
            };
            Assert.True(vm.FalconIsAim);
            Assert.False(vm.FalconIsJoystick);
            vm.ResetFalconAllCommand.Execute(null);
            Assert.Equal(0, vm.FalconFeel);
            Assert.True(vm.FalconIsJoystick);
            Assert.Equal(100, vm.FalconSpring);
            Assert.Equal(1, vm.FalconKickButton);
            Assert.Equal(100, vm.FalconKick);
            Assert.Equal(100, vm.FalconShake);
            Assert.Equal(0, vm.FalconBraceButton);
            Assert.Equal(0, vm.FalconPreloadButton);
            Assert.Equal(100, vm.FalconGravity);
            Assert.Equal(8, vm.FalconForceCap);
        }

        // ── The reach ──

        [Fact]
        public void ASweep_KeepsOnlyReachesInsideTheAcceptedSpan()
        {
            var old = new FalconReach(0.05, 0.05, 0.05);
            // 100 mm of travel: 95% of half is 47.5 mm. 20 mm is too small,
            // 300 mm too large, and both keep the old reach.
            var result = FalconDevice.Measure(old, 0.100, 0.020, 0.300);
            Assert.True(result.MeasuredX);
            Assert.False(result.MeasuredY);
            Assert.False(result.MeasuredZ);
            Assert.Equal(0.0475, result.Reach.X, 12);
            Assert.Equal(0.05, result.Reach.Y, 12);
            Assert.Equal(0.05, result.Reach.Z, 12);
        }

        [Fact]
        public void TheRecordsReach_RoundTrips_AndFallsBackPerAxis()
        {
            var reach = new FalconReach(0.0475, 0.04, 0.1);
            string stored = FalconDevice.FormatReach(reach);
            Assert.Equal("47.5,40.0,100.0", stored);
            Assert.Equal(reach, FalconDevice.ParseReach(stored));
            Assert.Equal(FalconReach.Default, FalconDevice.ParseReach(""));
            Assert.Equal(FalconReach.Default, FalconDevice.ParseReach("1,2"));
            var mixed = FalconDevice.ParseReach("47.5,999,x");
            Assert.Equal(0.0475, mixed.X, 12);
            Assert.Equal(FalconReach.DefaultMeters, mixed.Y);
            Assert.Equal(FalconReach.DefaultMeters, mixed.Z);
        }

        // ── The row and its default mapping ──

        [Fact]
        public void TheHandle_ReadsLikeAGamepadStick_UpAtTheLowEnd()
        {
            Assert.Equal(32768, FalconDevice.ToAxis(0));
            Assert.Equal(65535, FalconDevice.ToAxis(1));
            Assert.Equal(1, FalconDevice.ToAxis(-1));

            var link = new ScriptedFalcon { Z = Reach.Z };
            var falcon = new FalconDevice(link, 0, 0, 1234);
            try
            {
                Assert.True(SpinWait.SpinUntil(() => falcon.Reading.StickY > 0.99, 3000), "no reading");
                var state = falcon.GetCurrentState();
                Assert.Equal(32768, state.Axis[FalconDevice.AxisLeftRight]);
                // Up reads low, as an SDL gamepad's Y does (SdlDeviceWrapper,
                // CustomInputState), so Axis 1 on the right stick lifts it.
                Assert.True(state.Axis[FalconDevice.AxisUpDown] <= 1);
                Assert.Equal(InputDeviceType.NovintFalcon, falcon.GetInputDeviceType());
                Assert.Equal("falcon://1234", falcon.DevicePath);
                Assert.Equal(7, falcon.GetDeviceObjects().Length);
            }
            finally
            {
                falcon.Dispose();
            }
        }

        [Fact]
        public void TheDefaultMapping_IsRumbleRoutes_OnXboxAndPlayStation()
        {
            var ud = new UserDevice();
            ud.LoadCapabilities(3, 4, 0, InputDeviceType.NovintFalcon);
            foreach (var type in new[] { VirtualControllerType.Xbox, VirtualControllerType.PlayStation })
            {
                var ps = SettingsManager.CreateDefaultPadSetting(ud, type);
                Assert.Equal("Axis 0", ps.RightThumbAxisX);
                Assert.Equal("Axis 1", ps.RightThumbAxisY);
                Assert.Equal("Button 0", ps.RightTrigger);
                Assert.Equal("Button 1", ps.LeftTrigger);
                Assert.Equal("Button 2", ps.RightShoulder);
                Assert.Equal("Button 3", ps.RightThumbButton);
                Assert.True(string.IsNullOrEmpty(ps.LeftThumbAxisX));
                Assert.True(string.IsNullOrEmpty(ps.LeftThumbAxisY));
            }
            ud.ForceRawJoystickMode = true;
            Assert.True(string.IsNullOrEmpty(SettingsManager.CreateDefaultPadSetting(ud, VirtualControllerType.Xbox).RightThumbAxisX));
        }

        [Fact]
        public void TheDevicesPage_NamesTheHandlesAxes()
        {
            var vm = new DevicesViewModel();
            vm.RebuildRawStateCollections(new[] { 0, 1, 2 }, new[] { 0, 1, 2, 3 }, 0, isFalcon: true);
            Assert.True(vm.IsFalconDevice);
            Assert.Equal(new[] { DevicesViewModel.FalconAxisName(0), DevicesViewModel.FalconAxisName(1), DevicesViewModel.FalconAxisName(2) },
                vm.RawAxes.Select(a => a.Name));
            Assert.Equal(0.5, vm.FalconDotX);
            vm.ClearRawState();
            Assert.False(vm.IsFalconDevice);
        }

        // ── The device closes on every way out ──

        [Fact]
        public void Dispose_SetsZeroForce_AndClosesTheDevice()
        {
            var link = new ScriptedFalcon();
            var falcon = new FalconDevice(link, 0, 0, null);
            Assert.True(SpinWait.SpinUntil(() => link.Reads > 5, 3000));
            falcon.Dispose();
            Assert.Equal(1, link.Closes);
            Assert.Equal((0.0, 0.0, 0.0), link.LastForce);
            Assert.False(falcon.IsAttached);
            Assert.Equal("falcon://index/0", falcon.DevicePath);
        }

        [Fact]
        public void TheCrashQuiesce_StopsTheFalconWithinItsWait()
        {
            var link = new ScriptedFalcon();
            var falcon = new FalconDevice(link, 0, 0, 7);
            try
            {
                Assert.True(SpinWait.SpinUntil(() => link.Reads > 5, 3000));
                falcon.Quiesce(Environment.TickCount64 + 1000);
                Assert.True(SpinWait.SpinUntil(() => link.Closes == 1, 1000));
                Assert.Equal((0.0, 0.0, 0.0), link.LastForce);
            }
            finally
            {
                falcon.Dispose();
            }
            Assert.Equal(1, link.Closes);
        }

        [Fact]
        public void AFalconThatStopsAnswering_DetachesAndCloses()
        {
            var link = new ScriptedFalcon();
            var falcon = new FalconDevice(link, 0, 0, 9);
            try
            {
                Assert.True(SpinWait.SpinUntil(() => link.Reads > 5, 3000));
                link.Fail = true;
                Assert.True(SpinWait.SpinUntil(() => !falcon.IsAttached, 3000));
                Assert.True(SpinWait.SpinUntil(() => link.Closes == 1, 1000));
                Assert.Null(falcon.GetCurrentState());
            }
            finally
            {
                falcon.Dispose();
            }
        }

        /// <summary>Reads that keep working between failed force writes
        /// never reset the writes' count: a Falcon that takes no force
        /// detaches, closes and logs the library's reason.</summary>
        [Fact]
        public void AFalconWhoseForceWritesFail_DetachesLogsWhyAndCloses()
        {
            var link = new ScriptedFalcon { ErrorText = "scripted force write failure (D1)" };
            var falcon = new FalconDevice(link, 5, 0, 21);
            try
            {
                Assert.True(SpinWait.SpinUntil(() => link.Reads > 5, 3000));
                link.ForceFails = true;
                Assert.True(SpinWait.SpinUntil(() => !falcon.IsAttached, 1000), "a Falcon that takes no force kept its row");
                Assert.True(SpinWait.SpinUntil(() => link.Closes == 1, 1000));
                Assert.Contains("Falcon: device 5 failed 3 force writes in a row, so it closes: scripted force write failure (D1)",
                    SdlDiagLog.Snapshot());
            }
            finally
            {
                falcon.Dispose();
            }
            Assert.Equal(1, link.Closes);
        }

        /// <summary>A Falcon whose forces do not turn on closes at once and
        /// logs why, as Force Dimension's examples stop when
        /// dhdEnableForce fails, so the next sweep opens it again.</summary>
        [Theory]
        [InlineData(true, false, "could not turn on gravity compensation")]
        [InlineData(false, true, "could not turn on its forces")]
        public void AFalconWhoseForcesDoNotTurnOn_ClosesAndLogsWhy(bool gravityFails, bool enableFails, string why)
        {
            string text = "scripted start failure (D2, " + why + ")";
            var link = new ScriptedFalcon { GravityFails = gravityFails, EnableFails = enableFails, ErrorText = text };
            var falcon = new FalconDevice(link, 6, 0, 22);
            try
            {
                Assert.True(SpinWait.SpinUntil(() => link.Closes == 1, 1000), "a Falcon with no forces stayed open");
                Assert.False(falcon.IsAttached);
                Assert.Equal(0, link.Reads);
                Assert.Contains("Falcon: device 6 " + why + ", so it closes: " + text, SdlDiagLog.Snapshot());
            }
            finally
            {
                falcon.Dispose();
            }
            Assert.Equal(1, link.Closes);
        }

        /// <summary>A sweep asked for after the haptic thread ended still
        /// answers, once, as a Falcon that closed mid-sweep does, so the
        /// row never keeps "Move the handle..." for good.</summary>
        [Fact]
        public void ACalibrationAskedAfterTheFalconClosed_AnswersAtOnce()
        {
            var link = new ScriptedFalcon { Fail = true };
            var falcon = new FalconDevice(link, 0, 0, 23);
            try
            {
                Assert.True(SpinWait.SpinUntil(() => link.Closes == 1, 3000));
                // The thread's last step, after the close, takes back any
                // sweep stored before it.
                Thread.Sleep(100);
                int calls = 0;
                FalconCalibration result = default;
                Assert.True(falcon.BeginCalibration(r => { result = r; Interlocked.Increment(ref calls); }));
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref calls) > 0, 1000), "the sweep never answered");
                Thread.Sleep(50);
                Assert.Equal(1, calls);
                Assert.False(result.MeasuredX || result.MeasuredY || result.MeasuredZ);
                Assert.Equal(FalconReach.Default, result.Reach);
                Assert.False(falcon.IsCalibrating);
            }
            finally
            {
                falcon.Dispose();
            }
            Assert.False(falcon.BeginCalibration(_ => { }));
        }

        [Fact]
        public void TheHandOff_IsKeptWhileNothingChanged_AndSilenceKeepsTheFeel()
        {
            var link = new ScriptedFalcon();
            var falcon = new FalconDevice(link, 0, 0, 3);
            try
            {
                var ps = new PadSetting { FalconFeel = "1" };
                var v = new Vibration();
                falcon.Publish(ps, v, 1000, 2000);
                var first = falcon.Handoff;
                Assert.True(first.Assigned);
                Assert.Equal(FalconFeel.Aim, first.Settings.Feel);
                falcon.Publish(ps, v, 1000, 2000);
                Assert.Same(first, falcon.Handoff);
                falcon.Publish(ps, v, 1000, 2500);
                Assert.NotSame(first, falcon.Handoff);
                Assert.Same(first.Settings, falcon.Handoff.Settings);
                falcon.SilenceGame();
                Assert.Same(FalconGameForce.Silent, falcon.Handoff.Game);
                Assert.Equal(FalconFeel.Aim, falcon.Handoff.Settings.Feel);
                Assert.True(falcon.Handoff.Assigned);
            }
            finally
            {
                falcon.Dispose();
            }
        }

        // ── Force Dimension's library and driver ──

        [Fact]
        public void TheLibrary_IsRefusedInTheTestHost()
        {
            Assert.True(VendorProgramGate.Refuses);
            Assert.Null(ForceDimensionLibrary.TryLoad(@"C:\nowhere\dhd64.dll", out string reason));
            Assert.Equal("gate", reason);
        }

        [Fact]
        public void APickedLibrary_IsTriedFirst_AndTheSdkRootIsTheParentOfBin()
        {
            string dir = Path.Combine(Path.GetTempPath(), "PadForge_FalconTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string picked = Path.Combine(dir, ForceDimensionCatalog.LibraryName);
                File.WriteAllBytes(picked, new byte[] { 0x4D, 0x5A });
                var list = ForceDimensionCatalog.Candidates(picked);
                Assert.Equal(Path.GetFullPath(picked), list[0]);
                Assert.Equal(list.Count, list.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
            Assert.Equal(@"C:\FD\sdk-3.17.7", ForceDimensionCatalog.SdkRootOf(@"C:\FD\sdk-3.17.7\bin\dhd64.dll"));
            Assert.Equal(@"C:\FD\sdk-3.17.7", ForceDimensionCatalog.SdkRootOf(@"C:\FD\sdk-3.17.7\lib\dhd64.dll"));
            Assert.Null(ForceDimensionCatalog.SdkRootOf(@"C:\Windows\System32\dhd64.dll"));
            Assert.Null(ForceDimensionCatalog.SdkRootOf(""));
        }

        [Fact]
        public void TheDriverPackage_IsOfferedOnlyWithTheCatalogItNames()
        {
            Assert.Equal("fdhusb.cat", DriverInstaller.InfCatalogName(new[]
            {
                "[version]", "Signature = \"$Windows NT$\"", "Class = FDUSBDevices", "CatalogFile=fdhusb.cat ; signed",
            }));
            Assert.Null(DriverInstaller.InfCatalogName(new[] { "[Version]", "DriverVer = 12/21/2017,3.7.0.3073" }));
            Assert.Null(DriverInstaller.InfCatalogName(new[] { "[Strings]", "CatalogFile=elsewhere.cat" }));

            string root = Path.Combine(Path.GetTempPath(), "PadForge_FalconTests", Guid.NewGuid().ToString("N"));
            string usb = Path.Combine(root, "drivers", "usb");
            Directory.CreateDirectory(usb);
            try
            {
                string inf = Path.Combine(usb, "fdhusb.inf");
                File.WriteAllText(inf, "[Version]\r\nCatalogFile=fdhusb.cat\r\n");
                Assert.Null(DriverInstaller.FalconDriverPackage(root));
                File.WriteAllBytes(Path.Combine(usb, "fdhusb.cat"), new byte[] { 0x30 });
                Assert.Equal(inf, DriverInstaller.FalconDriverPackage(root));
                Assert.Null(DriverInstaller.FalconDriverPackage(null));
                // The install never runs in the test host.
                Assert.Throws<UnauthorizedAccessException>(() => DriverInstaller.InstallFalconDriver(inf));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        // ── Picking another library while one is loaded ──

        private const string PathA = @"C:\FD\sdk-a\bin\dhd64.dll", PathB = @"C:\FD\sdk-b\bin\dhd64.dll";

        private static object Call(InputManager im, string method)
            => typeof(InputManager).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(im, null);

        /// <summary>An engine whose poll step starts no sweep: the test runs
        /// each one itself.</summary>
        private static InputManager SweepHost()
        {
            var im = new InputManager();
            typeof(InputManager).GetField("_falconNextSweepTicks", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(im, long.MaxValue);
            return im;
        }

        /// <summary>The installed copy is A, and B loads only when picked.
        /// Any other pick fails to load.</summary>
        private static Func<string, FalconLibrary> Loader(ScriptedFalcon a, ScriptedFalcon b)
            => picked => picked == null ? new FalconLibrary(a, PathA, "3.17.7")
                : string.Equals(picked, PathB, StringComparison.OrdinalIgnoreCase) ? new FalconLibrary(b, PathB, "3.17.7")
                : null;

        private static void EndSweeps(InputManager im)
        {
            im.ShutdownFalconInputs();
            FalconRuntime.Enabled = false;
            FalconRuntime.PickedLibrary = null;
            FalconRuntime.UseLoaderForTest(null);
        }

        [Fact]
        public void ALibraryPickedAfterOneLoaded_TakesOver_AndTheFalconReopensOnIt()
        {
            var a = new ScriptedFalcon();
            var b = new ScriptedFalcon();
            FalconRuntime.PickedLibrary = null;
            FalconRuntime.UseLoaderForTest(Loader(a, b));
            var im = SweepHost();
            try
            {
                FalconRuntime.Enabled = true;
                Call(im, "FalconSweep");
                Assert.Equal(PathA, FalconRuntime.Status.LibraryPath);
                Assert.True(SpinWait.SpinUntil(() => a.Reads > 5, 3000), "no Falcon opened on the installed copy");

                FalconRuntime.PickedLibrary = PathB;
                // The first sweep after the pick closes the Falcon on the
                // old copy, and the poll step lets it go.
                Call(im, "FalconSweep");
                Assert.Equal(1, a.Closes);
                Call(im, "UpdateFalconDevices");
                // The next sweep opens it on the picked copy.
                Call(im, "FalconSweep");
                Assert.True(SpinWait.SpinUntil(() => b.Reads > 5, 3000), "the Falcon never opened on the picked copy");
                Assert.Equal(PathB, FalconRuntime.Status.LibraryPath);
                Assert.Same(b, FalconRuntime.Acquire(Environment.TickCount64, out _, out _, out _));
                Assert.Equal(1, a.Closes);
            }
            finally
            {
                EndSweeps(im);
            }
        }

        [Fact]
        public void APickedLibraryThatDoesNotLoad_LeavesTheCopyInUseAndItsFalcon_AndSaysSo()
        {
            var a = new ScriptedFalcon();
            var b = new ScriptedFalcon();
            FalconRuntime.PickedLibrary = null;
            FalconRuntime.UseLoaderForTest(Loader(a, b));
            var im = SweepHost();
            try
            {
                FalconRuntime.Enabled = true;
                Call(im, "FalconSweep");
                Assert.True(SpinWait.SpinUntil(() => a.Reads > 5, 3000), "no Falcon opened on the installed copy");
                Assert.False(FalconRuntime.Status.PickNotLoaded);

                FalconRuntime.PickedLibrary = @"C:\elsewhere\dhd64.dll";
                Call(im, "FalconSweep");
                Call(im, "FalconSweep");
                Assert.Equal(0, a.Closes);
                Assert.Equal(PathA, FalconRuntime.Status.LibraryPath);
                Assert.True(FalconRuntime.Status.PickNotLoaded);
                Assert.Equal(0, b.Reads);
            }
            finally
            {
                EndSweeps(im);
            }
        }

        /// <summary>The poll thread takes the same lock every pass, so the
        /// sweep never makes a library call while it holds it.</summary>
        [Fact]
        public void ASwitchTurnedOffMidSweep_ClosesTheFalconOutsideTheEnginesLock()
        {
            var im = SweepHost();
            object gate = typeof(InputManager).GetField("_falconLock", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(im);
            bool closedUnderLock = false;
            var a = new ScriptedFalcon
            {
                // Off between the open and the hand-off to the poll thread.
                OnSerial = () => FalconRuntime.Enabled = false,
                OnClose = () => closedUnderLock |= Monitor.IsEntered(gate),
            };
            FalconRuntime.PickedLibrary = null;
            FalconRuntime.UseLoaderForTest(Loader(a, a));
            try
            {
                FalconRuntime.Enabled = true;
                Call(im, "FalconSweep");
                Assert.Equal(1, a.Closes);
                Assert.False(closedUnderLock, "the sweep closed the Falcon inside _falconLock");
                Assert.Equal(0, a.Reads);
            }
            finally
            {
                EndSweeps(im);
            }
        }

        // ── Why no Falcon opened ──

        /// <summary>Force Dimension's own package puts the Falcon on WinUSB
        /// with its interface GUID (fdhusb-3.18.00.03.inf, Falcon_Install.HW,
        /// and fdhusb-3.7.0.3073.inf, Falcon_AddReg). A node with it is on
        /// the driver the library needs.</summary>
        [Fact]
        public void ANodeWithForceDimensionsInterface_IsOnItsOwnPackage()
        {
            Assert.True(DriverInstaller.NamesFalconInterface(new[] { "{CB5DAA55-DC1A-4f2d-8F76-14CEE032832A}" }));
            Assert.True(DriverInstaller.NamesFalconInterface(new[] { "{11111111-2222-3333-4444-555555555555}", "{cb5daa55-dc1a-4f2d-8f76-14cee032832a}" }));
            Assert.False(DriverInstaller.NamesFalconInterface(new[] { "{FD826C66-3556-4845-9436-0FE7580DED02}" }));
            Assert.False(DriverInstaller.NamesFalconInterface(Array.Empty<string>()));
            Assert.False(DriverInstaller.NamesFalconInterface(null));
            Assert.False(DriverInstaller.NamesFalconInterface(new[] { "not a guid" }));
        }

        [Fact]
        public void AFalconOnForceDimensionsDriverThatDidNotOpen_GetsTheOpensErrorNotTheOtherDriverLine()
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            var own = new FalconSweepStatus(string.Empty, PathA, "3.17.7", null, 0, Array.Empty<string>(), null,
                OwnDriverNodes: 1, OpenError: "USB timeout");
            string line = PadForge.Services.InputService.FalconNoRowLine(own, canInstall: false);
            Assert.Equal(string.Format(s.Falcon_Status_OwnDriverNotOpen_Format, "USB timeout"), line);
            Assert.DoesNotContain("WINUSB", line);
            // A second, driverless Falcon: the install stops on any Falcon
            // with a driver, so the button stays hidden.
            var withDriverless = own with { NodeDrivers = new[] { string.Empty }, DriverPackage = @"C:\FD\drivers\usb\fdhusb.inf" };
            Assert.True(PadForge.Services.InputService.FalconCanInstall(withDriverless with { OwnDriverNodes = 0 }, rows: 0));
            Assert.False(PadForge.Services.InputService.FalconCanInstall(withDriverless, rows: 0));

            // No error from the library: the SDK version's "?" stands in.
            Assert.Equal(string.Format(s.Falcon_Status_OwnDriverNotOpen_Format, "?"),
                PadForge.Services.InputService.FalconNoRowLine(own with { OpenError = null }, canInstall: false));

            // Another driver keeps its own line.
            var other = own with { NodeDrivers = new[] { "libusbK" }, OwnDriverNodes = 0 };
            Assert.Equal(string.Format(s.Falcon_Status_OtherDriver_Format, "libusbK"),
                PadForge.Services.InputService.FalconNoRowLine(other, canInstall: false));
        }

        [Fact]
        public void AFailedOpen_LeavesTheLibrarysReasonInTheStatus()
        {
            var a = new ScriptedFalcon { OpenFails = true, ErrorText = "communication error (D4)" };
            FalconRuntime.PickedLibrary = null;
            FalconRuntime.UseLoaderForTest(Loader(a, a));
            var im = SweepHost();
            try
            {
                FalconRuntime.Enabled = true;
                Call(im, "FalconSweep");
                Assert.Equal("communication error (D4)", FalconRuntime.Status.OpenError);
                Assert.Equal(0, FalconRuntime.Status.InUse);
            }
            finally
            {
                EndSweeps(im);
            }
        }

        /// <summary>The scripted loader opens the sweep's fence, so nothing
        /// in the app or the engine sets it.</summary>
        [Fact]
        public void TheScriptedLoader_IsSetByTestsAlone()
        {
            foreach (string project in new[] { "PadForge.App", "PadForge.Engine" })
                foreach (string file in Directory.EnumerateFiles(RepoFile(project), "*.cs", SearchOption.AllDirectories))
                {
                    if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
                    string text = File.ReadAllText(file);
                    int calls = text.Split("UseLoaderForTest(").Length - 1;
                    int declared = Path.GetFileName(file) == "FalconRuntime.cs" ? 1 : 0;
                    Assert.True(calls == declared, file + " calls UseLoaderForTest");
                }
        }

        /// <summary>The Falcon install's own lines, a refused forced bind and
        /// a restart Windows asked for among them, reach the diagnostics
        /// ring, which a Release build keeps. Debug.WriteLine reaches nothing
        /// there.</summary>
        [Fact]
        public void TheFalconDriverInstall_LogsToTheDiagnosticsRing()
        {
            string source = File.ReadAllText(RepoFile("PadForge.App", "Common", "DriverInstaller.cs")).Replace("\r\n", "\n");
            int start = source.IndexOf("public static void InstallFalconDriver(string inf)", StringComparison.Ordinal);
            int end = source.IndexOf("\n        }\n", start, StringComparison.Ordinal);
            Assert.True(start > 0 && end > start);
            string install = source.Substring(start, end - start);
            Assert.DoesNotContain("Debug.WriteLine", install);
            Assert.Contains("InstallSignedPackage(inf, FalconHardwareId,\n                    m => PadForge.Engine.SdlDiagLog.WriteLine(\"Falcon driver: \" + m))", install);
        }

        // ── A Falcon shared over Remote Link ──

        private static void PollFeedback(InputManager manager, UserDevice device)
            => typeof(InputManager).GetMethod("ApplyForceFeedback", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(manager, new object[] { device });

        private static readonly MethodInfo ApplyRemote =
            typeof(InputService).GetMethod("ApplyRemoteOutput", BindingFlags.NonPublic | BindingFlags.Instance);

        private static PadSetting PeerSlot() => new()
        {
            FalconFeel = "1", FalconSpring = "150", FalconKickButton = "2", FalconKick = "80", FalconShake = "60",
            FalconBraceButton = "3", FalconPreloadButton = "4", FalconGravity = "50", FalconForceCap = "5.5",
            ForceOverall = "70",
        };

        [Fact]
        public void TheFalconTail_RoundTrips_AndAFrameWithoutItReadsAsBefore()
        {
            var v = new Vibration(30000, 1000) { LeftTriggerMotorSpeed = 5, RightTriggerMotorSpeed = 6 };
            var ps = PeerSlot();
            var tail = FalconSettings.ToWire(ps);
            byte[] withTail = OutputEffectCodec.EncodeVibration(v, tail);
            Assert.True(OutputEffectCodec.TryDecode(withTail, out var with));
            Assert.Equal(tail, with.Falcon);
            Assert.Equal(FalconSettings.From(ps), FalconSettings.FromWire(with.Falcon.Value));
            Assert.Equal((ushort)30000, with.Vibration.LeftMotorSpeed);
            Assert.Equal((ushort)6, with.Vibration.RightTriggerMotorSpeed);
            Assert.False(FalconSettings.ToWire(null).Equals(OutputEffectCodec.FalconTail.Unassigned));

            // The tail follows the frame an owner from before it reads, byte
            // for byte, and a frame without it carries no settings.
            byte[] plain = OutputEffectCodec.EncodeVibration(v);
            Assert.Equal(plain, withTail.AsSpan(0, plain.Length).ToArray());
            Assert.Equal(OutputEffectCodec.FalconTail.Length, withTail.Length - plain.Length);
            Assert.True(OutputEffectCodec.TryDecode(plain, out var without));
            Assert.Null(without.Falcon);
            Assert.True(OutputEffectCodec.TryDecode(OutputEffectCodec.EncodeVibration(v, OutputEffectCodec.FalconTail.Unassigned), out var letGo));
            Assert.False(letGo.Falcon.Value.Assigned);
        }

        [Fact]
        public void TheOwner_AnnouncesForceForASharedFalcon_AndTheFalconItselfStillReportsNone()
        {
            var savedDevices = SettingsManager.UserDevices;
            var falcon = new FalconDevice(new ScriptedFalcon(), 0, 0, 78);
            try
            {
                SettingsManager.UserDevices = new DeviceCollection();
                var owner = new UserDevice();
                owner.LoadFromExternalDevice(falcon);
                owner.IsOnline = true;
                SettingsManager.UserDevices.Items.Add(owner);
                var service = new InputService(new MainViewModel());
                var inventory = (System.Collections.Generic.IEnumerable<PadForge.Engine.RemoteLink.RemotePeerDeviceInfo>)typeof(InputService)
                    .GetMethod("BuildExposedDevices", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, null);
                var info = Assert.Single(inventory);
                Assert.Equal(InputDeviceType.NovintFalcon, info.InputDeviceType);
                Assert.True(info.HasRumble, "a peer's slot sends a shared Falcon no force");
                // The local gates read the device's own flags, which stay off.
                Assert.False(falcon.HasRumble);
                Assert.False(falcon.HasHaptic);
            }
            finally
            {
                SettingsManager.UserDevices = savedDevices;
                falcon.Dispose();
            }
        }

        [Fact]
        public void APeersFalconSlot_ReachesTheOwnersHandle_AndLettingItGoUnassignsIt()
        {
            var savedSettings = SettingsManager.UserSettings;
            var savedSend = RemoteLinkOutputRouter.SendOutput;
            var savedMembership = RemoteLinkOutputRouter.IsPeerConnected;
            var falcon = new FalconDevice(new ScriptedFalcon(), 0, 0, 79);
            var owner = new UserDevice();
            owner.LoadFromExternalDevice(falcon);
            owner.IsOnline = true;
            var peer = new RemotePeerDevice(new RemotePeerDeviceInfo
            {
                PeerFingerprintHex = "owner", PeerLocalDeviceId = falcon.InstanceGuid.ToString("N"), HasRumble = true,
                VendorId = falcon.VendorId, ProductId = falcon.ProductId, NumAxes = 3, NumButtons = 4,
                InputDeviceType = InputDeviceType.NovintFalcon,
            });
            var remote = new UserDevice();
            remote.LoadFromExternalDevice(peer);
            var frames = new System.Collections.Generic.List<OutputEffectCodec.OutputEffect>();
            var service = new InputService(new MainViewModel());
            RemoteLinkOutputRouter.Register(peer.DevicePath, "owner", 0);
            RemoteLinkOutputRouter.IsPeerConnected = _ => true;
            RemoteLinkOutputRouter.SendOutput = (_, _, bytes) =>
            {
                Assert.True(OutputEffectCodec.TryDecode(bytes, out var effect));
                frames.Add(effect);
                ApplyRemote.Invoke(service, new object[] { effect, falcon, owner, "consumer", null });
            };
            try
            {
                SettingsManager.UserSettings = new SettingsCollection();
                var setting = new UserSetting { InstanceGuid = remote.InstanceGuid, MapTo = 0 };
                setting.SetPadSetting(new PadSetting { ForceOverall = "50", FalconFeel = "1", FalconForceCap = "5" });
                SettingsManager.UserSettings.Items.Add(setting);
                var manager = new InputManager();
                manager.VibrationStates[0].LeftMotorSpeed = 40000;
                PollFeedback(manager, remote);
                var frame = Assert.Single(frames);
                Assert.True(frame.Falcon is { Assigned: true }, "the frame carries no Falcon settings");
                var handoff = falcon.Handoff;
                Assert.True(handoff.Assigned, "the peer's force never reached the Falcon");
                Assert.Equal(FalconFeel.Aim, handoff.Settings.Feel);
                Assert.Equal(5.0, handoff.Settings.ForceCap);
                Assert.Equal(0.5, handoff.Settings.Overall);
                // The motors carry the peer's Overall, which the force model
                // applies once, as on the Falcon's own PC.
                Assert.True(frame.Vibration.LeftMotorSpeed > 0);
                Assert.Equal(frame.Vibration.LeftMotorSpeed / 65535.0 / 0.5, handoff.Game.Heavy, 9);

                // The owner's own pass, with no slot of its own, leaves the
                // peer's hand-off standing while the peer drives the Falcon.
                PollFeedback(manager, owner);
                Assert.Same(handoff, falcon.Handoff);

                // The peer's last slot lets the Falcon go.
                SettingsManager.UserSettings.Items.Clear();
                PollFeedback(manager, remote);
                Assert.Equal(2, frames.Count);
                Assert.True(frames[1].Falcon is { Assigned: false });
                Assert.False(falcon.Handoff.Assigned);
                PollFeedback(manager, remote);
                Assert.Equal(2, frames.Count);
            }
            finally
            {
                SettingsManager.UserSettings = savedSettings;
                RemoteLinkOutputRouter.SendOutput = savedSend;
                RemoteLinkOutputRouter.IsPeerConnected = savedMembership;
                RemoteLinkOutputRouter.Unregister(peer.DevicePath);
                RemoteLinkOutputRouter.ReleaseDevice(owner.DevicePath);
                falcon.Dispose();
            }
        }

        [Fact]
        public void AFrameWithoutFalconSettings_LeavesTheFalconAsThisPCsSlotsLeaveIt()
        {
            var savedMembership = RemoteLinkOutputRouter.IsPeerConnected;
            var falcon = new FalconDevice(new ScriptedFalcon(), 0, 0, 80);
            var owner = new UserDevice();
            owner.LoadFromExternalDevice(falcon);
            owner.IsOnline = true;
            var service = new InputService(new MainViewModel());
            RemoteLinkOutputRouter.IsPeerConnected = _ => true;
            try
            {
                // A peer from before the settings rode the frame.
                Assert.True(OutputEffectCodec.TryDecode(OutputEffectCodec.EncodeVibration(new Vibration(40000, 0)), out var old));
                ApplyRemote.Invoke(service, new object[] { old, falcon, owner, "consumer", null });
                Assert.Same(FalconHandoff.Unassigned, falcon.Handoff);
                Assert.False(RemoteLinkOutputRouter.IsClaimedByPeer(owner.DevicePath));
                Assert.False(RemoteLinkOutputRouter.PeerWroteLast(owner.DevicePath));

                // Positive control: the same frame with the settings claims
                // the Falcon and reaches it.
                Assert.True(OutputEffectCodec.TryDecode(OutputEffectCodec.EncodeVibration(new Vibration(40000, 0),
                    FalconSettings.ToWire(PeerSlot())), out var current));
                ApplyRemote.Invoke(service, new object[] { current, falcon, owner, "consumer", null });
                Assert.True(falcon.Handoff.Assigned);
                Assert.True(RemoteLinkOutputRouter.IsClaimedByPeer(owner.DevicePath));
            }
            finally
            {
                RemoteLinkOutputRouter.IsPeerConnected = savedMembership;
                RemoteLinkOutputRouter.ReleaseDevice(owner.DevicePath);
                falcon.Dispose();
            }
        }

        /// <summary>A feel changed on the peer's pad page ships while the
        /// game's force holds still, and an unchanged frame does not.</summary>
        [Fact]
        public void AChangedFalconSetting_ShipsTheFrameAgain()
        {
            string path = "peer://owner/falcon-" + Guid.NewGuid().ToString("N");
            var saved = RemoteLinkOutputRouter.SendOutput;
            var tails = new System.Collections.Generic.List<OutputEffectCodec.FalconTail?>();
            RemoteLinkOutputRouter.SendOutput = (_, _, bytes) =>
            {
                Assert.True(OutputEffectCodec.TryDecode(bytes, out var effect));
                tails.Add(effect.Falcon);
            };
            RemoteLinkOutputRouter.Register(path, "owner", 0);
            try
            {
                var joystick = FalconSettings.ToWire(new PadSetting());
                var aim = FalconSettings.ToWire(new PadSetting { FalconFeel = "1" });
                Assert.True(RemoteLinkOutputRouter.ShipVibration(path, new Vibration(20000, 0), joystick));
                Assert.True(RemoteLinkOutputRouter.ShipVibration(path, new Vibration(20000, 0), joystick));
                Assert.Single(tails);
                Assert.True(RemoteLinkOutputRouter.ShipVibration(path, new Vibration(20000, 0), aim));
                Assert.Equal(new OutputEffectCodec.FalconTail?[] { joystick, aim }, tails);
            }
            finally
            {
                RemoteLinkOutputRouter.SendOutput = saved;
                RemoteLinkOutputRouter.Unregister(path);
            }
        }

        /// <summary>A Falcon both PCs assign: the peer's game holds it while
        /// its lease is fresh, through this PC's slot and through a pass
        /// whose slot gives no force, and this PC's slot takes it back once
        /// the peer lets go.</summary>
        [Fact]
        public void AFalconAPeerDrives_KeepsThePeersHandOff_OverThisPCsSlot()
        {
            var savedSettings = SettingsManager.UserSettings;
            var savedMembership = RemoteLinkOutputRouter.IsPeerConnected;
            var falcon = new FalconDevice(new ScriptedFalcon(), 0, 0, 81);
            var owner = new UserDevice();
            owner.LoadFromExternalDevice(falcon);
            owner.IsOnline = true;
            var service = new InputService(new MainViewModel());
            RemoteLinkOutputRouter.IsPeerConnected = _ => true;
            try
            {
                SettingsManager.UserSettings = new SettingsCollection();
                var local = new UserSetting { InstanceGuid = owner.InstanceGuid, MapTo = 0 };
                local.SetPadSetting(new PadSetting { ForceOverall = "100", FalconFeel = "0" });
                SettingsManager.UserSettings.Items.Add(local);
                var manager = new InputManager();
                manager.VibrationStates[0].LeftMotorSpeed = 20000;

                Assert.True(OutputEffectCodec.TryDecode(OutputEffectCodec.EncodeVibration(new Vibration(40000, 0),
                    FalconSettings.ToWire(PeerSlot())), out var relayed));
                ApplyRemote.Invoke(service, new object[] { relayed, falcon, owner, "consumer", null });
                var peers = falcon.Handoff;
                Assert.Equal(FalconFeel.Aim, peers.Settings.Feel);
                PollFeedback(manager, owner);
                Assert.Same(peers, falcon.Handoff);
                // A pass whose slot gives no force leaves the peer's force too.
                manager.TestRumbleTargetGuid[0] = Guid.NewGuid();
                PollFeedback(manager, owner);
                Assert.Same(peers, falcon.Handoff);
                manager.TestRumbleTargetGuid[0] = Guid.Empty;

                // Positive control: the peer lets go, and this PC's slot
                // hands over its own feel and force.
                RemoteLinkOutputRouter.ReleaseDevice(owner.DevicePath);
                PollFeedback(manager, owner);
                Assert.Equal(FalconFeel.Joystick, falcon.Handoff.Settings.Feel);
                Assert.True(falcon.Handoff.Game.Heavy > 0);
            }
            finally
            {
                SettingsManager.UserSettings = savedSettings;
                RemoteLinkOutputRouter.IsPeerConnected = savedMembership;
                RemoteLinkOutputRouter.ReleaseDevice(owner.DevicePath);
                falcon.Dispose();
            }
        }

        /// <summary>The owner's Falcon applies the peer slot's Overall to the
        /// game's effect, so the peer leaves it out of the effect it ships.
        /// Any other device gets it baked in, since its owner replays at
        /// 100.</summary>
        [Theory]
        [InlineData(InputDeviceType.NovintFalcon, 8000)]
        [InlineData(InputDeviceType.Gamepad, 4000)]
        public void APeersEffect_CarriesOverallOnlyForADeviceThatDoesNotApplyIt(int deviceType, int magnitude)
        {
            var savedSettings = SettingsManager.UserSettings;
            var savedSend = RemoteLinkOutputRouter.SendOutput;
            var peer = new RemotePeerDevice(new RemotePeerDeviceInfo
            {
                PeerFingerprintHex = "owner", PeerLocalDeviceId = Guid.NewGuid().ToString("N"), HasRumble = true,
                HasHaptic = true, VendorId = FalconDevice.FalconVendorId, ProductId = FalconDevice.FalconProductId,
                NumAxes = 3, NumButtons = 4, InputDeviceType = deviceType,
            });
            var remote = new UserDevice();
            remote.LoadFromExternalDevice(peer);
            var frames = new System.Collections.Generic.List<OutputEffectCodec.OutputEffect>();
            RemoteLinkOutputRouter.Register(peer.DevicePath, "owner", 0);
            RemoteLinkOutputRouter.SendOutput = (_, _, bytes) =>
            {
                Assert.True(OutputEffectCodec.TryDecode(bytes, out var effect));
                frames.Add(effect);
            };
            try
            {
                SettingsManager.UserSettings = new SettingsCollection();
                var setting = new UserSetting { InstanceGuid = remote.InstanceGuid, MapTo = 0 };
                setting.SetPadSetting(new PadSetting { ForceOverall = "50" });
                SettingsManager.UserSettings.Items.Add(setting);
                var manager = new InputManager();
                var game = manager.VibrationStates[0];
                game.HasDirectionalData = true;
                game.EffectType = FfbEffectTypes.Const;
                game.SignedMagnitude = 8000;
                game.DeviceGain = 255;
                PollFeedback(manager, remote);
                var frame = Assert.Single(frames);
                Assert.Equal(magnitude, frame.Vibration.SignedMagnitude);
                Assert.Equal(deviceType == InputDeviceType.NovintFalcon, frame.Falcon.HasValue);
            }
            finally
            {
                SettingsManager.UserSettings = savedSettings;
                RemoteLinkOutputRouter.SendOutput = savedSend;
                RemoteLinkOutputRouter.Unregister(peer.DevicePath);
            }
        }

        // ── A scripted Falcon ──

        private sealed class ScriptedFalcon : IFalconLink
        {
            /// <summary>The handle's position the Falcon reports, in meters.</summary>
            public double X { get; init; }
            public double Y { get; init; }
            public double Z { get; init; }
            public volatile bool Fail;
            /// <summary>Every force write fails, as a write the USB link
            /// refuses does.</summary>
            public volatile bool ForceFails;
            /// <summary>Gravity compensation, or the forces, refuse to
            /// turn on.</summary>
            public bool GravityFails { get; init; }
            public bool EnableFails { get; init; }
            /// <summary>Run inside the serial number read and the close.</summary>
            public Action OnSerial { get; init; }
            public Action OnClose { get; init; }
            /// <summary>Every open fails with an error other than in use.</summary>
            public bool OpenFails { get; init; }
            /// <summary>The library's last error, in its words.</summary>
            public string ErrorText { get; init; } = string.Empty;
            private int _reads, _closes;
            private (double, double, double) _lastForce = (double.NaN, double.NaN, double.NaN);

            public int Reads => Volatile.Read(ref _reads);
            public int Closes => Volatile.Read(ref _closes);
            public (double, double, double) LastForce { get { lock (this) return _lastForce; } }

            public int DeviceCount() => 1;
            public int OpenIndex(int index) => OpenFails ? -1 : 0;
            public int Close(int id) { OnClose?.Invoke(); Interlocked.Increment(ref _closes); return 0; }
            public int SystemType(int id) => FalconDevice.SystemTypeFalcon;
            public int SerialNumber(int id, out ushort serial) { OnSerial?.Invoke(); serial = 1; return 0; }

            public int GetPosition(int id, out double x, out double y, out double z)
            {
                x = X; y = Y; z = Z;
                Interlocked.Increment(ref _reads);
                // A Falcon answers at about 1 kHz. A sleep keeps the test
                // thread from spinning a core.
                Thread.Sleep(1);
                return Fail ? -1 : 0;
            }

            public uint ButtonMask(int id) => 0;
            public int EnableForce(int id, bool on) => EnableFails ? -1 : 0;
            public int SetForce(int id, double x, double y, double z) { lock (this) _lastForce = (x, y, z); return ForceFails ? -1 : 0; }
            public int SetMaxForce(int id, double newtons) => 0;
            public int SetGravityCompensation(int id, bool on) => GravityFails ? -1 : 0;
            public int SetStandardGravity(int id, double metersPerSecondSquared) => 0;
            public int GetStatus(int id, int[] status) => 0;
            public double ComFrequency(int id) => 1.0;
            public int LastError() => 0;
            public string LastErrorText() => ErrorText;
        }

        private static string RepoFile(params string[] parts)
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !Directory.Exists(Path.Combine(root.FullName, "PadForge.App"))) root = root.Parent;
            Assert.NotNull(root);
            return Path.Combine(new[] { root.FullName }.Concat(parts).ToArray());
        }
    }
}
