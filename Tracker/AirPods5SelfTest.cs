// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace AapHeadtracker;

internal static class AirPods5SelfTest
{
    public static int Run()
    {
        var start = AapPackets.BuildStartPackets(StartPacketFormat.AirPods5, 2).Single().Bytes;
        var stop = AapPackets.BuildStopPackets(StartPacketFormat.AirPods5, 1).Single().Bytes;
        Require(start.SequenceEqual(Convert.FromHexString("040004001700000010000F000873420B084F10021A0501409C0000")), "AirPods 5 start");
        Require(stop.SequenceEqual(Convert.FromHexString("040004001700000010000F000875420B081010021A050100000000")), "AirPods 5 stop");

        var frame = new byte[80];
        Convert.FromHexString("040004001700000010004400").CopyTo(frame, 0);
        frame[57] = 4;
        frame[59] = 4; // Header-like bytes inside the payload must not split the report.
        var framer = new AapStreamFramer();
        Require(!framer.Push(frame[..43]).Any(), "incomplete frame is retained");
        Require(framer.Push(frame[43..].Concat(frame).ToArray()).Count() == 2, "fragmented and concatenated reports");
        Require(!framer.Complete().Any(), "framer drained");

        var decoder = new HeadTrackingDecoder(StartPacketFormat.AirPods5);
        Require(decoder.Process(frame[..20]) is null, "short acknowledgement is ignored");
        for (var i = 0; i < decoder.RequiredSamples - 1; i++)
            Require(decoder.Process(frame) is null, "neutral calibration waits for enough samples");
        Require(decoder.Process(frame)?.Yaw == 0, "neutral yaw");
        BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(45), 1200);
        BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(47), -800);
        var pose = decoder.Process(frame) ?? throw new InvalidOperationException("missing decoded pose");
        Require(Math.Abs(pose.Yaw - 5.625) < 0.0001 && Math.Abs(pose.Pitch - 1.125) < 0.0001, "relative pose axes");

        CheckModelPresets();
        CheckQuaternionReports();
        using var input = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        input.Client.ReceiveTimeout = 1500;
        using var output = new OpenTrackUdpSender("127.0.0.1", ((IPEndPoint)input.Client.LocalEndPoint!).Port);
        output.Send(pose);
        IPEndPoint? remote = null;
        var data = input.Receive(ref remote);
        Require(data.Length == 48 && BitConverter.ToDouble(data, 24) == pose.Yaw &&
            BitConverter.ToDouble(data, 32) == pose.Pitch, "loopback UDP wire format");
        Console.WriteLine("PASS: AirPods 5 start/stop, report framing, calibration, pose, model presets, quaternion reports and UDP output (synthetic reports).");
        return 0;
    }

    private static void CheckModelPresets()
    {
        var five = Options.Parse(["bridge", "--model", "airpods5"]);
        Require(five.StartFormat == StartPacketFormat.AirPods5 && five.InitFormat == InitPacketFormat.LibrePods
            && five.StartDelayMs == 750 && five.PreStartReadMs == 1000, "AirPods 5 defaults match original launcher");

        var pro2 = Options.Parse(["bridge", "--model", "pro2"]);
        Require(pro2.StartFormat == StartPacketFormat.DevMotion6 && pro2.InitFormat == InitPacketFormat.RtBuddyAacp
            && AapPackets.BuildInitPackets(pro2.InitFormat).Count() == 7, "Pro 2 init and start");
        var max2 = Options.Parse(["bridge", "--model", "max2"]);
        Require(max2.StartFormat == StartPacketFormat.Max2 && max2.InitFormat == InitPacketFormat.RtBuddy, "Max 2 preset");

        foreach (var model in new[] { "airpods3", "airpods4", "pro1", "pro3", "max1" })
        {
            var options = Options.Parse(["bridge", "--model", model]);
            Require(options.StartFormat == StartPacketFormat.Alternate && options.InitFormat == InitPacketFormat.LibrePods,
                model + " preset");
        }
        Require(Options.Parse(["bridge", "--model", "airpods4", "--start-format", "legacy"]).StartFormat
            == StartPacketFormat.Legacy, "explicit protocol override");
    }

    private static void CheckQuaternionReports()
    {
        foreach (var (format, model, envelope, service) in new[]
        {
            (StartPacketFormat.Max2, "max2", (byte)0x3A, (byte)6),
            (StartPacketFormat.DevMotion6, "pro2", (byte)0x1A, (byte)16),
            (StartPacketFormat.Alternate, "airpods4", (byte)0x1A, (byte)16)
        })
        {
            var motionOffset = envelope == 0x3A ? 18 : 16;
            var report = new byte[motionOffset + 58];
            Convert.FromHexString("04000400170000001000").CopyTo(report, 0);
            report[10] = (byte)(report.Length - 12);
            report[12] = envelope;
            report[13] = (byte)(report.Length - 14);
            if (envelope == 0x3A)
            {
                report[14] = 0x08;
                report[15] = service;
            }
            report[motionOffset - 2] = 0x1A;
            report[motionOffset - 1] = 58;
            report[motionOffset] = 1;
            report[motionOffset + 9] = 3;
            BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(motionOffset + 20), 16000);
            var decoder = new HeadTrackingDecoder(format, model);
            Require(decoder.IsHeadTrackingPacket(report), model + " quaternion envelope");
            Require(!decoder.IsHeadTrackingPacket(report[..20]), model + " truncated report");
            for (var i = 0; i < decoder.RequiredSamples; i++)
                Require(decoder.Process(report) is null, model + " quaternion calibration");
            Require(decoder.Process(report) is { } neutral && Math.Abs(neutral.Yaw) < 0.001 &&
                Math.Abs(neutral.Pitch) < 0.001, model + " neutral axes");
            BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(motionOffset + 22), 1600);
            Require(decoder.Process(report) is { } moved && Math.Abs(moved.Yaw) + Math.Abs(moved.Pitch) > 1,
                model + " moving pose");
        }

        var axes = new byte[80];
        Convert.FromHexString("040004001700000010004400").CopyTo(axes, 0);
        var alternate = new HeadTrackingDecoder(StartPacketFormat.Alternate, "airpods4");
        Require(alternate.IsHeadTrackingPacket(axes), "AirPods 4 relative-axis envelope");
        for (var i = 0; i < AirPods5Pose.RequiredSamples - 1; i++) alternate.Process(axes);
        Require(alternate.Process(axes)?.Pitch == 0 && alternate.RequiredSamples == AirPods5Pose.RequiredSamples,
            "relative-axis decoder calibrates independently");
    }

    private static void Require(bool condition, string check)
    {
        if (!condition) throw new InvalidOperationException("Failed: " + check);
    }
}
