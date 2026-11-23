// Independent minimal USB host, implemented from the Stage 2 register/packet design.
// No firmware or TinyUSB implementation is incorporated.
using System;
using System.Collections.Generic;
using System.Text;
using Antmicro.Renode.Core;

namespace Antmicro.Renode.Peripherals.USB
{
    public class MinimalUsbHost : IPeripheral
    {
        public MinimalUsbHost(IMachine machine, RP2040UsbDevice controller)
        {
            device = controller;
            device.Tick += Tick;
            Reset();
        }

        public string State { get { return state; } }
        public string Failure { get { return failure; } }
        public void Reset()
        {
            ticks = 0;
            epoch = -1;
            state = "Detached";
            failure = "";
            phase = Phase.Idle;
            ports.Clear();
            data.Clear();
            expectedAddress = 0;
            maxPacket = 64;
            step = totalLength = configuration = requestedLength = pid = 0;
            configured = false;
            bulkBytes = 0;
        }

        // A USB bus reset, intentionally separate from a machine/CPU reset.
        public void ForceBusReset()
        {
            if(!device.Attached) { throw new InvalidOperationException("USB is detached"); }
            BeginReset();
        }

        private void Tick()
        {
            ticks++;
            try
            {
                if(!device.Attached)
                {
                    if(state != "Detached") { Log("detach: discard pending host request"); }
                    state = "Detached";
                    phase = Phase.Idle;
                    configured = false;
                    return;
                }
                if(epoch != device.Epoch || state == "Detached")
                {
                    epoch = device.Epoch;
                    state = "AttachDelay";
                    phase = Phase.Idle;
                    deadline = ticks + 1000;
                    configured = false;
                    ports.Clear();
                    Log("pullup attached; reset in 10ms");
                }
                if(state == "Failed") { return; }
                if(state == "AttachDelay")
                {
                    if(ticks >= deadline) { BeginReset(); }
                    return;
                }
                if(state == "Reset")
                {
                    if(ticks >= deadline)
                    {
                        device.BusReset(false);
                        state = "ResetRecovery";
                        deadline = ticks + 1000;
                        requestDeadline = ticks + RequestTimeout;
                        Log("bus reset released; idle J; recover 10ms");
                    }
                    return;
                }
                if(ticks % 100 == 0) { device.FrameTick(); }
                if(state == "ResetRecovery")
                {
                    if(ticks > requestDeadline) { Fail("bus reset IRQ not acknowledged"); return; }
                    if(ticks < deadline || (device.ReadDoubleWord(0x50) & 0x80000) != 0) { return; }
                    Log("bus reset acknowledged");
                    state = "Default";
                    step = 0;
                    StartStep();
                    return;
                }
                // Finish the short enumeration sequence before polling bulk. Otherwise
                // an unrelated bulk completion could starve the global status-ack gate.
                if(configured && phase == Phase.Idle) { DrainBulk(); PumpBulkOut(); }
                if(phase != Phase.Idle && ticks > requestDeadline)
                {
                    Fail("control timeout step=" + step + " phase=" + phase);
                    return;
                }
                ProcessControl();
            }
            catch(Exception e)
            {
                Fail(e.GetType().Name + ": " + e.Message);
            }
        }

        private void BeginReset()
        {
            configured = false;
            ports.Clear();
            data.Clear();
            phase = Phase.Idle;
            expectedAddress = 0;
            maxPacket = 64;
            step = totalLength = configuration = requestedLength = pid = 0;
            device.BusReset(true);
            epoch = device.Epoch;
            state = "Reset";
            deadline = ticks + 1000;
            Log("bus reset asserted SE0; address/pending host transfers discarded");
        }

        private void StartStep()
        {
            if(device.HasPendingCompletions) { throw new InvalidOperationException("new SETUP while completion pending"); }
            if(device.Address != expectedAddress) { throw new InvalidOperationException("address mismatch expected=" + expectedAddress + " actual=" + device.Address); }
            switch(step)
            {
                case 0: Setup(0x80, 6, 0x100, 0, 8); break;
                case 1: Setup(0, 5, 1, 0, 0); break;
                case 2: Setup(0x80, 6, 0x100, 0, 18); break;
                case 3: Setup(0x80, 6, 0x200, 0, 9); break;
                case 4: Setup(0x80, 6, 0x200, 0, totalLength); break;
                case 5: Setup(0, 9, configuration, 0, 0); break;
                case 6: Setup(0x21, 0x22, 1, ports[0].Communication, 0); break;
                case 7: Setup(0x21, 0x22, 1, ports[1].Communication, 0); break;
                default:
                    state = "CdcBothOpen";
                    phase = Phase.Idle;
                    Log("CdcBothOpen: both DTR status packets completed and acknowledged; firmware connection must be independently checked");
                    break;
            }
        }

        private void Setup(int bm, int request, int value, int index, int length)
        {
            byte[] setup = { (byte)bm, (byte)request, (byte)value, (byte)(value >> 8), (byte)index, (byte)(index >> 8), (byte)length, (byte)(length >> 8) };
            requestedLength = length;
            data.Clear();
            pid = 1;
            device.SubmitSetup(setup);
            phase = length > 0 ? Phase.DataIn : Phase.StatusIn;
            requestDeadline = ticks + RequestTimeout;
            Log(String.Format("SETUP step={0} address={1} bytes={2}", step + 1, expectedAddress, Hex(setup)));
        }

        private void ProcessControl()
        {
            byte[] packet;
            switch(phase)
            {
                case Phase.DataIn:
                    if(!device.TryIn(0, pid, out packet)) { return; }
                    if(packet.Length > maxPacket || data.Count + packet.Length > requestedLength) { throw new InvalidOperationException("EP0 IN exceeds descriptor/request capacity"); }
                    data.AddRange(packet);
                    Log("EP0 data IN PID=" + pid + " bytes=" + Hex(packet));
                    pid ^= 1;
                    if(data.Count == requestedLength || packet.Length < maxPacket) { phase = Phase.StatusOut; }
                    break;
                case Phase.StatusOut:
                    if(!device.TryOut(0, 1, new byte[0])) { return; }
                    Log("EP0 status OUT DATA1 ZLP complete step=" + (step + 1));
                    phase = Phase.AwaitAck;
                    break;
                case Phase.StatusIn:
                    if(!device.TryIn(0, 1, out packet)) { return; }
                    if(packet.Length != 0) { throw new InvalidOperationException("status IN was not a ZLP"); }
                    Log("EP0 status IN DATA1 ZLP complete step=" + (step + 1) + " old-address=" + expectedAddress);
                    phase = Phase.AwaitAck;
                    break;
                case Phase.AwaitAck:
                    if(device.HasPendingCompletions) { return; }
                    if(step == 1 && device.Address != 1) { return; }
                    ValidateResponse();
                    step++;
                    StartStep();
                    break;
            }
        }

        private void ValidateResponse()
        {
            byte[] d = data.ToArray();
            switch(step)
            {
                case 0:
                    Require(d.Length == 8 && d[0] == 18 && d[1] == 1, "invalid device descriptor prefix");
                    Require(d[7] == 8 || d[7] == 16 || d[7] == 32 || d[7] == 64, "invalid EP0 max packet");
                    maxPacket = d[7];
                    break;
                case 1:
                    expectedAddress = 1;
                    state = "Addressed";
                    Log("SET_ADDRESS status acknowledged; firmware ADDR_ENDP=1 observed");
                    break;
                case 2:
                    Require(d.Length == 18 && d[0] == 18 && d[1] == 1 && d[7] == maxPacket, "invalid full device descriptor");
                    Require(U16(d, 8) == 0x239a && U16(d, 10) == 0xcafe, "VID:PID mismatch");
                    Log("device descriptor VID:PID=239a:cafe verified via DPRAM");
                    break;
                case 3:
                    Require(d.Length == 9 && d[0] == 9 && d[1] == 2, "invalid config header");
                    totalLength = U16(d, 2);
                    configuration = d[5];
                    Require(totalLength >= 9 && configuration != 0, "invalid configuration length/value");
                    break;
                case 4:
                    Require(d.Length == totalLength && U16(d, 2) == totalLength && d[5] == configuration, "config response length/value changed");
                    ParseConfiguration(d);
                    break;
                case 5:
                    configured = true;
                    state = "Configured";
                    Log("SET_CONFIGURATION status completed configuration=" + configuration);
                    break;
                case 6:
                    state = "Cdc0Open";
                    Log("CDC0 DTR status acknowledged interface=" + ports[0].Communication);
                    break;
                case 7:
                    Log("CDC1 DTR status acknowledged interface=" + ports[1].Communication);
                    break;
            }
        }

        private void ParseConfiguration(byte[] d)
        {
            var interfaces = new Dictionary<int, Interface>();
            var unions = new Dictionary<int, int>();
            var iads = new List<int[]>();
            Interface current = null;
            for(int p = 0; p < d.Length;)
            {
                int n = d[p];
                Require(n >= 2 && p + n <= d.Length, "malformed descriptor boundary at " + p);
                switch(d[p + 1])
                {
                    case 4:
                        Require(n >= 9, "short interface descriptor");
                        current = null;
                        if(d[p + 3] == 0)
                        {
                            Require(!interfaces.ContainsKey(d[p + 2]), "duplicate interface");
                            current = new Interface { Number = d[p + 2], Class = d[p + 5], Subclass = d[p + 6] };
                            interfaces.Add(current.Number, current);
                        }
                        break;
                    case 5:
                        Require(n >= 7, "short endpoint descriptor");
                        if(current != null && (d[p + 3] & 3) == 2)
                        {
                            Require((d[p + 2] & 15) != 0 && U16(d, p + 4) <= 64 && U16(d, p + 4) > 0, "invalid full speed bulk endpoint");
                            if((d[p + 2] & 128) != 0) { current.BulkIn.Add(d[p + 2] & 15); }
                            else { current.BulkOut.Add(d[p + 2] & 15); }
                        }
                        break;
                    case 11:
                        Require(n >= 8, "short IAD");
                        if(d[p + 4] == 2) { iads.Add(new[] { (int)d[p + 2], (int)d[p + 3] }); }
                        break;
                    case 0x24:
                        if(n >= 3 && d[p + 2] == 6)
                        {
                            Require(n == 5 && current != null && current.Number == d[p + 3], "unsupported or invalid CDC union");
                            Require(!unions.ContainsKey(d[p + 3]), "duplicate CDC union");
                            unions.Add(d[p + 3], d[p + 4]);
                        }
                        break;
                }
                p += n;
            }
            var keys = new List<int>(interfaces.Keys);
            keys.Sort();
            var endpointOwners = new HashSet<int>();
            var outEndpointOwners = new HashSet<int>();
            var dataOwners = new HashSet<int>();
            foreach(int key in keys)
            {
                Interface communication = interfaces[key];
                if(communication.Class != 2 || communication.Subclass != 2) { continue; }
                Require(unions.ContainsKey(key), "CDC communication interface has no union");
                int slave = unions[key];
                Require(interfaces.ContainsKey(slave) && interfaces[slave].Class == 10, "CDC union data interface missing");
                bool associated = false;
                foreach(int[] iad in iads)
                {
                    if(iad[0] == key && slave >= iad[0] && slave < iad[0] + iad[1]) { associated = true; }
                }
                Require(associated, "CDC union not covered by matching IAD");
                Require(dataOwners.Add(slave), "data interface shared by multiple CDC unions");
                Interface dat = interfaces[slave];
                Require(dat.BulkIn.Count == 1 && dat.BulkOut.Count == 1, "CDC data must have one bulk IN and OUT");
                Require(endpointOwners.Add(dat.BulkIn[0]), "duplicate bulk IN endpoint");
                Require(outEndpointOwners.Add(dat.BulkOut[0]), "duplicate bulk OUT endpoint");
                var port = new Port { Communication = key, Data = slave, BulkIn = dat.BulkIn[0], BulkOut = dat.BulkOut[0], Pid = 0 };
                ports.Add(port);
                Log("CDC descriptor association communication=" + key + " data=" + slave + " bulk IN=" + port.BulkIn + " OUT=" + port.BulkOut);
            }
            Require(ports.Count == 2, "expected exactly two CDC ACM functions");
        }

        private void DrainBulk()
        {
            for(int n = 0; n < ports.Count; n++)
            {
                Port p = ports[n];
                byte[] packet;
                if(!CanReceiveBulk(n) || !device.TryIn(p.BulkIn, p.Pid, out packet)) { continue; }
                p.Pid ^= 1;
                bulkBytes += packet.Length;
                OnBulkReceived(n, packet);
                Log("CDC" + n + " bulk IN ep=" + p.BulkIn + " bytes=" + packet.Length + " total=" + bulkBytes + " hex=" + Hex(packet) + " text=\"" + Escape(packet) + "\"");
            }
        }

        // Optional transport hooks: default host retains its original drain-only behavior.
        protected virtual bool CanReceiveBulk(int port) { return true; }
        protected virtual void OnBulkReceived(int port, byte[] packet) { }
        protected virtual void PumpBulkOut() { }
        protected bool TryBulkOut(int port, byte[] packet)
        {
            if(state != "CdcBothOpen" || port < 0 || port >= ports.Count || packet.Length > 64) { return false; }
            Port p = ports[port];
            if(!device.TryOut(p.BulkOut, p.OutPid, packet)) { return false; }
            p.OutPid ^= 1;
            return true;
        }

        private void Fail(string reason)
        {
            if(state == "Failed") { return; }
            failure = reason;
            state = "Failed";
            phase = Phase.Idle;
            Log("FAILED " + reason + " snapshot=" + device.Snapshot());
        }
        private void Log(string message) { device.Log(String.Format("HOST t={0:F6}s epoch={1} {2}", ticks / 100000.0, epoch, message)); }
        private static void Require(bool test, string reason) { if(!test) { throw new InvalidOperationException(reason); } }
        private static int U16(byte[] d, int p) { return d[p] | (d[p + 1] << 8); }
        private static string Hex(byte[] d) { return BitConverter.ToString(d).Replace("-", " "); }
        private static string Escape(byte[] d)
        {
            var b = new StringBuilder();
            foreach(byte c in d)
            {
                if(c == 10) { b.Append("\\n"); }
                else if(c == 13) { b.Append("\\r"); }
                else if(c == 34 || c == 92) { b.Append('\\'); b.Append((char)c); }
                else if(c >= 32 && c <= 126) { b.Append((char)c); }
                else { b.Append("\\x"); b.Append(c.ToString("x2")); }
            }
            return b.ToString();
        }
        private class Interface
        {
            public int Number, Class, Subclass;
            public readonly List<int> BulkIn = new List<int>();
            public readonly List<int> BulkOut = new List<int>();
        }
        private class Port { public int Communication, Data, BulkIn, BulkOut, Pid, OutPid; }
        private enum Phase { Idle, DataIn, StatusIn, StatusOut, AwaitAck }
        private readonly RP2040UsbDevice device;
        private readonly List<Port> ports = new List<Port>();
        private readonly List<byte> data = new List<byte>();
        private const long RequestTimeout = 200000;
        private long ticks, deadline, requestDeadline, bulkBytes;
        private int epoch, step, pid, requestedLength, expectedAddress, totalLength, configuration;
        private int maxPacket = 64;
        private bool configured;
        private string state, failure;
        private Phase phase;
    }
}
