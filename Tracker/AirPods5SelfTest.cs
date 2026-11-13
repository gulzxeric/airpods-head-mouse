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

        var decoder = new HeadTrackingDecoder(airPods5: true);
        Require(decoder.Process(frame[..20]) is null, "short acknowledgement is ignored");
        for (var i = 0; i < decoder.RequiredSamples - 1; i++)
            Require(decoder.Process(frame) is null, "neutral calibration waits for enough samples");
        Require(decoder.Process(frame)?.Yaw == 0, "neutral yaw");
        BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(45), 1200);
        BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(47), -800);
        var pose = decoder.Process(frame) ?? throw new InvalidOperationException("missing decoded pose");
        Require(Math.Abs(pose.Yaw - 5.625) < 0.0001 && Math.Abs(pose.Pitch - 1.125) < 0.0001, "relative pose axes");

        using var input = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        input.Client.ReceiveTimeout = 1500;
        using var output = new OpenTrackUdpSender("127.0.0.1", ((IPEndPoint)input.Client.LocalEndPoint!).Port);
        output.Send(pose);
        IPEndPoint? remote = null;
        var data = input.Receive(ref remote);
        Require(data.Length == 48 && BitConverter.ToDouble(data, 24) == pose.Yaw &&
            BitConverter.ToDouble(data, 32) == pose.Pitch, "loopback UDP wire format");
        Console.WriteLine("PASS: AirPods 5 start/stop, report framing, calibration, pose and UDP output (synthetic reports).");
        return 0;
    }

    private static void Require(bool condition, string check)
    {
        if (!condition) throw new InvalidOperationException("Failed: " + check);
    }
}
