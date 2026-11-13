// SPDX-License-Identifier: GPL-3.0-only
// Modified 2026-11-13 for AirPods Head Mouse: AirPods model presets, motion decoding, and RTBuddy framing.

using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.ApplicationModel.AppService;
using Windows.Foundation.Collections;

namespace AapHeadtracker;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args is ["--self-test"]) return AirPods5SelfTest.Run();

        try
        {
            var options = Options.Parse(args);
            if (options.ShowHelp)
            {
                PrintHelp();
                return 0;
            }

            return options.Command switch
            {
                Command.List => RunList(),
                Command.Ipc => await RunMagicPodsIpc(options),
                Command.Probe => await RunProbe(options),
                Command.Bridge => await RunBridge(options),
                _ => UsageError("Unknown command.")
            };
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine();
            Console.WriteLine("Stopped.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static int RunList()
    {
        var candidates = MagicAapDeviceEnumerator.Enumerate().ToList();
        if (candidates.Count == 0)
        {
            Console.WriteLine("No MagicAAP/AAP device interfaces were found.");
            return 2;
        }

        foreach (var candidate in candidates)
        {
            Console.WriteLine($"{candidate.Kind}: {candidate.InterfaceClassGuid}");
            Console.WriteLine($"  {candidate.Path}");

            var status = MagicAapDevice.TryOpenForQuery(candidate.Path, out var error)
                ? "openable"
                : $"not openable: {error}";

            Console.WriteLine($"  status: {status}");
        }

        return 0;
    }

    private static async Task<int> RunMagicPodsIpc(Options options)
    {
        using var lifetime = ConsoleLifetime.Create(options.Seconds);
        using var connection = new AppServiceConnection
        {
            AppServiceName = MagicPodsAppService.ServiceName,
            PackageFamilyName = MagicPodsAppService.PackageFamilyName
        };

        connection.ServiceClosed += (_, args) =>
        {
            Console.WriteLine($"MagicPods AppService closed: {args.Status}");
        };
        connection.RequestReceived += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                Console.WriteLine();
                Console.WriteLine("EVENT from MagicPods");
                MagicPodsAppService.PrintValueSet(args.Request.Message, "  ");
                await args.Request.SendResponseAsync(new ValueSet());
            }
            finally
            {
                deferral.Complete();
            }
        };

        Console.WriteLine($"Opening MagicPods AppService '{MagicPodsAppService.ServiceName}'...");
        var openStatus = await connection.OpenAsync();
        Console.WriteLine($"Open status: {openStatus}");
        if (openStatus != AppServiceConnectionStatus.Success)
        {
            return 4;
        }

        var commands = options.IpcCommand is not null
            ? [options.IpcCommand]
            : MagicPodsAppService.DefaultProbeCommands;

        foreach (var command in commands)
        {
            if (lifetime.Token.IsCancellationRequested)
            {
                break;
            }

            var message = MagicPodsAppService.CreateCommandMessage(command);
            Console.WriteLine();
            Console.WriteLine($"SEND {command}");
            var response = await connection.SendMessageAsync(message);
            Console.WriteLine($"RESPONSE {command}: {response.Status}");
            MagicPodsAppService.PrintValueSet(response.Message, "  ");
        }

        if (options.Seconds > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Listening for pushed MagicPods events for {options.Seconds:N0}s total. Press Ctrl+C to stop early.");
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token);
            }
            catch (OperationCanceledException) when (lifetime.Token.IsCancellationRequested)
            {
            }
        }

        return 0;
    }

    private static async Task<int> RunProbe(Options options)
    {
        using var lifetime = ConsoleLifetime.Create(options.Seconds);
        using var device = MagicAapDevice.Open(options);

        Console.WriteLine($"Opened {device.Candidate.Kind} interface ({device.IoMode} I/O).");
        Console.WriteLine(device.Candidate.Path);

        await using var dump = RawDump.Open(options.RawDumpPath);
        var decoder = new HeadTrackingDecoder(options.StartFormat, options.Model);
        var stopwatch = Stopwatch.StartNew();
        var sentStart = false;
        var headPackets = 0;
        var rawChunks = 0;
        var framer = new AapStreamFramer();
        var sessionState = new AapSessionState();

        try
        {
            if (options.SendMagicPodsInit)
            {
                await SendInitPackets(device, options, lifetime.Token);
            }

            if (options.PreStartReadMs > 0)
            {
                var summary = await DrainFor(
                    device,
                    dump,
                    framer,
                    decoder,
                    sessionState,
                    options.PreStartReadMs,
                    lifetime.Token,
                    printPacketSummaries: true,
                    label: "pre-start");

                rawChunks += summary.Chunks;
                headPackets += summary.HeadPackets;
            }

            if (options.TakeOwnership)
            {
                await SendTakeoverPackets(device, options, sessionState, lifetime.Token);
                var summary = await DrainFor(
                    device,
                    dump,
                    framer,
                    decoder,
                    sessionState,
                    options.TakeoverReadMs,
                    lifetime.Token,
                    printPacketSummaries: true,
                    label: "takeover");

                rawChunks += summary.Chunks;
                headPackets += summary.HeadPackets;
            }

            if (options.RawSendPacket is not null)
            {
                await SendRawPacket(device, options.RawSendPacket, lifetime.Token);
            }

            if (options.SendStart)
            {
                if (options.StartDelayMs > 0)
                {
                    Console.WriteLine($"Waiting {options.StartDelayMs}ms before start packet.");
                    await Task.Delay(options.StartDelayMs, lifetime.Token);
                }

                await SendStartPackets(device, options, lifetime.Token);
                sentStart = true;
            }

            Console.WriteLine(options.Seconds > 0
                ? $"Reading for {options.Seconds:N0}s. Press Ctrl+C to stop early."
                : "Reading until Ctrl+C.");

            var buffer = new byte[4096];
            while (!lifetime.Token.IsCancellationRequested)
            {
                var count = await ReadOrStop(device, buffer, lifetime.Token);
                if (count <= 0)
                {
                    break;
                }

                rawChunks++;
                var chunk = buffer.AsSpan(0, count).ToArray();
                await dump.WriteAsync(chunk, lifetime.Token);

                foreach (var packet in framer.Push(chunk))
                {
                    PrintPacketSummary(packet, decoder, ref headPackets);
                }
            }

            foreach (var packet in framer.Complete())
            {
                PrintPacketSummary(packet, decoder, ref headPackets);
            }
        }
        finally
        {
            if (sentStart && options.SendStop)
            {
                await TrySendStop(device, options);
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Probe summary: chunks={rawChunks}, headPackets={headPackets}, elapsed={stopwatch.Elapsed.TotalSeconds:N1}s");
        if (dump.Path is not null)
        {
            Console.WriteLine($"Raw dump: {dump.Path}");
        }

        return headPackets > 0 ? 0 : 3;
    }

    private static async Task<int> RunBridge(Options options)
    {
        using var lifetime = ConsoleLifetime.Create(options.Seconds);
        using var device = MagicAapDevice.Open(options);
        using var udp = new OpenTrackUdpSender(options.Host, options.Port);
        await using var dump = RawDump.Open(options.RawDumpPath);
        var decoder = new HeadTrackingDecoder(options.StartFormat, options.Model);

        Console.WriteLine($"Opened {device.Candidate.Kind} interface ({device.IoMode} I/O).");
        Console.WriteLine(device.Candidate.Path);
        Console.WriteLine($"OpenTrack UDP target: {options.Host}:{options.Port}");

        var sentStart = false;
        var headPackets = 0;
        var sentSamples = 0;
        var lastDisplay = Stopwatch.StartNew();
        var buffer = new byte[4096];
        var framer = new AapStreamFramer();
        var sessionState = new AapSessionState();

        try
        {
            if (options.SendMagicPodsInit)
            {
                await SendInitPackets(device, options, lifetime.Token);
            }

            if (options.PreStartReadMs > 0)
            {
                await DrainFor(
                    device,
                    dump,
                    framer,
                    decoder,
                    sessionState,
                    options.PreStartReadMs,
                    lifetime.Token,
                    printPacketSummaries: false,
                    label: "pre-start");
            }

            if (options.TakeOwnership)
            {
                await SendTakeoverPackets(device, options, sessionState, lifetime.Token);
                await DrainFor(
                    device,
                    dump,
                    framer,
                    decoder,
                    sessionState,
                    options.TakeoverReadMs,
                    lifetime.Token,
                    printPacketSummaries: false,
                    label: "takeover");
            }

            if (options.RawSendPacket is not null)
            {
                await SendRawPacket(device, options.RawSendPacket, lifetime.Token);
            }

            if (options.SendStart)
            {
                if (options.StartDelayMs > 0)
                {
                    Console.WriteLine($"Waiting {options.StartDelayMs}ms before start packet.");
                    await Task.Delay(options.StartDelayMs, lifetime.Token);
                }

                await SendStartPackets(device, options, lifetime.Token);
                sentStart = true;
            }

            Console.WriteLine(options.Seconds > 0
                ? $"Bridging for {options.Seconds:N0}s. Press Ctrl+C to stop early."
                : "Bridging until Ctrl+C.");

            while (!lifetime.Token.IsCancellationRequested)
            {
                var count = await ReadOrStop(device, buffer, lifetime.Token);
                if (count <= 0)
                {
                    break;
                }

                var chunk = buffer.AsSpan(0, count).ToArray();
                await dump.WriteAsync(chunk, lifetime.Token);

                foreach (var packet in framer.Push(chunk))
                {
                    if (!decoder.IsHeadTrackingPacket(packet))
                    {
                        continue;
                    }

                    headPackets++;
                    var sample = decoder.Process(packet);
                    if (sample is null)
                    {
                        if (lastDisplay.ElapsedMilliseconds >= 500)
                        {
                            Console.Write($"\rCalibrating from head packets... {decoder.CalibrationCount}/{decoder.RequiredSamples}     ");
                            lastDisplay.Restart();
                        }

                        continue;
                    }

                    udp.Send(sample.Value);
                    sentSamples++;

                    if (lastDisplay.ElapsedMilliseconds >= 100)
                    {
                        Console.Write(
                            $"\rYPR {sample.Value.Yaw,7:0.00} {sample.Value.Pitch,7:0.00} {sample.Value.Roll,7:0.00}  packets={headPackets} sent={sentSamples}     ");
                        lastDisplay.Restart();
                    }
                }
            }
        }
        finally
        {
            if (sentStart && options.SendStop)
            {
                await TrySendStop(device, options);
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Bridge summary: headPackets={headPackets}, sentSamples={sentSamples}");
        if (dump.Path is not null)
        {
            Console.WriteLine($"Raw dump: {dump.Path}");
        }

        return sentSamples > 0 ? 0 : 3;
    }

    private static async Task SendStartPackets(MagicAapDevice device, Options options, CancellationToken cancellationToken)
    {
        foreach (var packet in AapPackets.BuildStartPackets(options.StartFormat, options.HeadMode))
        {
            await device.WritePacketAsync(packet.Bytes, cancellationToken);
            Console.WriteLine($"Sent {packet.Label} start packet ({packet.Bytes.Length} bytes): {Hex.Preview(packet.Bytes)}");
        }
    }

    private static async Task SendRawPacket(
        MagicAapDevice device,
        byte[] packet,
        CancellationToken cancellationToken)
    {
        await device.WritePacketAsync(packet, cancellationToken);
        Console.WriteLine($"Sent custom raw packet ({packet.Length} bytes): {Hex.Full(packet)}");
    }

    private static async Task SendTakeoverPackets(
        MagicAapDevice device,
        Options options,
        AapSessionState sessionState,
        CancellationToken cancellationToken)
    {
        var selfMac = MacAddress.Normalize(options.SelfMac)
            ?? BluetoothMacResolver.TryFindLocalBluetoothMac()
            ?? sessionState.AudioSourceMac;

        if (selfMac is null)
        {
            Console.WriteLine("Could not infer local Bluetooth MAC; sending ownership control only. Use --self-mac AA:BB:CC:DD:EE:FF to enable smart-routing takeover packets.");
        }
        else
        {
            Console.WriteLine($"Using local Bluetooth MAC for takeover: {selfMac}");
        }

        var targets = selfMac is null
            ? new List<string>()
            : sessionState.ConnectedDeviceMacs
                .Where(mac => !string.Equals(mac, selfMac, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        if (selfMac is not null && targets.Count == 0)
        {
            Console.WriteLine("No non-local connected devices were parsed yet; sending ownership control only.");
        }
        else if (targets.Count > 0)
        {
            Console.WriteLine($"Smart-routing takeover targets: {string.Join(", ", targets)}");
        }

        foreach (var packet in AapPackets.BuildTakeoverPackets(selfMac, targets))
        {
            await device.WritePacketAsync(packet.Bytes, cancellationToken);
            Console.WriteLine($"Sent {packet.Label} packet ({packet.Bytes.Length} bytes): {Hex.Preview(packet.Bytes)}");
        }
    }

    private static async Task TrySendStop(MagicAapDevice device, Options options)
    {
        foreach (var packet in AapPackets.BuildStopPackets(options.StartFormat, options.StopMode))
        {
            try
            {
                await device.WritePacketAsync(packet.Bytes, CancellationToken.None);
                Console.WriteLine();
                Console.WriteLine($"Sent {packet.Label} stop packet ({packet.Bytes.Length} bytes): {Hex.Preview(packet.Bytes)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"Could not send {packet.Label} stop packet: {ex.Message}");
            }
        }
    }

    private static async Task SendInitPackets(MagicAapDevice device, Options options, CancellationToken cancellationToken)
    {
        var packets = AapPackets.BuildInitPackets(options.InitFormat).ToList();
        for (var i = 0; i < packets.Count; i++)
        {
            var packet = packets[i];
            await device.WritePacketAsync(packet.Bytes, cancellationToken);
            Console.WriteLine($"Sent {packet.Label} init packet {i + 1}/{packets.Count} ({packet.Bytes.Length} bytes): {Hex.Preview(packet.Bytes)}");

            if (options.InitFormat is (InitPacketFormat.RtBuddy or InitPacketFormat.RtBuddyAacp) && i < packets.Count - 1)
            {
                await Task.Delay(i % 2 == 0 ? 180 : 220, cancellationToken);
            }
        }
    }

    private static async Task<PacketReadSummary> DrainFor(
        MagicAapDevice device,
        RawDump dump,
        AapStreamFramer framer,
        HeadTrackingDecoder decoder,
        AapSessionState sessionState,
        int milliseconds,
        CancellationToken cancellationToken,
        bool printPacketSummaries,
        string label)
    {
        if (milliseconds <= 0)
        {
            return new PacketReadSummary(0, 0, 0);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(milliseconds);

        Console.WriteLine($"Reading {label} traffic for {milliseconds}ms.");

        var buffer = new byte[4096];
        var chunks = 0;
        var packets = 0;
        var headPackets = 0;

        while (!timeout.Token.IsCancellationRequested)
        {
            var count = await ReadOrStop(device, buffer, timeout.Token);
            if (count <= 0)
            {
                break;
            }

            chunks++;
            var chunk = buffer.AsSpan(0, count).ToArray();
            await dump.WriteAsync(chunk, cancellationToken);

            foreach (var packet in framer.Push(chunk))
            {
                packets++;
                sessionState.Observe(packet);
                if (printPacketSummaries)
                {
                    PrintPacketSummary(packet, decoder, ref headPackets);
                }
                else if (decoder.IsHeadTrackingPacket(packet))
                {
                    headPackets++;
                }
            }
        }

        Console.WriteLine($"{CultureInfo.CurrentCulture.TextInfo.ToTitleCase(label)} read summary: chunks={chunks}, packets={packets}, headPackets={headPackets}");
        if (sessionState.ConnectedDeviceMacs.Count > 0 || sessionState.AudioSourceMac is not null || sessionState.OwnsConnection is not null)
        {
            var owns = sessionState.OwnsConnection is null ? "unknown" : sessionState.OwnsConnection.Value ? "yes" : "no";
            var connected = sessionState.ConnectedDeviceMacs.Count == 0 ? "none" : string.Join(", ", sessionState.ConnectedDeviceMacs);
            Console.WriteLine($"Session state: audioSource={sessionState.AudioSourceMac ?? "unknown"}, ownsConnection={owns}, connected=[{connected}]");
        }

        return new PacketReadSummary(chunks, packets, headPackets);
    }

    private static async Task<int> ReadOrStop(MagicAapDevice device, byte[] buffer, CancellationToken cancellationToken)
    {
        try
        {
            return await device.ReadAsync(buffer, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
    }

    private static void PrintPacketSummary(byte[] packet, HeadTrackingDecoder decoder, ref int headPackets)
    {
        var prefix = AapPackets.HasAapPrefix(packet) ? "AAP" : "raw";
        var opcode = packet.Length > 4 ? $" opcode=0x{packet[4]:X2}" : "";
        var kind = decoder.IsHeadTrackingPacket(packet) ? " head" : "";
        var rtBuddy = RtBuddyFrameInfo.TryParse(packet, out var frame)
            ? $" {frame.Describe()}"
            : "";
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {prefix}{opcode}{kind} len={packet.Length}{rtBuddy} {Hex.Preview(packet)}");

        if (!decoder.IsHeadTrackingPacket(packet))
        {
            return;
        }

        headPackets++;
        var sample = decoder.Process(packet);
        if (sample is null)
        {
            Console.WriteLine($"  calibrating {decoder.CalibrationCount}/{decoder.RequiredSamples}");
            return;
        }

        Console.WriteLine(
            $"  ypr={sample.Value.Yaw:0.00},{sample.Value.Pitch:0.00},{sample.Value.Roll:0.00} q15={sample.Value.RawO1},{sample.Value.RawO2},{sample.Value.RawO3} qw={sample.Value.RawQuaternionW:0.0000} accel={sample.Value.HorizontalAcceleration},{sample.Value.VerticalAcceleration}");
    }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine();
        PrintHelp();
        return 64;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
        AAP Headtracker - MagicAAP/AirPods proof-of-life bridge

        Commands:
          aap-headtracker list
          aap-headtracker ipc    [--seconds N] [--command GetHeadphones]
          aap-headtracker probe  [--seconds N] [--interface auto|private|service] [--path DEVICE_PATH] [--sync] [--no-init] [--init-format magicpods|librepods|rtbuddy|rtbuddy-aacp|both] [--prestart-read-ms N] [--takeover] [--self-mac AA:BB:CC:DD:EE:FF] [--takeover-read-ms N] [--send-hex HEX] [--start-format magicpods|legacy|alternate|airpods5|devmotion6|max2|both] [--start-delay-ms N] [--head-mode 1|2|3] [--stop-mode 1|2|3] [--no-start] [--no-stop] [--dump]
          aap-headtracker bridge [--seconds N] [--port 4242] [--host 127.0.0.1] [--interface auto|private|service] [--path DEVICE_PATH] [--sync] [--no-init] [--init-format magicpods|librepods|rtbuddy|rtbuddy-aacp|both] [--prestart-read-ms N] [--takeover] [--self-mac AA:BB:CC:DD:EE:FF] [--takeover-read-ms N] [--send-hex HEX] [--start-format magicpods|legacy|alternate|airpods5|devmotion6|max2|both] [--start-delay-ms N] [--head-mode 1|2|3] [--stop-mode 1|2|3] [--no-start] [--no-stop] [--dump]

        Notes:
          list   only enumerates and query-opens device interfaces.
          ipc    connects to MagicPods' SystrayExtensionService and prints AppService responses/events.
          probe  starts AirPods head tracking by default, prints packet summaries, and exits nonzero if no head packets are seen.
          bridge starts AirPods head tracking by default and sends OpenTrack-compatible UDP: six little-endian doubles x,y,z,yaw,pitch,roll.
          probe/bridge default to RTBuddy init, a 1000 ms reply drain, and the confirmed Max 2 service-6 start packet.
          auto   tries the private MagicAAP interface first, then the AAP service interface.
          sync   uses MagicPods-style blocking ReadFile/WriteFile instead of an overlapped FileStream.
          init   sends AirPods setup/notification packets before the head-tracking start packet.
          --model airpods5|pro2|max2|airpods3|airpods4|pro1|pro3|max1 selects a Bluetooth preset.
          Override --start-format to try alternate or legacy if a preset yields no data.
          init-format librepods sends the exact INIT + head-tracking notification request used by the LibrePods visualizer.
          init-format rtbuddy connects AACP services 0 and 4 at version 1.3 and requests both capability sets.
          prestart-read-ms drains and prints device replies before the head-tracking start packet.
          takeover sends LibrePods' OWNS_CONNECTION control command before the head-tracking start packet.
          self-mac overrides the local Bluetooth MAC used for smart-routing takeover packets.
          send-hex transmits one custom packet after init/takeover; combine it with --no-start for isolated protocol probes.
          start-delay-ms waits after init/notification setup before sending the start packet.
          start-format magicpods sends MagicPods' raw 12-byte FlyccSetHeadTracking stage packet.
          start-format legacy sends the older rtbuddy-derived 0x17 AAP packet guess.
          start-format alternate sends LibrePods' newer alternate 0x17 AAP packet.
          start-format airpods5 starts CMA_COMMAND and stops DEVMOTION6 (experimental on Windows).
          start-format devmotion6 requests RTBuddy DEVMOTION6 sensor samples at 50 Hz.
          start-format max2 requests the dynamically advertised Max 2 devmotion6 service (ID 6 on firmware 8E258).
        """);
    }
}

internal readonly record struct RtBuddyFrameInfo(
    uint Descriptor,
    ushort DeclaredLength,
    ulong? Sequence,
    ulong? LogType,
    int? SensorStreamLength,
    int? DescriptorService,
    int? DescriptorPayloadLength,
    int? CommandService,
    int? CommandPayloadLength,
    int? SettingService,
    int? SettingMode,
    uint? SettingIntervalUs,
    int? StartAckService,
    int? CommandAckService)
{
    public static bool TryParse(ReadOnlySpan<byte> packet, out RtBuddyFrameInfo frame)
    {
        frame = default;
        if (packet.Length < 12
            || !AapPackets.HasAapPrefix(packet)
            || packet[4] != 0x17
            || packet[5] != 0x00)
        {
            return false;
        }

        var descriptor = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(6, 4));
        var declaredLength = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(10, 2));
        var payloadLength = Math.Min(declaredLength, packet.Length - 12);
        var reader = new ProtobufReader(packet.Slice(12, payloadLength));

        ulong? sequence = null;
        ulong? logType = null;
        int? sensorStreamLength = null;
        int? descriptorService = null;
        int? descriptorPayloadLength = null;
        int? commandService = null;
        int? commandPayloadLength = null;
        int? settingService = null;
        int? settingMode = null;
        uint? settingIntervalUs = null;
        int? startAckService = null;
        int? commandAckService = null;

        while (reader.TryRead(out var field))
        {
            switch (field.Number)
            {
                case 1 when field.WireType == 0:
                    sequence = field.Varint;
                    break;
                case 2 when field.WireType == 0:
                    logType = field.Varint;
                    break;
                case 3 when field.WireType == 2:
                    sensorStreamLength = field.Bytes.Length;
                    break;
                case 5 when field.WireType == 2:
                    ParseServiceMessage(field.Bytes, payloadField: 2, out descriptorService, out descriptorPayloadLength);
                    break;
                case 7 when field.WireType == 2:
                    ParseServiceMessage(field.Bytes, payloadField: 3, out commandService, out commandPayloadLength);
                    break;
                case 8 when field.WireType == 2:
                    ParseServiceSetting(field.Bytes, out settingService, out settingMode, out settingIntervalUs);
                    break;
                case 9 when field.WireType == 2:
                    startAckService = ParseService(field.Bytes);
                    break;
                case 12 when field.WireType == 2:
                    commandAckService = ParseService(field.Bytes);
                    break;
            }
        }

        frame = new RtBuddyFrameInfo(
            descriptor,
            declaredLength,
            sequence,
            logType,
            sensorStreamLength,
            descriptorService,
            descriptorPayloadLength,
            commandService,
            commandPayloadLength,
            settingService,
            settingMode,
            settingIntervalUs,
            startAckService,
            commandAckService);
        return true;
    }

    public static bool TryGetCommandPayload(
        ReadOnlySpan<byte> packet,
        out int service,
        out ReadOnlySpan<byte> payload)
    {
        service = -1;
        payload = default;
        if (packet.Length < 12
            || !AapPackets.HasAapPrefix(packet)
            || packet[4] != 0x17
            || packet[5] != 0x00
            || BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(6, 4)) != 0x00100000)
        {
            return false;
        }

        var declaredLength = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(10, 2));
        if (declaredLength > packet.Length - 12) return false;
        var reader = new ProtobufReader(packet.Slice(12, declaredLength));
        while (reader.TryRead(out var field))
        {
            if (field.Number != 7 || field.WireType != 2)
            {
                continue;
            }

            var commandReader = new ProtobufReader(field.Bytes);
            while (commandReader.TryRead(out var commandField))
            {
                if (commandField.Number == 1 && commandField.WireType == 0)
                {
                    if (commandField.Varint > int.MaxValue) return false;
                    service = (int)commandField.Varint;
                }
                else if (commandField.Number == 3 && commandField.WireType == 2)
                {
                    payload = commandField.Bytes;
                }
            }

            return service >= 0 && !payload.IsEmpty;
        }

        return false;
    }

    public string Describe()
    {
        var details = new List<string>
        {
            $"rtbuddy={DescriptorName(Descriptor)}",
            $"pb={DeclaredLength}"
        };

        if (Sequence is not null)
        {
            details.Add($"seq={Sequence}");
        }

        if (LogType is not null)
        {
            details.Add($"log={LogType}");
        }

        if (SensorStreamLength is not null)
        {
            details.Add($"stream={SensorStreamLength}");
        }

        if (DescriptorService is not null)
        {
            details.Add($"descriptor={ServiceName(DescriptorService.Value)}/{DescriptorPayloadLength ?? 0}");
        }

        if (CommandService is not null)
        {
            details.Add($"command={ServiceName(CommandService.Value)}/{CommandPayloadLength ?? 0}");
        }

        if (SettingService is not null)
        {
            details.Add($"setting={ServiceName(SettingService.Value)}:{SettingMode ?? -1}@{SettingIntervalUs ?? 0}us");
        }

        if (StartAckService is not null)
        {
            details.Add($"startAck={ServiceName(StartAckService.Value)}");
        }

        if (CommandAckService is not null)
        {
            details.Add($"commandAck={ServiceName(CommandAckService.Value)}");
        }

        return string.Join(' ', details);
    }

    private static void ParseServiceMessage(
        ReadOnlySpan<byte> data,
        int payloadField,
        out int? service,
        out int? payloadLength)
    {
        service = null;
        payloadLength = null;
        var reader = new ProtobufReader(data);
        while (reader.TryRead(out var field))
        {
            if (field.Number == 1 && field.WireType == 0)
            {
                service = checked((int)field.Varint);
            }
            else if (field.Number == payloadField && field.WireType == 2)
            {
                payloadLength = field.Bytes.Length;
            }
        }
    }

    private static void ParseServiceSetting(
        ReadOnlySpan<byte> data,
        out int? service,
        out int? mode,
        out uint? intervalUs)
    {
        service = null;
        mode = null;
        intervalUs = null;
        var reader = new ProtobufReader(data);
        while (reader.TryRead(out var field))
        {
            if (field.Number == 1 && field.WireType == 0)
            {
                service = checked((int)field.Varint);
            }
            else if (field.Number == 2 && field.WireType == 0)
            {
                mode = checked((int)field.Varint);
            }
            else if (field.Number == 3 && field.WireType == 2 && field.Bytes.Length >= 5)
            {
                intervalUs = BinaryPrimitives.ReadUInt32LittleEndian(field.Bytes.Slice(1, 4));
            }
        }
    }

    private static int? ParseService(ReadOnlySpan<byte> data)
    {
        var reader = new ProtobufReader(data);
        while (reader.TryRead(out var field))
        {
            if (field.Number == 1 && field.WireType == 0)
            {
                return checked((int)field.Varint);
            }
        }

        return null;
    }

    private static string DescriptorName(uint descriptor) => descriptor switch
    {
        0x00000800 => "Sensor",
        0x00040000 => "SensorV2",
        0x00100000 => "SensorDataWX",
        _ => $"0x{descriptor:X8}"
    };

    private static string ServiceName(int service) => service switch
    {
        6 => "DEVMOTION6(Max2)",
        11 => "ACCEL",
        12 => "GYRO",
        13 => "PDR",
        14 => "ACTIVITY",
        15 => "CMA",
        16 => "DEVMOTION6",
        17 => "SPL0",
        18 => "HOSTLIBHID",
        19 => "HEARTRATE",
        _ => service.ToString(CultureInfo.InvariantCulture)
    };
}

internal readonly ref struct ProtobufField
{
    public ProtobufField(int number, int wireType, ulong varint, ReadOnlySpan<byte> bytes)
    {
        Number = number;
        WireType = wireType;
        Varint = varint;
        Bytes = bytes;
    }

    public int Number { get; }
    public int WireType { get; }
    public ulong Varint { get; }
    public ReadOnlySpan<byte> Bytes { get; }
}

internal ref struct ProtobufReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _offset;

    public ProtobufReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _offset = 0;
    }

    public bool TryRead(out ProtobufField field)
    {
        field = default;
        if (!TryReadVarint(out var key))
        {
            return false;
        }

        var number = checked((int)(key >> 3));
        var wireType = checked((int)(key & 0x07));
        switch (wireType)
        {
            case 0:
                if (!TryReadVarint(out var value))
                {
                    return false;
                }

                field = new ProtobufField(number, wireType, value, default);
                return true;
            case 1:
                return TryReadFixed(number, wireType, 8, out field);
            case 2:
                if (!TryReadVarint(out var rawLength)
                    || rawLength > int.MaxValue
                    || rawLength > (ulong)(_data.Length - _offset))
                {
                    return false;
                }

                var length = checked((int)rawLength);
                var bytes = _data.Slice(_offset, length);
                _offset += length;
                field = new ProtobufField(number, wireType, 0, bytes);
                return true;
            case 5:
                return TryReadFixed(number, wireType, 4, out field);
            default:
                return false;
        }
    }

    private bool TryReadFixed(int number, int wireType, int length, out ProtobufField field)
    {
        field = default;
        if (_data.Length - _offset < length)
        {
            return false;
        }

        var bytes = _data.Slice(_offset, length);
        _offset += length;
        field = new ProtobufField(number, wireType, 0, bytes);
        return true;
    }

    private bool TryReadVarint(out ulong value)
    {
        value = 0;
        for (var shift = 0; shift < 64 && _offset < _data.Length; shift += 7)
        {
            var current = _data[_offset++];
            value |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                return true;
            }
        }

        return false;
    }
}

internal enum Command
{
    Help,
    List,
    Ipc,
    Probe,
    Bridge
}

internal enum InterfacePreference
{
    Auto,
    Private,
    Service
}

internal enum StartPacketFormat
{
    MagicPods,
    Legacy,
    Alternate,
    AirPods5,
    DevMotion6,
    Max2,
    Both
}

internal enum InitPacketFormat
{
    MagicPods,
    LibrePods,
    RtBuddy,
    RtBuddyAacp,
    Both
}

internal sealed record Options(
    Command Command,
    InterfacePreference InterfacePreference,
    string? ExplicitPath,
    int Seconds,
    string Host,
    int Port,
    bool SendStart,
    bool SendStop,
    bool SendMagicPodsInit,
    bool TakeOwnership,
    InitPacketFormat InitFormat,
    StartPacketFormat StartFormat,
    int StartDelayMs,
    int PreStartReadMs,
    int TakeoverReadMs,
    byte HeadMode,
    byte StopMode,
    string? RawDumpPath,
    string? IpcCommand,
    string? SelfMac,
    byte[]? RawSendPacket,
    bool SyncIo,
    bool ShowHelp)
{
    public string? Model { get; init; }

    public static Options Parse(string[] args)
    {
        if (args.Length == 0 || Has(args, "--help") || Has(args, "-h") || Has(args, "/?"))
        {
            return Default(Command.Help) with { ShowHelp = true };
        }

        var command = args[0].ToLowerInvariant() switch
        {
            "list" => Command.List,
            "ipc" => Command.Ipc,
            "probe" => Command.Probe,
            "bridge" => Command.Bridge,
            "help" => Command.Help,
            _ => Command.Help
        };

        if (command == Command.Help)
        {
            return Default(command) with { ShowHelp = true };
        }

        var interfaceText = Value(args, "--interface", "auto").ToLowerInvariant();
        var preference = interfaceText switch
        {
            "auto" => InterfacePreference.Auto,
            "private" => InterfacePreference.Private,
            "service" => InterfacePreference.Service,
            _ => throw new ArgumentException($"Unknown --interface value '{interfaceText}'.")
        };

        var defaultSeconds = command switch
        {
            Command.Probe => 10,
            Command.Ipc => 15,
            _ => 0
        };
        var seconds = ParseInt(Value(args, "--seconds", defaultSeconds.ToString(CultureInfo.InvariantCulture)), "--seconds");
        var port = ParseInt(Value(args, "--port", "4242"), "--port");
        var rawDump = Has(args, "--dump") ? DefaultDumpPath() : ValueOrNull(args, "--dump-file");
        var useMax2Defaults = command is Command.Probe or Command.Bridge;
        var model = ValueOrNull(args, "--model")?.ToLowerInvariant();
        var preset = AirPodsPreset.Resolve(model);
        var initFormatText = Value(args, "--init-format", preset?.Init ?? (useMax2Defaults ? "rtbuddy" : "magicpods")).ToLowerInvariant();
        var initFormat = initFormatText switch
        {
            "magicpods" => InitPacketFormat.MagicPods,
            "librepods" => InitPacketFormat.LibrePods,
            "rtbuddy" => InitPacketFormat.RtBuddy,
            "rtbuddy-aacp" => InitPacketFormat.RtBuddyAacp,
            "both" => InitPacketFormat.Both,
            _ => throw new ArgumentException($"Unknown --init-format value '{initFormatText}'.")
        };
        var startFormatText = Value(args, "--start-format", preset?.Start ?? (useMax2Defaults ? "max2" : "magicpods")).ToLowerInvariant();
        var startFormat = startFormatText switch
        {
            "magicpods" => StartPacketFormat.MagicPods,
            "legacy" => StartPacketFormat.Legacy,
            "alternate" => StartPacketFormat.Alternate,
            "airpods5" => StartPacketFormat.AirPods5,
            "devmotion6" => StartPacketFormat.DevMotion6,
            "max2" => StartPacketFormat.Max2,
            "both" => StartPacketFormat.Both,
            _ => throw new ArgumentException($"Unknown --start-format value '{startFormatText}'.")
        };
        var headMode = ParseMode(Value(args, "--head-mode", "2"), "--head-mode");
        var stopMode = ParseMode(Value(args, "--stop-mode", "1"), "--stop-mode");
        var startDelayMs = ParseInt(Value(args, "--start-delay-ms", preset?.Delay ?? (useMax2Defaults ? "0" : "750")), "--start-delay-ms");
        if (startDelayMs < 0)
        {
            throw new ArgumentException("--start-delay-ms must be zero or greater.");
        }

        var preStartReadMs = ParseInt(Value(args, "--prestart-read-ms", useMax2Defaults ? "1000" : "0"), "--prestart-read-ms");
        if (preStartReadMs < 0)
        {
            throw new ArgumentException("--prestart-read-ms must be zero or greater.");
        }

        var takeoverReadMs = ParseInt(Value(args, "--takeover-read-ms", "750"), "--takeover-read-ms");
        if (takeoverReadMs < 0)
        {
            throw new ArgumentException("--takeover-read-ms must be zero or greater.");
        }

        return new Options(
            command,
            preference,
            ValueOrNull(args, "--path"),
            seconds,
            Value(args, "--host", "127.0.0.1"),
            port,
            SendStart: !Has(args, "--no-start"),
            SendStop: !Has(args, "--no-stop"),
            SendMagicPodsInit: !Has(args, "--no-init"),
            TakeOwnership: Has(args, "--takeover"),
            InitFormat: initFormat,
            StartFormat: startFormat,
            StartDelayMs: startDelayMs,
            PreStartReadMs: preStartReadMs,
            TakeoverReadMs: takeoverReadMs,
            HeadMode: headMode,
            StopMode: stopMode,
            RawDumpPath: rawDump,
            IpcCommand: ValueOrNull(args, "--command"),
            SelfMac: ValueOrNull(args, "--self-mac"),
            RawSendPacket: ValueOrNull(args, "--send-hex") is { } sendHex ? Hex.Parse(sendHex) : null,
            SyncIo: Has(args, "--sync"),
            ShowHelp: false) { Model = model };
    }

    private static Options Default(Command command) =>
        new(command, InterfacePreference.Auto, null, 0, "127.0.0.1", 4242, true, true, true, false, InitPacketFormat.MagicPods, StartPacketFormat.MagicPods, 750, 0, 750, 2, 1, null, null, null, null, false, false);

    private static string DefaultDumpPath()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "captures");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"magicaap-{DateTime.Now:yyyyMMdd-HHmmss}.hexlog");
    }

    private static int ParseInt(string text, string option)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new ArgumentException($"{option} must be an integer.");
        }

        return value;
    }

    private static byte ParseMode(string text, string option)
    {
        var value = ParseInt(text, option);
        if (value is < 1 or > 3)
        {
            throw new ArgumentException($"{option} must be 1, 2, or 3.");
        }

        return checked((byte)value);
    }

    private static bool Has(IReadOnlyList<string> args, string key) =>
        args.Any(arg => string.Equals(arg, key, StringComparison.OrdinalIgnoreCase));

    private static string Value(IReadOnlyList<string> args, string key, string fallback) =>
        ValueOrNull(args, key) ?? fallback;

    private static string? ValueOrNull(IReadOnlyList<string> args, string key)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}

internal static class MagicAapGuids
{
    public static readonly Guid AapServerService = new("74ec2172-0bad-4d01-8f77-997b2be0722a");
    public static readonly Guid MagicAapPrivate = new("9eec98bb-3c54-45d4-a843-7900c4635e08");
    public static readonly Guid AapClientService = new("1ff31936-572e-4b36-a2bf-b2409b1aa6f4");

    public static readonly IReadOnlyList<(string Kind, Guid Guid)> KnownInterfaces =
    [
        ("private", MagicAapPrivate),
        ("service", AapServerService),
        ("client", AapClientService)
    ];
}

internal sealed record DeviceInterfaceCandidate(string Kind, Guid InterfaceClassGuid, string Path);

internal static class MagicAapDeviceEnumerator
{
    public static IEnumerable<DeviceInterfaceCandidate> Enumerate()
    {
        foreach (var (kind, guid) in MagicAapGuids.KnownInterfaces)
        {
            foreach (var path in ConfigurationManagerNative.GetPresentDeviceInterfacePaths(guid))
            {
                yield return new DeviceInterfaceCandidate(kind, guid, path);
            }
        }
    }
}

internal sealed class MagicAapDevice : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly FileStream? _stream;
    private readonly bool _syncIo;

    private MagicAapDevice(DeviceInterfaceCandidate candidate, SafeFileHandle handle, bool syncIo)
    {
        Candidate = candidate;
        _handle = handle;
        _syncIo = syncIo;
        _stream = syncIo
            ? null
            : new FileStream(handle, FileAccess.ReadWrite, bufferSize: 4096, isAsync: true);
    }

    public DeviceInterfaceCandidate Candidate { get; }
    public string IoMode => _syncIo ? "synchronous" : "overlapped";

    public static MagicAapDevice Open(Options options)
    {
        if (options.ExplicitPath is not null)
        {
            var candidate = new DeviceInterfaceCandidate("explicit", Guid.Empty, options.ExplicitPath);
            return OpenCandidate(candidate, options.SyncIo);
        }

        var all = MagicAapDeviceEnumerator.Enumerate().ToList();
        var ordered = options.InterfacePreference switch
        {
            InterfacePreference.Private => all.Where(c => c.Kind == "private"),
            InterfacePreference.Service => all.Where(c => c.Kind == "service"),
            _ => all.Where(c => c.Kind is "private" or "service").OrderBy(c => c.Kind == "private" ? 0 : 1)
        };

        var errors = new List<string>();
        foreach (var candidate in ordered)
        {
            try
            {
                return OpenCandidate(candidate, options.SyncIo);
            }
            catch (Exception ex)
            {
                errors.Add($"{candidate.Kind}: {ex.Message}");
            }
        }

        var suffix = errors.Count == 0 ? "No candidate interfaces matched." : string.Join(Environment.NewLine, errors);
        throw new InvalidOperationException($"Could not open a MagicAAP interface.{Environment.NewLine}{suffix}");
    }

    public static bool TryOpenForQuery(string path, out string error)
    {
        using var handle = Kernel32.OpenDevice(path, desiredAccess: 0, overlapped: false);
        if (!handle.IsInvalid)
        {
            error = "";
            return true;
        }

        error = Kernel32.LastWin32ErrorText();
        return false;
    }

    private static MagicAapDevice OpenCandidate(DeviceInterfaceCandidate candidate, bool syncIo)
    {
        var handle = Kernel32.OpenDevice(
            candidate.Path,
            Kernel32.GenericRead | Kernel32.GenericWrite,
            overlapped: !syncIo);

        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateFile failed for {candidate.Kind} interface");
        }

        return new MagicAapDevice(candidate, handle, syncIo);
    }

    public async Task<int> ReadAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        if (_syncIo)
        {
            return await SynchronousDeviceIo.ReadAsync(_handle, buffer, cancellationToken);
        }

        try
        {
            return await _stream!.ReadAsync(buffer, cancellationToken);
        }
        catch (IOException ex)
        {
            throw new IOException($"MagicAAP read failed: {ex.Message}", ex);
        }
    }

    public async Task WritePacketAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (_syncIo)
        {
            SynchronousDeviceIo.Write(_handle, packet);
            return;
        }

        try
        {
            await _stream!.WriteAsync(packet, cancellationToken);
            await _stream.FlushAsync(cancellationToken);
        }
        catch (IOException ex)
        {
            throw new IOException($"MagicAAP write failed: {ex.Message}", ex);
        }
    }

    public void Dispose()
    {
        if (_stream is not null)
        {
            _stream.Dispose();
        }
        else
        {
            _handle.Dispose();
        }
    }
}

internal static class SynchronousDeviceIo
{
    public static async Task<int> ReadAsync(SafeFileHandle handle, byte[] buffer, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new ManualResetEventSlim(false);
        var threadHandle = IntPtr.Zero;

        var thread = new Thread(() =>
        {
            try
            {
                if (!Kernel32.DuplicateCurrentThread(out threadHandle, out var duplicateError))
                {
                    completion.TrySetException(new Win32Exception(duplicateError, "Could not duplicate synchronous reader thread handle"));
                    return;
                }

                ready.Set();
                if (Kernel32.ReadFile(handle, buffer, (uint)buffer.Length, out var bytesRead, IntPtr.Zero))
                {
                    completion.TrySetResult(checked((int)bytesRead));
                    return;
                }

                var error = Marshal.GetLastWin32Error();
                if (error == Kernel32.ErrorOperationAborted && cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }

                completion.TrySetException(new IOException($"MagicAAP synchronous read failed: {Kernel32.FormatWin32Error(error)}"));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
            finally
            {
                if (threadHandle != IntPtr.Zero)
                {
                    Kernel32.CloseHandle(threadHandle);
                }

                ready.Set();
            }
        })
        {
            IsBackground = true,
            Name = "MagicAAP sync reader"
        };

        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(1));

        using var registration = cancellationToken.Register(() =>
        {
            var handleToCancel = threadHandle;
            if (handleToCancel != IntPtr.Zero)
            {
                Kernel32.CancelSynchronousIo(handleToCancel);
            }
        });

        return await completion.Task;
    }

    public static void Write(SafeFileHandle handle, byte[] packet)
    {
        if (!Kernel32.WriteFile(handle, packet, (uint)packet.Length, out var bytesWritten, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            throw new IOException($"MagicAAP synchronous write failed: {Kernel32.FormatWin32Error(error)}");
        }

        if (bytesWritten != packet.Length)
        {
            throw new IOException($"MagicAAP synchronous write wrote {bytesWritten} of {packet.Length} bytes.");
        }
    }
}

internal static class ConfigurationManagerNative
{
    private const int CR_SUCCESS = 0;
    private const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0x00000000;

    public static IEnumerable<string> GetPresentDeviceInterfacePaths(Guid interfaceClassGuid)
    {
        var guid = interfaceClassGuid;
        var result = CM_Get_Device_Interface_List_SizeW(
            out var length,
            ref guid,
            null,
            CM_GET_DEVICE_INTERFACE_LIST_PRESENT);

        if (result != CR_SUCCESS || length <= 1)
        {
            yield break;
        }

        var buffer = new char[length];
        result = CM_Get_Device_Interface_ListW(
            ref guid,
            null,
            buffer,
            length,
            CM_GET_DEVICE_INTERFACE_LIST_PRESENT);

        if (result != CR_SUCCESS)
        {
            yield break;
        }

        var start = 0;
        for (var i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] != '\0')
            {
                continue;
            }

            if (i == start)
            {
                break;
            }

            yield return new string(buffer, start, i - start);
            start = i + 1;
        }
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List_SizeW(
        out uint pulLen,
        ref Guid interfaceClassGuid,
        string? pDeviceID,
        uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_ListW(
        ref Guid interfaceClassGuid,
        string? pDeviceID,
        [Out] char[] buffer,
        uint bufferLen,
        uint ulFlags);
}

internal static class Kernel32
{
    public const int ErrorOperationAborted = 995;
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;

    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint DuplicateSameAccess = 0x00000002;

    public static SafeFileHandle OpenDevice(string path, uint desiredAccess, bool overlapped)
    {
        var flags = FileAttributeNormal | (overlapped ? FileFlagOverlapped : 0);
        return CreateFileW(
            path,
            desiredAccess,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            flags,
            IntPtr.Zero);
    }

    public static bool DuplicateCurrentThread(out IntPtr duplicatedThreadHandle, out int error)
    {
        var process = GetCurrentProcess();
        var currentThread = GetCurrentThread();
        var ok = DuplicateHandle(
            process,
            currentThread,
            process,
            out duplicatedThreadHandle,
            0,
            false,
            DuplicateSameAccess);

        error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    public static string LastWin32ErrorText()
    {
        var error = Marshal.GetLastWin32Error();
        return FormatWin32Error(error);
    }

    public static string FormatWin32Error(int error) => $"{error} ({new Win32Exception(error).Message})";

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(
        SafeFileHandle hFile,
        byte[] lpBuffer,
        uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteFile(
        SafeFileHandle hFile,
        byte[] lpBuffer,
        uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CancelSynchronousIo(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(
        IntPtr hSourceProcessHandle,
        IntPtr hSourceHandle,
        IntPtr hTargetProcessHandle,
        out IntPtr lpTargetHandle,
        uint dwDesiredAccess,
        bool bInheritHandle,
        uint dwOptions);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);
}

internal static class MagicPodsAppService
{
    public const string PackageFamilyName = "715AleksandrMaslov.MagicPods_vk1h99z2mpttm";
    public const string ServiceName = "SystrayExtensionService";

    public static readonly IReadOnlyList<string> DefaultProbeCommands =
    [
        "GetHeadphones",
        "GetBluetoothState",
        "IsHeadphonesWatcherEnumerationCompleted",
        "GetProtocolSettings",
        "GetAirohaCompatibilityAndSettings",
        "GetSonySettings",
        "GetLicenseState"
    ];

    public static ValueSet CreateCommandMessage(string command)
    {
        var message = new ValueSet
        {
            [command] = command
        };

        return message;
    }

    public static void PrintValueSet(ValueSet? valueSet, string indent)
    {
        if (valueSet is null)
        {
            Console.WriteLine($"{indent}<null>");
            return;
        }

        if (valueSet.Count == 0)
        {
            Console.WriteLine($"{indent}<empty>");
            return;
        }

        foreach (var pair in valueSet)
        {
            PrintValue(pair.Key, pair.Value, indent);
        }
    }

    private static void PrintValue(string key, object? value, string indent)
    {
        switch (value)
        {
            case null:
                Console.WriteLine($"{indent}{key}: <null>");
                break;
            case ValueSet nested:
                Console.WriteLine($"{indent}{key}: ValueSet");
                PrintValueSet(nested, indent + "  ");
                break;
            case byte[] bytes:
                Console.WriteLine($"{indent}{key}: byte[{bytes.Length}] {Hex.Preview(bytes)}");
                break;
            default:
                Console.WriteLine($"{indent}{key}: {value} ({value.GetType().FullName})");
                break;
        }
    }
}

internal static class AapPackets
{
    private static readonly byte[] Prefix = [0x04, 0x00, 0x04, 0x00];

    public sealed record NamedPacket(string Label, byte[] Bytes);

    private static readonly byte[] AapHandshake =
    [
        0x00, 0x00, 0x04, 0x00, 0x01, 0x00, 0x02, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    ];

    private static readonly byte[] RtBuddyConnectService0 =
    [
        0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x03, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    ];

    private static readonly byte[] RtBuddyCapabilitiesService0 =
    [
        0x04, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00
    ];

    private static readonly byte[] RtBuddyConnectService4 =
    [
        0x00, 0x00, 0x04, 0x00, 0x01, 0x00, 0x03, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    ];

    private static readonly byte[] RtBuddyCapabilitiesService4 =
    [
        0x04, 0x00, 0x04, 0x00, 0x01, 0x00, 0x00
    ];

    private static readonly byte[] MagicPodsNotificationAll =
    [
        0x04, 0x00, 0x04, 0x00, 0x0F, 0x00, 0xFF, 0xFF,
        0xFF, 0xFF
    ];

    private static readonly byte[] LibrePodsHeadTrackingNotification =
    [
        0x04, 0x00, 0x04, 0x00, 0x0F, 0x00, 0xFF, 0xFF,
        0xFE, 0xFF
    ];

    private static readonly byte[] MagicPodsFeatureQuery =
    [
        0x04, 0x00, 0x04, 0x00, 0x4D, 0x00, 0x0E, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    ];

    public static readonly IReadOnlyList<NamedPacket> MagicPodsAirPodsInitPackets =
    [
        new("MagicPods handshake", AapHandshake),
        new("MagicPods notification-all", MagicPodsNotificationAll),
        new("MagicPods feature-query", MagicPodsFeatureQuery)
    ];

    public static readonly IReadOnlyList<NamedPacket> LibrePodsHeadTrackingInitPackets =
    [
        new("LibrePods handshake", AapHandshake),
        new("LibrePods head-notify", LibrePodsHeadTrackingNotification)
    ];

    public static readonly IReadOnlyList<NamedPacket> RtBuddyInitPackets =
    [
        new("RTBuddy connect service 0 v1.3", RtBuddyConnectService0),
        new("RTBuddy capabilities service 0", RtBuddyCapabilitiesService0),
        new("RTBuddy connect service 4 v1.3", RtBuddyConnectService4),
        new("RTBuddy capabilities service 4", RtBuddyCapabilitiesService4)
    ];

    public static IEnumerable<NamedPacket> BuildInitPackets(InitPacketFormat format)
    {
        if (format is InitPacketFormat.RtBuddy or InitPacketFormat.RtBuddyAacp)
        {
            foreach (var packet in RtBuddyInitPackets)
            {
                yield return packet;
            }

            if (format is InitPacketFormat.RtBuddyAacp)
            {
                yield return new NamedPacket("AACP handshake", AapHandshake);
                yield return new NamedPacket("AACP feature flags", Convert.FromHexString("040004004D00D700000000000000"));
                yield return new NamedPacket("AACP notifications", MagicPodsNotificationAll);
            }
            yield break;
        }

        if (format is InitPacketFormat.MagicPods or InitPacketFormat.Both)
        {
            foreach (var packet in MagicPodsAirPodsInitPackets)
            {
                yield return packet;
            }
        }

        if (format is InitPacketFormat.LibrePods)
        {
            foreach (var packet in LibrePodsHeadTrackingInitPackets)
            {
                yield return packet;
            }
        }
        else if (format is InitPacketFormat.Both)
        {
            yield return new NamedPacket("LibrePods head-notify", LibrePodsHeadTrackingNotification);
        }
    }

    public static IEnumerable<NamedPacket> BuildStartPackets(StartPacketFormat format, byte magicPodsMode)
    {
        if (format is StartPacketFormat.MagicPods or StartPacketFormat.Both)
        {
            yield return new NamedPacket($"MagicPods head-mode {magicPodsMode}", MagicPodsHeadTracking(magicPodsMode));
        }

        if (format is StartPacketFormat.Legacy or StartPacketFormat.Both)
        {
            yield return new NamedPacket("legacy AAP 0x17", LegacyStartHeadTracking);
        }

        if (format is StartPacketFormat.Alternate)
        {
            yield return new NamedPacket("alternate AAP 0x17", AlternateStartHeadTracking);
        }

        if (format is StartPacketFormat.AirPods5)
        {
            var packet = (byte[])AlternateStartHeadTracking.Clone();
            // CMA_COMMAND (79), the service used for AirPods 5 / AirPods 5 WCC in PodsOnDroid.
            packet[17] = 0x4F;
            yield return new NamedPacket("AirPods 5 CMA_COMMAND", packet);
        }

        if (format is StartPacketFormat.DevMotion6)
        {
            yield return new NamedPacket("RTBuddy DEVMOTION6 50 Hz", DevMotion6StartHeadTracking);
        }

        if (format is StartPacketFormat.Max2)
        {
            yield return new NamedPacket("RTBuddy Max 2 DEVMOTION6 service 6 at 50 Hz", Max2StartHeadTracking);
        }
    }

    public static IEnumerable<NamedPacket> BuildStopPackets(StartPacketFormat format, byte magicPodsMode)
    {
        if (format is StartPacketFormat.MagicPods or StartPacketFormat.Both)
        {
            yield return new NamedPacket($"MagicPods head-mode {magicPodsMode}", MagicPodsHeadTracking(magicPodsMode));
        }

        if (format is StartPacketFormat.Legacy or StartPacketFormat.Both)
        {
            yield return new NamedPacket("legacy AAP 0x17", LegacyStopHeadTracking);
        }

        if (format is StartPacketFormat.Alternate or StartPacketFormat.AirPods5)
        {
            yield return new NamedPacket("alternate AAP 0x17", AlternateStopHeadTracking);
        }

        if (format is StartPacketFormat.DevMotion6)
        {
            yield return new NamedPacket("RTBuddy DEVMOTION6", DevMotion6StopHeadTracking);
        }

        if (format is StartPacketFormat.Max2)
        {
            yield return new NamedPacket("RTBuddy Max 2 DEVMOTION6 service 6", Max2StopHeadTracking);
        }
    }

    public static IEnumerable<NamedPacket> BuildTakeoverPackets(string? selfMac, IReadOnlyList<string> targetMacs)
    {
        yield return new NamedPacket("ownership/takeover control", OwnsConnectionControl);

        if (selfMac is null || targetMacs.Count == 0)
        {
            yield break;
        }

        var firstTarget = targetMacs[0];
        yield return new NamedPacket($"smart-routing media-info -> {firstTarget}", CreateMediaInformationPacket(selfMac, firstTarget));
        yield return new NamedPacket($"smart-routing show-ui -> {firstTarget}", CreateSmartRoutingShowUiPacket(firstTarget));

        foreach (var targetMac in targetMacs)
        {
            yield return new NamedPacket($"smart-routing hijack -> {targetMac}", CreateHijackRequestPacket(targetMac));
        }
    }

    public static byte[] MagicPodsHeadTracking(byte mode) =>
    [
        // MagicPodsService FlyccSetHeadTracking:
        // type=0x5A, command=0x2100, payload=0F 14 00 00 <mode> 00.
        0x5F, 0x5A, 0x08, 0x00, 0x00, 0x21,
        0x0F, 0x14, 0x00, 0x00, mode, 0x00
    ];

    public static readonly byte[] LegacyStartHeadTracking =
    [
        0x04, 0x00, 0x04, 0x00, 0x17, 0x00, 0x00, 0x00,
        0x10, 0x00, 0x10, 0x00, 0x08, 0xA1, 0x02, 0x42,
        0x0B, 0x08, 0x0E, 0x10, 0x02, 0x1A, 0x05, 0x01,
        0x40, 0x9C, 0x00, 0x00
    ];

    public static readonly byte[] LegacyStopHeadTracking =
    [
        0x04, 0x00, 0x04, 0x00, 0x17, 0x00, 0x00, 0x00,
        0x10, 0x00, 0x11, 0x00, 0x08, 0x7E, 0x10, 0x02,
        0x42, 0x0B, 0x08, 0x4E, 0x10, 0x02, 0x1A, 0x05,
        0x01, 0x00, 0x00, 0x00, 0x00
    ];

    public static readonly byte[] AlternateStartHeadTracking =
    [
        0x04, 0x00, 0x04, 0x00, 0x17, 0x00, 0x00, 0x00,
        0x10, 0x00, 0x0F, 0x00, 0x08, 0x73, 0x42, 0x0B,
        0x08, 0x10, 0x10, 0x02, 0x1A, 0x05, 0x01, 0x40,
        0x9C, 0x00, 0x00
    ];

    public static readonly byte[] AlternateStopHeadTracking =
    [
        0x04, 0x00, 0x04, 0x00, 0x17, 0x00, 0x00, 0x00,
        0x10, 0x00, 0x0F, 0x00, 0x08, 0x75, 0x42, 0x0B,
        0x08, 0x10, 0x10, 0x02, 0x1A, 0x05, 0x01, 0x00,
        0x00, 0x00, 0x00
    ];

    public static readonly byte[] DevMotion6StartHeadTracking =
    [
        // SensorDataWX(seq=0x93), ServiceSetting(DEVMOTION6=16, mode=2, interval=20,000 us).
        0x04, 0x00, 0x04, 0x00, 0x17, 0x00, 0x00, 0x00,
        0x10, 0x00, 0x10, 0x00, 0x08, 0x93, 0x01, 0x42,
        0x0B, 0x08, 0x10, 0x10, 0x02, 0x1A, 0x05, 0x01,
        0x20, 0x4E, 0x00, 0x00
    ];

    public static readonly byte[] DevMotion6StopHeadTracking =
    [
        // Sequence values are request identifiers; 0x95 is the next unused local request id.
        0x04, 0x00, 0x04, 0x00, 0x17, 0x00, 0x00, 0x00,
        0x10, 0x00, 0x10, 0x00, 0x08, 0x95, 0x01, 0x42,
        0x0B, 0x08, 0x10, 0x10, 0x02, 0x1A, 0x05, 0x01,
        0x00, 0x00, 0x00, 0x00
    ];

    public static readonly byte[] Max2StartHeadTracking =
    [
        // AirPods Max 2 on firmware 8E258 advertises AccessoryService=devmotion6 as service 6.
        0x04, 0x00, 0x04, 0x00, 0x17, 0x00, 0x00, 0x00,
        0x10, 0x00, 0x10, 0x00, 0x08, 0x98, 0x01, 0x42,
        0x0B, 0x08, 0x06, 0x10, 0x02, 0x1A, 0x05, 0x01,
        0x20, 0x4E, 0x00, 0x00
    ];

    public static readonly byte[] Max2StopHeadTracking =
    [
        0x04, 0x00, 0x04, 0x00, 0x17, 0x00, 0x00, 0x00,
        0x10, 0x00, 0x10, 0x00, 0x08, 0x9A, 0x01, 0x42,
        0x0B, 0x08, 0x06, 0x10, 0x02, 0x1A, 0x05, 0x01,
        0x00, 0x00, 0x00, 0x00
    ];

    public static readonly byte[] OwnsConnectionControl =
    [
        // AACP control command 0x09, identifier 0x06 (OWNS_CONNECTION), value 0x01.
        0x04, 0x00, 0x04, 0x00, 0x09, 0x00, 0x06, 0x01,
        0x00, 0x00, 0x00
    ];

    public static bool HasAapPrefix(ReadOnlySpan<byte> data) =>
        data.Length >= Prefix.Length && data[..Prefix.Length].SequenceEqual(Prefix);

    private static byte[] CreateMediaInformationPacket(string selfMac, string targetMac) =>
        CreateSmartRoutingPacket(targetMac, bufferSize: 138, buffer =>
        {
            buffer.AddRange([0x82, 0x00, 0x01, 0xE5, 0x4A]);
            AddAscii(buffer, "PlayingApp");
            buffer.Add(0x56);
            AddAscii(buffer, "com.google.ios.youtube");
            buffer.Add(0x52);
            AddAscii(buffer, "HostStreamingState");
            buffer.Add(0x42);
            AddAscii(buffer, "NO");
            buffer.Add(0x49);
            AddAscii(buffer, "btAddress");
            buffer.Add(0x51);
            AddAscii(buffer, selfMac);
            AddAscii(buffer, "btName");
            buffer.Add(0x47);
            AddAscii(buffer, "Android");
            buffer.Add(0x58);
            AddAscii(buffer, "otherDevice");
            AddAscii(buffer, "AudioCategory");
            buffer.AddRange([0x31, 0x2D, 0x01]);
        });

    private static byte[] CreateSmartRoutingShowUiPacket(string targetMac) =>
        CreateSmartRoutingPacket(targetMac, bufferSize: 134, buffer =>
        {
            buffer.AddRange([0x7E, 0x00, 0x01, 0xE6, 0x5B]);
            AddAscii(buffer, "SmartRoutingKeyShowNearbyUI");
            buffer.AddRange([0x01, 0x4A]);
            AddAscii(buffer, "localscore");
            buffer.AddRange([0x31, 0x2D, 0x01, 0x46]);
            AddAscii(buffer, "reasonHhijackv2");
            buffer.Add(0x51);
            AddAscii(buffer, "audioRoutingScore");
            buffer.AddRange([0xA2, 0x5F]);
            AddAscii(buffer, "audioRoutingSetOwnershipToFalse");
            buffer.AddRange([0x01, 0x4B]);
            AddAscii(buffer, "remotescore");
            buffer.Add(0xA2);
        });

    private static byte[] CreateHijackRequestPacket(string targetMac) =>
        CreateSmartRoutingPacket(targetMac, bufferSize: 106, buffer =>
        {
            buffer.AddRange([0x62, 0x00, 0x01, 0xE5, 0x4A]);
            AddAscii(buffer, "localscore");
            buffer.AddRange([0x30, 0x64, 0x46]);
            AddAscii(buffer, "reason");
            buffer.Add(0x48);
            AddAscii(buffer, "Hijackv2");
            buffer.Add(0x51);
            AddAscii(buffer, "audioRoutingScore");
            buffer.AddRange([0x31, 0x2D, 0x01, 0x5F]);
            AddAscii(buffer, "audioRoutingSetOwnershipToFalse");
            buffer.AddRange([0x01, 0x4B]);
            AddAscii(buffer, "remotescore");
            buffer.Add(0xA5);
        });

    private static byte[] CreateSmartRoutingPacket(string targetMac, int bufferSize, Action<List<byte>> fill)
    {
        var buffer = new List<byte>(bufferSize);
        buffer.AddRange(MacAddress.ParseBytes(targetMac).Reverse());
        fill(buffer);

        if (buffer.Count > bufferSize)
        {
            throw new InvalidOperationException($"Smart-routing payload for {targetMac} is {buffer.Count} bytes, expected at most {bufferSize}.");
        }

        while (buffer.Count < bufferSize)
        {
            buffer.Add(0x00);
        }

        var payload = new List<byte>(2 + buffer.Count) { 0x10, 0x00 };
        payload.AddRange(buffer);
        return WrapDataPacket(payload);
    }

    private static byte[] WrapDataPacket(IReadOnlyList<byte> payload)
    {
        var packet = new byte[Prefix.Length + payload.Count];
        Prefix.CopyTo(packet, 0);
        for (var i = 0; i < payload.Count; i++)
        {
            packet[Prefix.Length + i] = payload[i];
        }

        return packet;
    }

    private static void AddAscii(List<byte> buffer, string text) =>
        buffer.AddRange(System.Text.Encoding.ASCII.GetBytes(text));
}

internal sealed class AapSessionState
{
    private readonly List<string> _connectedDeviceMacs = [];

    public IReadOnlyList<string> ConnectedDeviceMacs => _connectedDeviceMacs;
    public string? AudioSourceMac { get; private set; }
    public bool? OwnsConnection { get; private set; }

    public void Observe(ReadOnlySpan<byte> packet)
    {
        if (!AapPackets.HasAapPrefix(packet) || packet.Length < 6)
        {
            return;
        }

        switch (packet[4])
        {
            case 0x09:
                ObserveControlCommand(packet);
                break;
            case 0x0E:
                ObserveAudioSource(packet);
                break;
            case 0x2E:
                ObserveConnectedDevices(packet);
                break;
        }
    }

    private void ObserveControlCommand(ReadOnlySpan<byte> packet)
    {
        if (packet.Length >= 8 && packet[6] == 0x06)
        {
            OwnsConnection = packet[7] == 0x01;
        }
    }

    private void ObserveAudioSource(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 13)
        {
            return;
        }

        Span<byte> mac = stackalloc byte[6];
        packet.Slice(6, 6).CopyTo(mac);
        mac.Reverse();
        AudioSourceMac = MacAddress.Format(mac);
    }

    private void ObserveConnectedDevices(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 9)
        {
            return;
        }

        var count = packet[8];
        var offset = 9;
        var parsed = new List<string>();
        for (var i = 0; i < count && offset + 8 <= packet.Length; i++, offset += 8)
        {
            parsed.Add(MacAddress.Format(packet.Slice(offset, 6)));
        }

        if (parsed.Count == 0)
        {
            return;
        }

        _connectedDeviceMacs.Clear();
        _connectedDeviceMacs.AddRange(parsed);
    }
}

internal static class BluetoothMacResolver
{
    public static string? TryFindLocalBluetoothMac()
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!adapter.Name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
                && !adapter.Description.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var bytes = adapter.GetPhysicalAddress().GetAddressBytes();
            if (bytes.Length == 6)
            {
                return MacAddress.Format(bytes);
            }
        }

        return null;
    }
}

internal static class MacAddress
{
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var hex = new string(text.Where(IsHex).ToArray()).ToUpperInvariant();
        if (hex.Length != 12)
        {
            throw new ArgumentException($"Invalid Bluetooth MAC address '{text}'. Expected AA:BB:CC:DD:EE:FF.");
        }

        return string.Join(':', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }

    public static byte[] ParseBytes(string text)
    {
        var normalized = Normalize(text) ?? throw new ArgumentException("MAC address is required.");
        return normalized
            .Split(':')
            .Select(part => byte.Parse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
            .ToArray();
    }

    public static string Format(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 6)
        {
            throw new ArgumentException("A Bluetooth MAC address must contain exactly 6 bytes.");
        }

        return string.Join(':', bytes.ToArray().Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
    }

    private static bool IsHex(char c) =>
        c is >= '0' and <= '9'
        || c is >= 'a' and <= 'f'
        || c is >= 'A' and <= 'F';
}

internal sealed class AapStreamFramer
{
    private static readonly byte[] Prefix = [0x04, 0x00, 0x04, 0x00];
    private readonly List<byte> _pending = [];

    public IEnumerable<byte[]> Push(byte[] chunk)
    {
        foreach (var b in chunk)
        {
            _pending.Add(b);
        }

        while (_pending.Count > 0)
        {
            var data = _pending.ToArray();
            var firstPrefix = FindPrefix(data, 0);
            if (firstPrefix < 0)
            {
                var suffix = LongestPrefixSuffix(data);
                var rawLength = data.Length - suffix;
                if (rawLength <= 0)
                {
                    yield break;
                }

                var raw = data[..rawLength];
                _pending.RemoveRange(0, rawLength);
                yield return raw;
                continue;
            }

            if (firstPrefix > 0)
            {
                var raw = data[..firstPrefix];
                _pending.RemoveRange(0, firstPrefix);
                yield return raw;
                continue;
            }

            var nextPrefix = FindPrefix(data, 1);
            if (data.Length >= 12 && data[4] == 0x17 && data[5] == 0 &&
                data[6] == 0 && data[7] == 0 && data[8] == 0x10 && data[9] == 0)
            {
                var frameLength = 12 + BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(10, 2));
                if (frameLength <= 4096)
                {
                    if (data.Length < frameLength) yield break;
                    var frame = data[..frameLength];
                    _pending.RemoveRange(0, frameLength);
                    yield return frame;
                    continue;
                }
            }

            if (nextPrefix < 0)
            {
                yield break;
            }

            var packet = data[..nextPrefix];
            _pending.RemoveRange(0, nextPrefix);
            yield return packet;
        }
    }

    public IEnumerable<byte[]> Complete()
    {
        if (_pending.Count == 0)
        {
            yield break;
        }

        var packet = _pending.ToArray();
        _pending.Clear();
        yield return packet;
    }

    private static int FindPrefix(byte[] data, int start)
    {
        for (var i = start; i <= data.Length - Prefix.Length; i++)
        {
            if (data.AsSpan(i, Prefix.Length).SequenceEqual(Prefix))
            {
                return i;
            }
        }

        return -1;
    }

    private static int LongestPrefixSuffix(byte[] data)
    {
        var max = Math.Min(Prefix.Length - 1, data.Length);
        for (var length = max; length > 0; length--)
        {
            if (data.AsSpan(data.Length - length, length).SequenceEqual(Prefix.AsSpan(0, length)))
            {
                return length;
            }
        }

        return 0;
    }
}

internal readonly record struct PacketReadSummary(int Chunks, int Packets, int HeadPackets);

internal readonly record struct HeadTrackingSample(
    double Yaw,
    double Pitch,
    double Roll,
    short RawO1,
    short RawO2,
    short RawO3,
    double RawQuaternionW,
    short HorizontalAcceleration,
    short VerticalAcceleration);

internal sealed class HeadTrackingDecoder
{
    public const int CalibrationSampleCount = 25;

    private const double QuaternionScale = 32768.0;
    private readonly List<MotionQuaternion> _calibration = [];
    private static readonly MotionQuaternion Pro2Frame = MotionQuaternion.Normalize(
        new MotionQuaternion(-0.556423, 0.723507, -0.322324, -0.251074));
    private readonly AirPods5Pose _relativeAxes = new();
    private readonly bool _relativeAxesOnly;
    private readonly bool _allowRelativeAxes;
    private readonly bool _correctPro2Frame;
    private bool? _useRelativeAxes;
    private MotionQuaternion _neutral;

    public HeadTrackingDecoder(StartPacketFormat format, string? model = null)
    {
        _relativeAxesOnly = format == StartPacketFormat.AirPods5;
        _allowRelativeAxes = format is StartPacketFormat.AirPods5 or StartPacketFormat.Alternate
            or StartPacketFormat.Legacy or StartPacketFormat.Both;
        _correctPro2Frame = model == "pro2";
    }

    public int CalibrationCount => _useRelativeAxes == true ? _relativeAxes.CalibrationCount : _calibration.Count;
    public int RequiredSamples => _useRelativeAxes == true ? AirPods5Pose.RequiredSamples : CalibrationSampleCount;
    private bool IsCalibrated { get; set; }

    public bool IsHeadTrackingPacket(ReadOnlySpan<byte> data) =>
        (_allowRelativeAxes && _useRelativeAxes != false && AirPods5Pose.IsMotionReport(data))
        || (!_relativeAxesOnly && _useRelativeAxes != true && TryGetQuaternionPayload(data, out _));

    public HeadTrackingSample? Process(ReadOnlySpan<byte> packet)
    {
        if (_allowRelativeAxes && _useRelativeAxes != false && AirPods5Pose.IsMotionReport(packet))
        {
            _useRelativeAxes = true;
            return _relativeAxes.Process(packet);
        }

        if (_relativeAxesOnly || _useRelativeAxes == true || !TryGetQuaternionPayload(packet, out var payload))
            return null;

        var o1 = BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(20, 2));
        var o2 = BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(22, 2));
        var o3 = BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(24, 2));
        var vectorLengthSquared =
            ((double)o1 * o1 + (double)o2 * o2 + (double)o3 * o3)
            / (QuaternionScale * QuaternionScale);
        if (vectorLengthSquared < 0.01 || vectorLengthSquared > 1.01)
        {
            return null;
        }

        _useRelativeAxes = false;
        var quaternion = MotionQuaternion.Normalize(new MotionQuaternion(
            o1 / QuaternionScale,
            o2 / QuaternionScale,
            o3 / QuaternionScale,
            Math.Sqrt(Math.Max(0.0, 1.0 - vectorLengthSquared))));

        if (_correctPro2Frame)
            quaternion = MotionQuaternion.Multiply(MotionQuaternion.Multiply(Pro2Frame.Conjugate(), quaternion), Pro2Frame);

        if (!IsCalibrated)
        {
            _calibration.Add(quaternion);
            if (_calibration.Count >= CalibrationSampleCount)
            {
                _neutral = Average(_calibration);
                IsCalibrated = true;
            }

            return null;
        }

        var relative = MotionQuaternion.Normalize(MotionQuaternion.Multiply(_neutral.Conjugate(), quaternion));
        if (relative.W < 0)
        {
            relative = relative.Negated();
        }

        var (yaw, pitch, roll) = ToYawPitchRoll(relative);
        // HeadTracker routes the corrected Pro 2 sensor's X rotation to physical pitch.
        if (_correctPro2Frame) (pitch, roll) = (roll, pitch);
        var horizontalAcceleration = BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(28, 2));
        var verticalAcceleration = BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(30, 2));

        return new HeadTrackingSample(
            Yaw: yaw,
            Pitch: pitch,
            Roll: roll,
            RawO1: o1,
            RawO2: o2,
            RawO3: o3,
            RawQuaternionW: quaternion.W,
            HorizontalAcceleration: horizontalAcceleration,
            VerticalAcceleration: verticalAcceleration);
    }

    private static bool TryGetQuaternionPayload(ReadOnlySpan<byte> packet, out ReadOnlySpan<byte> payload)
    {
        if (RtBuddyFrameInfo.TryGetCommandPayload(packet, out var service, out payload)
            && service is 6 or 14 or 16 && IsQuaternionReport(payload))
            return true;

        if (packet.Length < 12 || !AapPackets.HasAapPrefix(packet) || packet[4] != 0x17 || packet[5] != 0
            || BinaryPrimitives.ReadUInt32LittleEndian(packet[6..]) != 0x00100000)
            return false;

        int declaredLength = BinaryPrimitives.ReadUInt16LittleEndian(packet[10..]);
        if (declaredLength > packet.Length - 12) return false;

        // RTBuddy also sends quaternion data in a sensor-stream message (field 3),
        // which HeadTracker uses with AirPods Pro 2.
        var reader = new ProtobufReader(packet.Slice(12, declaredLength));
        while (reader.TryRead(out var field))
        {
            if (field.Number != 3 || field.WireType != 2) continue;
            var stream = new ProtobufReader(field.Bytes);
            while (stream.TryRead(out var inner))
            {
                if (inner.Number == 3 && inner.WireType == 2 && IsQuaternionReport(inner.Bytes))
                {
                    payload = inner.Bytes;
                    return true;
                }
            }
        }

        payload = default;
        return false;
    }

    private static bool IsQuaternionReport(ReadOnlySpan<byte> payload) =>
        payload.Length >= 58 && payload[0] == 1 && payload[9] == 3;

    private static MotionQuaternion Average(IReadOnlyList<MotionQuaternion> samples)
    {
        var reference = samples[0];
        var x = 0.0;
        var y = 0.0;
        var z = 0.0;
        var w = 0.0;
        foreach (var original in samples)
        {
            var sample = MotionQuaternion.Dot(reference, original) < 0
                ? original.Negated()
                : original;
            x += sample.X;
            y += sample.Y;
            z += sample.Z;
            w += sample.W;
        }

        return MotionQuaternion.Normalize(new MotionQuaternion(x, y, z, w));
    }

    private static (double Yaw, double Pitch, double Roll) ToYawPitchRoll(MotionQuaternion q)
    {
        // Y-up Y-X-Z decomposition. On AirPods Max, the X term is the physical
        // roll axis and the Z term is the physical pitch axis.
        var r02 = 2.0 * (q.X * q.Z + q.Y * q.W);
        var r22 = 1.0 - 2.0 * (q.X * q.X + q.Y * q.Y);
        var r12 = 2.0 * (q.Y * q.Z - q.X * q.W);
        var r10 = 2.0 * (q.X * q.Y + q.Z * q.W);
        var r11 = 1.0 - 2.0 * (q.X * q.X + q.Z * q.Z);

        var yaw = Math.Atan2(r02, r22);
        var roll = Math.Asin(Math.Clamp(-r12, -1.0, 1.0));
        var pitch = Math.Atan2(r10, r11);
        const double radiansToDegrees = 180.0 / Math.PI;
        return (-yaw * radiansToDegrees, -pitch * radiansToDegrees, roll * radiansToDegrees);
    }

    private readonly record struct MotionQuaternion(double X, double Y, double Z, double W)
    {
        public MotionQuaternion Conjugate() => new(-X, -Y, -Z, W);

        public MotionQuaternion Negated() => new(-X, -Y, -Z, -W);

        public static double Dot(MotionQuaternion left, MotionQuaternion right) =>
            left.X * right.X + left.Y * right.Y + left.Z * right.Z + left.W * right.W;

        public static MotionQuaternion Multiply(MotionQuaternion left, MotionQuaternion right) =>
            new(
                left.W * right.X + left.X * right.W + left.Y * right.Z - left.Z * right.Y,
                left.W * right.Y - left.X * right.Z + left.Y * right.W + left.Z * right.X,
                left.W * right.Z + left.X * right.Y - left.Y * right.X + left.Z * right.W,
                left.W * right.W - left.X * right.X - left.Y * right.Y - left.Z * right.Z);

        public static MotionQuaternion Normalize(MotionQuaternion value)
        {
            var length = Math.Sqrt(Dot(value, value));
            return length <= double.Epsilon
                ? new MotionQuaternion(0, 0, 0, 1)
                : new MotionQuaternion(
                    value.X / length,
                    value.Y / length,
                    value.Z / length,
                    value.W / length);
        }
    }
}

internal sealed class OpenTrackUdpSender : IDisposable
{
    private readonly UdpClient _client = new();
    private readonly IPEndPoint _target;

    public OpenTrackUdpSender(string host, int port)
    {
        _target = new IPEndPoint(IPAddress.Parse(host), port);
    }

    public void Send(HeadTrackingSample sample)
    {
        Span<byte> payload = stackalloc byte[48];
        WriteDouble(payload, 0, 0.0);
        WriteDouble(payload, 8, 0.0);
        WriteDouble(payload, 16, 0.0);
        WriteDouble(payload, 24, sample.Yaw);
        WriteDouble(payload, 32, sample.Pitch);
        WriteDouble(payload, 40, sample.Roll);
        _client.Send(payload.ToArray(), payload.Length, _target);
    }

    private static void WriteDouble(Span<byte> buffer, int offset, double value)
    {
        if (!BitConverter.TryWriteBytes(buffer.Slice(offset, sizeof(double)), value))
        {
            throw new InvalidOperationException("Could not encode UDP pose.");
        }
    }

    public void Dispose() => _client.Dispose();
}

internal sealed class RawDump : IAsyncDisposable
{
    private readonly StreamWriter? _writer;

    private RawDump(string? path, StreamWriter? writer)
    {
        Path = path;
        _writer = writer;
    }

    public string? Path { get; }

    public static RawDump Open(string? path)
    {
        if (path is null)
        {
            return new RawDump(null, null);
        }

        var fullPath = System.IO.Path.GetFullPath(path);
        var dir = System.IO.Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        return new RawDump(fullPath, new StreamWriter(File.Open(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read)));
    }

    public async Task WriteAsync(byte[] chunk, CancellationToken cancellationToken)
    {
        if (_writer is null)
        {
            return;
        }

        await _writer.WriteLineAsync($"{DateTimeOffset.Now:O} len={chunk.Length} {Hex.Full(chunk)}".AsMemory(), cancellationToken);
        await _writer.FlushAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_writer is not null)
        {
            await _writer.DisposeAsync();
        }
    }
}

internal sealed class ConsoleLifetime : IDisposable
{
    private readonly CancellationTokenSource _source;
    private readonly CancellationTokenRegistration _timerRegistration;
    private readonly ConsoleCancelEventHandler _handler;

    private ConsoleLifetime(CancellationTokenSource source, CancellationTokenRegistration timerRegistration, ConsoleCancelEventHandler handler)
    {
        _source = source;
        _timerRegistration = timerRegistration;
        _handler = handler;
    }

    public CancellationToken Token => _source.Token;

    public static ConsoleLifetime Create(int seconds)
    {
        var source = new CancellationTokenSource();
        if (seconds > 0)
        {
            source.CancelAfter(TimeSpan.FromSeconds(seconds));
        }

        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            source.Cancel();
        };
        Console.CancelKeyPress += handler;

        return new ConsoleLifetime(source, default, handler);
    }

    public void Dispose()
    {
        Console.CancelKeyPress -= _handler;
        _timerRegistration.Dispose();
        _source.Dispose();
    }
}

internal static class Hex
{
    public static byte[] Parse(string text)
    {
        var normalized = text.Replace("0x", "", StringComparison.OrdinalIgnoreCase);
        var compact = new string(normalized
            .Where(character => !char.IsWhiteSpace(character) && character is not '-' and not ':' and not ',' and not '_')
            .ToArray());

        if (compact.Length == 0 || compact.Length % 2 != 0 || compact.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("--send-hex must contain an even number of hexadecimal digits.");
        }

        return Convert.FromHexString(compact);
    }

    public static string Preview(ReadOnlySpan<byte> data)
    {
        const int max = 64;
        var prefix = data.Length > max ? data[..max] : data;
        var suffix = data.Length > max ? " ..." : "";
        return Full(prefix) + suffix;
    }

    public static string Full(ReadOnlySpan<byte> data) =>
        string.Join(' ', data.ToArray().Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
}
