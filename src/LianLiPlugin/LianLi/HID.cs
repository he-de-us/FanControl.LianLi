using System;
using System.Collections.Generic;
using System.Linq;

namespace LianLi
{
    internal class HID
    {

        public static List<HIDDevice> Locate(int[] VENDOR_IDS, int[] PRODUCT_IDS)
        {

            var devices = new List<HIDDevice>();
            var locatedDevices = HidSharp.DeviceList.Local.GetHidDevices().ToArray();

            foreach (HidSharp.HidDevice device in locatedDevices)
            {
                int _vid, _pid;
                if (!TryGetIds(device, out _vid, out _pid)) { continue; }

                if (VENDOR_IDS.Contains(_vid) && PRODUCT_IDS.Contains(_pid))
                {
                    devices.Add(new HIDDevice(_vid, _pid, device));
                }
            }

            return devices;

        }

        internal static bool TryGetIds(HidSharp.HidDevice device, out int vid, out int pid)
        {
            vid = 0;
            pid = 0;
            string path;
            try { path = device.DevicePath; } catch { return false; }
            if (path == null) { return false; }

            var lower = path.ToLowerInvariant();
            return int.TryParse(GetIdentifierPart("vid_", lower), System.Globalization.NumberStyles.HexNumber, null, out vid)
                && int.TryParse(GetIdentifierPart("pid_", lower), System.Globalization.NumberStyles.HexNumber, null, out pid);
        }

        private static string GetIdentifierPart(string identifier, string DeviceId)
        {
            var index = DeviceId.IndexOf(identifier, StringComparison.Ordinal);
            if (index < 0 || index + 4 + 4 > DeviceId.Length) { return ""; }
            return DeviceId.Substring(index + 4, 4);
        }

    }

    /// <summary>
    /// Thread-safe wrapper around a HidSharp stream that never throws to the caller.
    /// FanControl calls sensors (Update) and controls (Set) from different threads, so all
    /// I/O is serialized with a lock. When I/O fails (timeout, device reset after sleep,
    /// USB glitch) the stream is closed and transparently reopened on a later call.
    /// </summary>
    internal class HIDDevice
    {
        // Short timeouts: FanControl calls Set() on its UI thread; the HidSharp default (3 s) froze it.
        private const int IoTimeoutMs = 500;
        // Consecutive failures after which the handle is considered broken and reopened.
        private const int FailuresBeforeReopen = 2;
        // Minimal delay between reopen attempts.
        private static readonly TimeSpan ReopenInterval = TimeSpan.FromSeconds(2);

        public int _pid;
        public int _vid;

        private readonly object _lock = new object();
        private HidSharp.HidDevice _device;
        private readonly string _devicePath;
        private HidSharp.HidStream _stream;
        private int _consecutiveFailures;
        private DateTime _lastReopenAttempt = DateTime.MinValue;
        private bool _disposed;

        /// <summary>Incremented each time the stream is (re)opened. Lets the owner re-initialize the controller.</summary>
        public int OpenGeneration { get; private set; }

        public HIDDevice(int VID, int PID, HidSharp.HidDevice Device)
        {
            _vid = VID;
            _pid = PID;
            _device = Device;
            try { _devicePath = Device.DevicePath; } catch { _devicePath = null; }

            lock (_lock) { TryOpen(); }
        }

        /// <summary>Reads an input report. Returns null on failure (never a zero-filled buffer).</summary>
        public byte[] ReadInputReport(byte reportId, int bufferLength)
        {
            lock (_lock)
            {
                if (!EnsureOpen()) { return null; }

                byte[] buffer = new byte[bufferLength];
                buffer[0] = reportId;
                try
                {
                    _stream.GetInputReport(buffer);
                    _consecutiveFailures = 0;
                    return buffer;
                }
                catch (Exception ex)
                {
                    OnFailure(ex);
                    return null;
                }
            }
        }

        /// <summary>Writes an output report. Returns false on failure.</summary>
        public bool Write(byte[] buffer)
        {
            lock (_lock)
            {
                if (!EnsureOpen()) { return false; }

                try
                {
                    _stream.Write(buffer);
                    _consecutiveFailures = 0;
                    return true;
                }
                catch (Exception ex)
                {
                    OnFailure(ex);
                    return false;
                }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _disposed = true;
                CloseStream();
            }
        }

        // ---- internals (call with _lock held) ----

        private bool EnsureOpen()
        {
            if (_disposed) { return false; }
            if (_stream != null) { return true; }
            if (DateTime.UtcNow - _lastReopenAttempt < ReopenInterval) { return false; }
            return TryOpen();
        }

        private bool TryOpen()
        {
            _lastReopenAttempt = DateTime.UtcNow;

            // After a USB re-enumeration the old HidDevice object can be stale: look it up again.
            var device = FindCurrentDevice() ?? _device;
            if (device == null) { return false; }

            try
            {
                HidSharp.HidStream stream;
                if (!device.TryOpen(out stream) || stream == null) { return false; }

                stream.ReadTimeout = IoTimeoutMs;
                stream.WriteTimeout = IoTimeoutMs;

                _device = device;
                _stream = stream;
                _consecutiveFailures = 0;
                OpenGeneration++;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private HidSharp.HidDevice FindCurrentDevice()
        {
            if (_devicePath == null) { return null; }
            try
            {
                foreach (var d in HidSharp.DeviceList.Local.GetHidDevices())
                {
                    string p;
                    try { p = d.DevicePath; } catch { continue; }
                    if (string.Equals(p, _devicePath, StringComparison.OrdinalIgnoreCase)) { return d; }
                }
            }
            catch
            {
                // Fall back to the cached device object.
            }
            return null;
        }

        private void OnFailure(Exception ex)
        {
            _consecutiveFailures++;

            bool fatal = ex is ObjectDisposedException
                      || ex is InvalidOperationException
                      || ex is UnauthorizedAccessException;

            if (fatal || _consecutiveFailures >= FailuresBeforeReopen)
            {
                CloseStream();
                // Allow an immediate first reopen attempt on the next call.
                _lastReopenAttempt = DateTime.MinValue;
            }
        }

        private void CloseStream()
        {
            if (_stream == null) { return; }
            try { _stream.Dispose(); } catch { }
            _stream = null;
        }

    }
}
