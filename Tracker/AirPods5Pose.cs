// SPDX-License-Identifier: GPL-3.0-only
// Report layout and relative angle estimates follow PodsOnDroid HeadPose.kt,
// adapted there from LibrePods (Copyright (C) 2025 LibrePods contributors).
using System.Buffers.Binary;
using System.Diagnostics;

namespace AapHeadtracker;

internal sealed class AirPods5Pose
{
    private static ReadOnlySpan<byte> Prefix => [4, 0, 4, 0, 0x17, 0, 0, 0, 0x10, 0];
    public const int RequiredSamples = 10;
    private readonly Queue<(short Left, short Right)> _calibration = new();
    private double _neutralLeft, _neutralRight;
    private short _previousLeft, _previousRight;
    private long _lastTimestamp;
    private bool _calibrated;

    public int CalibrationCount => _calibration.Count;

    public static bool IsMotionReport(ReadOnlySpan<byte> data) =>
        data.Length > 60 && data.StartsWith(Prefix) && data[10] is 0x44 or 0x45 && data[11] == 0;

    public HeadTrackingSample? Process(ReadOnlySpan<byte> data)
    {
        if (!IsMotionReport(data)) return null;

        var timestamp = Stopwatch.GetTimestamp();
        var left = BinaryPrimitives.ReadInt16LittleEndian(data[45..]);
        var right = BinaryPrimitives.ReadInt16LittleEndian(data[47..]);
        if (_lastTimestamp != 0 && (Stopwatch.GetElapsedTime(_lastTimestamp, timestamp).TotalSeconds > 0.5 ||
            Math.Abs(left - _previousLeft) > 32768 || Math.Abs(right - _previousRight) > 32768))
        {
            _calibrated = false;
            _calibration.Clear();
        }
        _lastTimestamp = timestamp;
        _previousLeft = left;
        _previousRight = right;

        if (!_calibrated)
        {
            _calibration.Enqueue((left, right));
            while (_calibration.Max(sample => sample.Left) - _calibration.Min(sample => sample.Left) > 640 ||
                   _calibration.Max(sample => sample.Right) - _calibration.Min(sample => sample.Right) > 640)
            {
                _calibration.Dequeue();
            }
            if (_calibration.Count < RequiredSamples) return null;
            _neutralLeft = _calibration.Average(sample => sample.Left);
            _neutralRight = _calibration.Average(sample => sample.Right);
            _calibrated = true;
        }

        var leftOffset = left - _neutralLeft;
        var rightOffset = right - _neutralRight;
        const double degreesPerUnit = 180.0 / 64000;
        return new HeadTrackingSample(
            (leftOffset - rightOffset) * degreesPerUnit,
            (leftOffset + rightOffset) * degreesPerUnit,
            0, 0, left, right, 0,
            BinaryPrimitives.ReadInt16LittleEndian(data[51..]),
            BinaryPrimitives.ReadInt16LittleEndian(data[53..]));
    }
}
