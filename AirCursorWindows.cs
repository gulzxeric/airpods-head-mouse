using System;
using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        IPAddress phone = null;
        if (args.Length != 0 && (args.Length != 2 || args[0] != "--phone" ||
            !IPAddress.TryParse(args[1], out phone) || phone.AddressFamily != AddressFamily.InterNetwork ||
            IPAddress.IsLoopback(phone) || phone.Equals(IPAddress.Any) || phone.Equals(IPAddress.Broadcast)))
        {
            MessageBox.Show("请输入 iPhone 在 Wi-Fi 详情中显示的 IPv4 地址。", "AirCursor for Windows");
            return;
        }
        Application.Run(new PointerWindow(phone));
    }
}

internal sealed class PointerWindow : Form
{
    private const int PauseHotkey = 1;
    private const int RecenterHotkey = 2;
    private readonly CheckBox invertX = new CheckBox { Text = "反转左右", AutoSize = true };
    private readonly CheckBox invertY = new CheckBox { Text = "反转上下", AutoSize = true };
    private readonly CheckBox nodClick = new CheckBox { Text = "点头左键", AutoSize = true };
    private readonly NumericUpDown sensitivity = new NumericUpDown
    {
        DecimalPlaces = 1, Minimum = 0.2M, Maximum = 5, Value = 1, Increment = 0.1M, Width = 62
    };
    private readonly Button toggle = new Button { Text = "启用鼠标控制", AutoSize = true };
    private readonly Label status = new Label { AutoSize = true, Text = "等待本机姿态数据（UDP 4242）" };
    private readonly Label poseText = new Label { AutoSize = true };
    private readonly Stopwatch time = Stopwatch.StartNew();
    private readonly Timer timer = new Timer { Interval = 15 };
    private readonly MotionPointer motion = new MotionPointer();
    private readonly byte[] buffer = new byte[2048];
    private readonly IPAddress phone;
    private readonly string waiting;
    private EndPoint sender = new IPEndPoint(IPAddress.Any, 0);
    private Socket receiver;
    private bool enabled;
    private bool pauseKeyRegistered;
    private bool recenterKeyRegistered;
    private double lastPacketAt = -1;
    private double yaw;
    private double pitch;

    public PointerWindow(IPAddress phone = null)
    {
        this.phone = phone;
        waiting = phone == null ? "等待本机姿态数据（UDP 4242）" : "等待 iPhone " + phone + "（UDP 4242）";
        status.Text = waiting;
        Text = "AirCursor for Windows · 原型";
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Microsoft YaHei UI", 10F);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(478, 230);
        MinimumSize = Size;
        MaximizeBox = false;

        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            Padding = new Padding(17, 13, 12, 8)
        };
        layout.Controls.Add(new Label { Text = "AirPods 头部控制", Font = new Font(Font, FontStyle.Bold), AutoSize = true });
        layout.Controls.Add(status);
        layout.Controls.Add(poseText);
        var options = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        options.Controls.Add(new Label { Text = "灵敏度", AutoSize = true, Margin = new Padding(0, 6, 2, 0) });
        options.Controls.Add(sensitivity);
        options.Controls.Add(invertX);
        options.Controls.Add(invertY);
        options.Controls.Add(nodClick);
        layout.Controls.Add(options);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        var recenter = new Button { Text = "重置姿态", AutoSize = true };
        buttons.Controls.Add(toggle);
        buttons.Controls.Add(recenter);
        layout.Controls.Add(buttons);
        var shortcuts = new Label { AutoSize = true };
        layout.Controls.Add(shortcuts);
        Controls.Add(layout);

        toggle.Click += delegate { SetEnabled(!enabled); };
        recenter.Click += delegate { motion.Reset(); };
        nodClick.CheckedChanged += delegate { motion.Reset(); };
        invertX.CheckedChanged += delegate { motion.Reset(); };
        invertY.CheckedChanged += delegate { motion.Reset(); };
        Shown += delegate
        {
            pauseKeyRegistered = RegisterHotKey(Handle, PauseHotkey, 0x4003, (uint)Keys.F8);
            recenterKeyRegistered = RegisterHotKey(Handle, RecenterHotkey, 0x4003, (uint)Keys.F9);
            shortcuts.Text = (pauseKeyRegistered ? "Ctrl+Alt+F8 启用／暂停" : "F8 热键被占用") + "    " +
                (recenterKeyRegistered ? "Ctrl+Alt+F9 重置姿态" : "F9 热键被占用");
            OpenReceiver();
        };
        timer.Tick += delegate { ReadPose(); };
        FormClosed += delegate
        {
            timer.Dispose();
            if (receiver != null) receiver.Dispose();
        };
    }

    private void OpenReceiver()
    {
        receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            receiver.ExclusiveAddressUse = true;
            receiver.Bind(new IPEndPoint(phone == null ? IPAddress.Loopback : IPAddress.Any, 4242));
            timer.Start();
        }
        catch (SocketException ex)
        {
            receiver.Dispose();
            receiver = null;
            toggle.Enabled = false;
            status.Text = "UDP 4242 无法监听（" + ex.SocketErrorCode + "）";
            poseText.Text = "如有 OpenTrack 或另一个窗口在运行，关闭后重新打开。";
        }
    }

    private void SetEnabled(bool value)
    {
        if (receiver == null) return;
        enabled = value;
        toggle.Text = enabled ? "暂停鼠标控制" : "启用鼠标控制";
        motion.Reset();
    }

    private void ReadPose()
    {
        double now = time.Elapsed.TotalSeconds;
        if (now - lastPacketAt > MotionPointer.Timeout) motion.Reset();
        try
        {
            for (int i = 0; i < 64 && receiver.Poll(0, SelectMode.SelectRead); i++)
            {
                int size;
                try { size = receiver.ReceiveFrom(buffer, ref sender); }
                catch (SocketException ex)
                {
                    if (ex.SocketErrorCode == SocketError.MessageSize) continue;
                    throw;
                }
                if (phone != null && !((IPEndPoint)sender).Address.Equals(phone)) continue;
                double packetYaw, packetPitch, packetRoll;
                if (!MotionPointer.TryReadPose(buffer, size, out packetYaw, out packetPitch, out packetRoll)) continue;
                yaw = packetYaw;
                pitch = packetPitch;
                lastPacketAt = now;
                if (!enabled) continue;

                MouseStep step = motion.Update(invertX.Checked ? -yaw : yaw, pitch,
                    (double)sensitivity.Value, invertY.Checked, nodClick.Checked, now);
                WindowsMouse.Apply(step);
            }
        }
        catch (SocketException ex)
        {
            StopWithError(ex.Message);
            return;
        }
        catch (InvalidOperationException ex)
        {
            StopWithError(ex.Message);
            return;
        }

        bool receiving = now - lastPacketAt <= MotionPointer.Timeout;
        status.Text = receiving ? (enabled ? "鼠标控制中" : "已收到姿态，鼠标控制已暂停") :
            (lastPacketAt < 0 ? waiting : "姿态流已中断，光标已停住");
        poseText.Text = receiving ? String.Format("左右 {0,6:0.0}°   上下 {1,6:0.0}°", yaw, pitch) : "";
    }

    private void StopWithError(string details)
    {
        SetEnabled(false);
        timer.Stop();
        toggle.Enabled = false;
        status.Text = "鼠标控制已停止，请重新打开窗口。";
        poseText.Text = details;
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0312)
        {
            int key = message.WParam.ToInt32();
            if (key == PauseHotkey && toggle.Enabled) SetEnabled(!enabled);
            if (key == RecenterHotkey) motion.Reset();
        }
        base.WndProc(ref message);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (pauseKeyRegistered) UnregisterHotKey(Handle, PauseHotkey);
        if (recenterKeyRegistered) UnregisterHotKey(Handle, RecenterHotkey);
        base.OnHandleDestroyed(e);
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}

internal struct MouseStep
{
    public int X, Y;
    public bool Click;
}

internal sealed class MotionPointer
{
    public const double Timeout = 0.35;
    private const double DeadZone = 0.07;
    private double previousYaw, previousPitch;
    private double carryX, carryY;
    private double smoothYaw, smoothPitch;
    private double stablePitch, stableSince, nodStartAt, cooldownUntil;
    private int nodDirection;
    private bool hasPose;

    public void Reset()
    {
        hasPose = false;
        carryX = carryY = smoothYaw = smoothPitch = 0;
        nodDirection = 0;
        cooldownUntil = 0;
    }

    public static bool TryReadPose(byte[] bytes, int size, out double yaw, out double pitch, out double roll)
    {
        yaw = pitch = roll = 0;
        if (size != 48) return false;
        yaw = BitConverter.ToDouble(bytes, 24);
        pitch = BitConverter.ToDouble(bytes, 32);
        roll = BitConverter.ToDouble(bytes, 40);
        return Math.Abs(yaw) <= 360 && Math.Abs(pitch) <= 360 && Math.Abs(roll) <= 360;
    }

    public MouseStep Update(double yaw, double pitch, double sensitivity, bool invertPitch, bool clickOnNod, double now)
    {
        if (!hasPose)
        {
            hasPose = true;
            previousYaw = yaw;
            previousPitch = stablePitch = pitch;
            stableSince = now;
            return new MouseStep();
        }

        double dx = AngleDifference(yaw, previousYaw);
        double dy = AngleDifference(pitch, previousPitch);
        previousYaw = yaw;
        previousPitch = pitch;
        if (Math.Abs(dx) > 32 || Math.Abs(dy) > 32)
        {
            Reset();
            return new MouseStep();
        }

        if (clickOnNod && now >= cooldownUntil)
        {
            double nodOffset = AngleDifference(pitch, stablePitch);
            if (nodDirection == 0 && now - stableSince >= 0.18 && Math.Abs(dx) < 1 &&
                Math.Abs(dy) >= 1.1 && Math.Abs(nodOffset) >= 1.1)
            {
                nodDirection = Math.Sign(nodOffset);
                nodStartAt = now;
            }
            if (nodDirection != 0)
            {
                // Remember the peak while the pointer is parked. A normal pitch move times out.
                double elapsed = now - nodStartAt;
                if (Math.Abs(dx) > 2.5 || (elapsed > 0.24 && Math.Abs(nodDirection) == 1) || elapsed > 0.55)
                {
                    nodDirection = 0;
                    stablePitch = pitch;
                    stableSince = now;
                }
                else
                {
                    if (nodDirection * nodOffset >= 5.5) nodDirection = Math.Sign(nodDirection) * 2;
                    bool click = Math.Abs(nodDirection) == 2 && Math.Abs(nodOffset) <= 1.8 && elapsed >= 0.08;
                    if (click)
                    {
                        nodDirection = 0;
                        stablePitch = pitch;
                        stableSince = now;
                        cooldownUntil = now + 0.55;
                    }
                    carryX = carryY = smoothYaw = smoothPitch = 0;
                    return new MouseStep { Click = click };
                }
            }
        }
        if (Math.Abs(dx) > 0.8 || Math.Abs(dy) > 0.8 || Math.Abs(AngleDifference(pitch, stablePitch)) > 1.0)
        {
            stablePitch = pitch;
            stableSince = now;
        }

        smoothYaw = Smooth(dx, smoothYaw);
        smoothPitch = Smooth(dy, smoothPitch);
        carryX += Scale(smoothYaw, sensitivity);
        carryY += Scale(smoothPitch, sensitivity) * (invertPitch ? 1 : -1);
        var step = new MouseStep { X = (int)carryX, Y = (int)carryY };
        carryX -= step.X;
        carryY -= step.Y;
        return step;
    }

    private static double Smooth(double delta, double previous)
    {
        if (Math.Abs(delta) <= DeadZone) delta = 0;
        return delta * 0.65 + previous * 0.35;
    }

    private static double Scale(double delta, double sensitivity)
    {
        if (Math.Abs(delta) <= DeadZone) return 0;
        return delta * Math.Min(32, 12 + 5 * Math.Abs(delta)) * sensitivity;
    }

    private static double AngleDifference(double current, double previous)
    {
        return ((current - previous + 900) % 360) - 180;
    }
}

internal static class WindowsMouse
{
    public static void Apply(MouseStep step)
    {
        if (step.X != 0 || step.Y != 0)
        {
            var move = new Input { Mouse = new MouseInput { X = step.X, Y = step.Y, Flags = 0x0001 } };
            Send(new[] { move });
        }
        if (step.Click)
        {
            var down = new Input { Mouse = new MouseInput { Flags = 0x0002 } };
            var up = new Input { Mouse = new MouseInput { Flags = 0x0004 } };
            if (SendInput(2, new[] { down, up }, Marshal.SizeOf(typeof(Input))) != 2)
            {
                SendInput(1, new[] { up }, Marshal.SizeOf(typeof(Input)));
                throw new InvalidOperationException("Windows 拒绝了点击事件。");
            }
        }
    }

    private static void Send(Input[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Input))) != inputs.Length)
            throw new InvalidOperationException("Windows 拒绝了光标移动事件。");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] Input[] inputs, int size);
}
