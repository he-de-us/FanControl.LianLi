using System;
using System.Collections.Generic;
using System.Linq;

namespace LianLi
{
    public class Devices
    {

        private List<FanController> _fancontrollers = new List<FanController>{ };

        public Devices(bool enableARGB) {

            // Scan for controllers
            int[] VENDOR_IDS = { 0x0cf2 };
            int[] PRODUCT_IDS = { 0x7750, 0xa100, 0xa101, 0xa102, 0xa103, 0xa104, 0xa105 };

            var Devices = HID.Locate(VENDOR_IDS, PRODUCT_IDS);

            foreach (var device in Devices)
            {
                _fancontrollers.Add(new FanController(device, enableARGB));
            }

        }

        // Dispose Fan Controllers
        public void Dispose()
        {
            for (int i = 0; i < _fancontrollers.Count(); i++)
            {
                _fancontrollers[i].Dispose();
            }
        }

        public bool FanControllers_Located()
        {
            return _fancontrollers.Count != 0;
        }

        public int FanControllers_Count()
        {
            return _fancontrollers.Count;
        }

        public float? FanControllers_GetSpeed(int fancontroller_index, int fancontroller_channel)
        {
            return _fancontrollers[fancontroller_index].GetSpeed(fancontroller_channel);
        }

        public void FanControllers_SetSpeed(int fancontroller_index, int fancontroller_channel, float speed)
        {
            _fancontrollers[fancontroller_index].SetSpeed(fancontroller_channel, speed);
        }

    }

    internal class FanController
    {
        private enum Type {
            SL,
            SLV2,
            SLI,
            AL,
            ALV2,
            Unknown
        }

        private const int Channels = 4;
        // Re-send the requested speed even if unchanged. If the controller drops a command or
        // resets itself (sleep/resume, USB glitch) it falls back to full speed; this restores it.
        private static readonly TimeSpan SpeedKeepAlive = TimeSpan.FromSeconds(10);
        // Re-send "disable motherboard RPM sync" periodically for the same reason.
        private static readonly TimeSpan InitKeepAlive = TimeSpan.FromSeconds(30);
        // All 4 channel sensors share one input report; don't hit USB 4 times per tick.
        private static readonly TimeSpan ReportCacheTime = TimeSpan.FromMilliseconds(300);

        private readonly object _lock = new object();
        private readonly Type _type;
        private readonly HIDDevice _device;
        private readonly bool _enableARGB;

        private int _initializedGeneration = -1;
        private DateTime _lastInit = DateTime.MinValue;

        private readonly int?[] _targetSpeed = new int?[Channels];
        private readonly bool[] _pending = new bool[Channels];
        private readonly DateTime[] _lastSent = new DateTime[Channels];

        private byte[] _lastReport;
        private DateTime _lastReportTime = DateTime.MinValue;
        private readonly float?[] _lastRpm = new float?[Channels];

        public FanController(HIDDevice device, bool enableARGB)
        {
            _device = device;
            _enableARGB = enableARGB;
            switch (device._pid)
            {
                case 0xa100:
                case 0x7750:
                    _type = Type.SL;
                    break;
                case 0xa101:
                    _type = Type.AL;
                    break;
                case 0xa102:
                    _type = Type.SLI;
                    break;
                case 0xa103:
                case 0xa105:
                    _type = Type.SLV2;
                    break;
                case 0xa104:
                    _type = Type.ALV2;
                    break;
                default:
                    _type = Type.Unknown;
                    break;
            }

            lock (_lock) { EnsureInitialized(); }
        }

        public void SetSpeed(int fancontroller_channel, float speed)
        {
            if (fancontroller_channel < 0 || fancontroller_channel >= Channels) { return; }
            if (float.IsNaN(speed)) { return; }

            int clamped = (int)Math.Round(Math.Max(0f, Math.Min(100f, speed)));

            lock (_lock)
            {
                if (_targetSpeed[fancontroller_channel] != clamped)
                {
                    _targetSpeed[fancontroller_channel] = clamped;
                    _pending[fancontroller_channel] = true;
                }
                Service();
            }
        }

        public float? GetSpeed(int fancontroller_channel)
        {
            if (fancontroller_channel < 0 || fancontroller_channel >= Channels) { return null; }

            lock (_lock)
            {
                // Sensors are polled every tick even when no control changes, so use this
                // as the heartbeat for reconnect / re-init / speed keep-alive.
                Service();

                int offset;
                switch (_type)
                {
                    case Type.SLV2:
                    case Type.ALV2:
                        offset = 2;
                        break;
                    case Type.SL:
                    case Type.AL:
                    case Type.SLI:
                        offset = 1;
                        break;
                    default:
                        return null;
                }

                var now = DateTime.UtcNow;
                if (_lastReport == null || now - _lastReportTime > ReportCacheTime)
                {
                    var report = _device.ReadInputReport(224, 65);
                    if (report != null)
                    {
                        _lastReport = report;
                        _lastReportTime = now;
                        for (int ch = 0; ch < Channels; ch++)
                        {
                            int i = offset + ch * 2;
                            _lastRpm[ch] = (report[i] << 8) | report[i + 1];
                        }
                    }
                }

                // On a failed read keep the last good value instead of reporting 0 RPM.
                return _lastRpm[fancontroller_channel];
            }
        }

        public void Dispose()
        {
            _device.Dispose();
        }

        // ---- internals (call with _lock held) ----

        private void Service()
        {
            if (_type == Type.Unknown) { return; }

            EnsureInitialized();

            var now = DateTime.UtcNow;
            for (int ch = 0; ch < Channels; ch++)
            {
                if (!_targetSpeed[ch].HasValue) { continue; }
                if (!_pending[ch] && now - _lastSent[ch] < SpeedKeepAlive) { continue; }

                if (WriteSpeed(ch, _targetSpeed[ch].Value))
                {
                    _pending[ch] = false;
                    _lastSent[ch] = now;
                }
            }
        }

        private void EnsureInitialized()
        {
            if (_type == Type.Unknown) { return; }

            int generation = _device.OpenGeneration;
            bool reopened = generation != _initializedGeneration;
            if (!reopened && DateTime.UtcNow - _lastInit < InitKeepAlive) { return; }

            bool ok = true;
            if (reopened && _enableARGB) { ok &= SetARGB(); }
            for (int i = 0; i < Channels; i++) { ok &= DisableRPMSync(i); }

            if (!ok) { return; }

            _lastInit = DateTime.UtcNow;

            if (reopened)
            {
                // Controller may have reset to defaults (full speed): push all speeds again.
                for (int ch = 0; ch < Channels; ch++) { _pending[ch] = _targetSpeed[ch].HasValue; }
                _lastReport = null;
            }
            // Only mark as initialized if the generation didn't change mid-way (reopen during writes).
            if (_device.OpenGeneration == generation) { _initializedGeneration = generation; }
        }

        private bool WriteSpeed(int fancontroller_channel, int speed)
        {
            var speed_800_1900 = (byte)((800 + (11 * speed)) / 19);
            var speed_250_2000 = (byte)((250 + (17.5 * speed)) / 20);
            var speed_200_2100 = (byte)((200 + (19 * speed)) / 21);

            switch (_type)
            {
                case Type.SL:
                case Type.AL:
                    return _device.Write(new byte[] { 224, (byte)(32 + fancontroller_channel), 0, speed_800_1900 });
                case Type.SLV2:
                case Type.ALV2:
                    return _device.Write(new byte[] { 224, (byte)(32 + fancontroller_channel), 0, speed_250_2000 });
                case Type.SLI:
                    return _device.Write(new byte[] { 224, (byte)(32 + fancontroller_channel), 0, speed_200_2100 });
            }
            return false;
        }

        private bool DisableRPMSync(int fancontroller_channel)
        {
            byte channelByte = (byte)(16 << fancontroller_channel);

            switch (_type)
            {
                case Type.SL:
                    return _device.Write(new byte[] { 224, 16, 49, channelByte });
                case Type.AL:
                    return _device.Write(new byte[] { 224, 16, 66, channelByte });
                case Type.SLV2:
                case Type.ALV2:
                case Type.SLI:
                    return _device.Write(new byte[] { 224, 16, 98, channelByte });
            }
            return false;
        }

        private bool SetARGB()
        {
            switch(_type)
            {
                case Type.SL:
                    return _device.Write(new byte[] { 224, 16, 48, 1, 0, 0, 0 });
                case Type.AL:
                    return _device.Write(new byte[] { 224, 16, 65, 1, 0, 0, 0 });
                case Type.SLV2:
                case Type.ALV2:
                case Type.SLI:
                    return _device.Write(new byte[] { 224, 16, 97, 1, 0, 0, 0 });
            }
            return false;
        }

    }
}
