using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.CompilerServices;
using Concentus;
using Concentus.Enums;
using HIDMaestro;
using PadForge.Common.Input;
using Xunit.Abstractions;

namespace PadForge.Tests;

[Collection("AudioProfileLifecycle")]
public sealed class MicrophoneDecoderLifetimeTests(ITestOutputHelper output)
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;

    static void Backing(object obj, string name, object value)
        => obj.GetType().GetField("<" + name + ">k__BackingField", Private)!.SetValue(obj, value);

    sealed class Lane : IAsyncDisposable
    {
        public readonly AudioPassthroughService.PersonaFeed Feed;
        readonly HMMicrophoneInput microphone;
        readonly object engine;
        readonly byte[] ring = new byte[AudioPassthroughService.MicRingBytes(48000, 2)];
        readonly NamedPipeServerStream server;
        readonly Thread reader;
        readonly Task connection;
        public readonly IOpusEncoder Encoder = OpusCodecFactory.CreateEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_AUDIO);
        public readonly IOpusDecoder Expected = OpusCodecFactory.CreateDecoder(48000, 1);
        int frame;

        public Lane(int slot)
        {
            string name = "PadForge-mic-lifetime-" + Guid.NewGuid().ToString("N");
            server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            connection = server.WaitForConnectionAsync();
            var audio = (HMUsbAudio)RuntimeHelpers.GetUninitializedObject(typeof(HMUsbAudio));
            microphone = (HMMicrophoneInput)RuntimeHelpers.GetUninitializedObject(typeof(HMMicrophoneInput));
            Backing(audio, "Microphone", microphone);
            Backing(microphone, "Channels", 1);
            Backing(microphone, "SampleRateHz", 48000);
            Backing(microphone, "BitsPerSample", 16);
            var engineField = typeof(HMMicrophoneInput).GetField("_engine", Private)!;
            engine = RuntimeHelpers.GetUninitializedObject(engineField.FieldType);
            engine.GetType().GetField("_lock", Private)!.SetValue(engine, new object());
            // HIDMaestro 2.0.0 reads IsStreaming from the stream table: empty reads as not streaming.
            engine.GetType().GetField("_altSetting", Private)!.SetValue(engine, new Dictionary<byte, byte>());
            engine.GetType().GetField("_micRing", Private)!.SetValue(engine, ring);
            engine.GetType().GetField("_micFrameBytes", Private)!.SetValue(engine, 2);
            engineField.SetValue(microphone, engine);
            Feed = new AudioPassthroughService.PersonaFeed
            {
                Owner = new(), Audio = audio, Slot = slot, Published = true,
                RouteGeneration = 1, BtMicGen = 1, BtMicPadGuid = Guid.NewGuid()
            };
            reader = new Thread(() => typeof(AudioPassthroughService).GetMethod("BtMicLoop", Static)!
                .Invoke(null, [Feed, @"\\.\pipe\" + name, 1, 1])) { IsBackground = true };
            Feed.BtMicThread = reader;
            Encoder.Bitrate = 71 * 8 * 100;
            Encoder.UseVBR = false;
            reader.Start();
        }

        public Task Connect() => connection.WaitAsync(TimeSpan.FromSeconds(5));

        byte[] Packet()
        {
            var pcm = new short[480];
            for (int i = 0; i < pcm.Length; i++)
                pcm[i] = (short)(12000 * Math.Sin((frame * 480 + i) * 2 * Math.PI * 437 / 48000));
            frame++;
            var packet = new byte[78];
            packet[0] = 0x31;
            packet[1] = 2;
            Assert.Equal(71, Encoder.Encode(pcm.AsSpan(), 480, packet.AsSpan(3, 71), 71));
            return packet;
        }

        void SetRing(int used)
        {
            lock (engine.GetType().GetField("_lock", Private)!.GetValue(engine)!)
            {
                engine.GetType().GetField("_micTail", Private)!.SetValue(engine, 0);
                engine.GetType().GetField("_micHead", Private)!.SetValue(engine, used);
            }
        }

        public async Task<bool> Decode(bool reset = false)
        {
            SetRing(0);
            byte[] packet = Packet();
            if (reset) Expected.ResetState();
            var expected = new short[480];
            Assert.Equal(480, Expected.Decode(packet.AsSpan(3, 71), expected.AsSpan(), 480, false));
            await server.WriteAsync(packet);
            Assert.True(SpinWait.SpinUntil(() => microphone.BufferedBytes == 960, 5000));
            var actual = new short[480];
            Buffer.BlockCopy(ring, 0, actual, 0, 960);
            Assert.Contains(actual, value => value != 0);
            return expected.SequenceEqual(actual);
        }

        public async Task Skip()
        {
            SetRing(ring.Length - 2);
            long before = Feed.BtMicRxFrames;
            await server.WriteAsync(Packet());
            Assert.True(SpinWait.SpinUntil(() => Feed.BtMicRxFrames > before, 5000));
            // A following state report proves the skipped frame has finished.
            var state = new byte[78];
            state[0] = 0x31;
            state[55] = 1;
            await server.WriteAsync(state);
            Assert.True(SpinWait.SpinUntil(() => AudioPassthroughService.TryGetHeadphoneJack(Feed.BtMicPadGuid) == true, 5000));
        }

        public async ValueTask DisposeAsync()
        {
            Feed.BtMicStop = true;
            if (server.IsConnected && reader.IsAlive) await server.WriteAsync(new byte[78]);
            Assert.True(reader.Join(5000));
            server.Dispose();
            Encoder.Dispose();
            Expected.Dispose();
            var states = (ConcurrentDictionary<Guid, bool>)typeof(AudioPassthroughService).GetField("s_padJackState", Static)!.GetValue(null)!;
            states.TryRemove(Feed.BtMicPadGuid, out _);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdleMicrophoneResetsOnlyItsOwnDecoder(bool resumeIdleFirst)
    {
        await using var active = new Lane(14);
        await using var idle = new Lane(15);
        await Task.WhenAll(active.Connect(), idle.Connect());
        for (int i = 0; i < 4; i++)
        {
            Assert.True(await active.Decode());
            Assert.True(await idle.Decode());
        }
        await idle.Skip();
        bool activeMatches, resumedMatches;
        if (resumeIdleFirst)
        {
            resumedMatches = await idle.Decode(reset: true);
            activeMatches = await active.Decode();
        }
        else
        {
            activeMatches = await active.Decode();
            resumedMatches = await idle.Decode(reset: true);
        }
        output.WriteLine($"resumeIdleFirst={resumeIdleFirst} activeMatches={activeMatches} resumedMatches={resumedMatches}");
        Assert.True(activeMatches);
        Assert.True(resumedMatches);
    }
}
