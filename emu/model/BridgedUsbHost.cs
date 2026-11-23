// Independent CDC transport; no firmware implementation is incorporated.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Antmicro.Renode.Core;

namespace Antmicro.Renode.Peripherals.USB
{
    public class BridgedUsbHost : MinimalUsbHost, IDisposable
    {
        public BridgedUsbHost(IMachine machine, RP2040UsbDevice controller,
            int consolePort = 19100, int framesPort = 19101) : base(machine, controller)
        {
            channels = new[] { new Channel(consolePort), new Channel(framesPort) };
            try
            {
                foreach(var c in channels) { c.Listener.Start(1); }
                worker = new Thread(NetworkLoop) { IsBackground = true, Name = "CDC loopback bridge" };
                worker.Start();
            }
            catch { Dispose(); throw; }
        }

        protected override bool CanReceiveBulk(int port)
        {
            lock(gate) { return channels[port].Client != null && channels[port].ToSocket.Count <= Capacity - 64; }
        }
        protected override void OnBulkReceived(int port, byte[] packet)
        {
            lock(gate)
            {
                var c = channels[port];
                if(c.Client != null) { foreach(byte b in packet) { c.ToSocket.Enqueue(b); } }
            }
        }
        protected override void PumpBulkOut()
        {
            lock(gate)
            {
                for(int i = 0; i < channels.Length; i++)
                {
                    var q = channels[i].ToUsb;
                    if(q.Count == 0) { continue; }
                    byte[] packet = Peek(q, 64);
                    if(TryBulkOut(i, packet)) { Remove(q, packet.Length); }
                }
            }
        }
        public void Dispose()
        {
            stopping = true;
            if(worker != null) { worker.Join(2000); }
            lock(gate)
            {
                foreach(var c in channels) { Disconnect(c); c.Listener.Stop(); }
            }
        }
        private void NetworkLoop()
        {
            while(!stopping)
            {
                lock(gate)
                {
                    foreach(var c in channels)
                    {
                        try
                        {
                            if(c.Client == null && c.Listener.Pending())
                            {
                                c.Client = c.Listener.AcceptSocket();
                                c.Client.Blocking = false;
                                c.Client.NoDelay = true;
                            }
                            if(c.Client == null) { continue; }
                            if(c.ToUsb.Count < Capacity && c.Client.Poll(0, SelectMode.SelectRead))
                            {
                                byte[] buffer = new byte[Math.Min(4096, Capacity - c.ToUsb.Count)];
                                int n = c.Client.Receive(buffer);
                                if(n == 0) { Disconnect(c); continue; }
                                for(int j = 0; j < n; j++) { c.ToUsb.Enqueue(buffer[j]); }
                            }
                            if(c.ToSocket.Count != 0 && c.Client.Poll(0, SelectMode.SelectWrite))
                            {
                                byte[] buffer = Peek(c.ToSocket, 4096);
                                int n = c.Client.Send(buffer);
                                if(n == 0) { Disconnect(c); continue; }
                                Remove(c.ToSocket, n);
                            }
                        }
                        catch(SocketException e)
                        {
                            if(e.SocketErrorCode != SocketError.WouldBlock) { Disconnect(c); }
                        }
                        catch(ObjectDisposedException) { Disconnect(c); }
                    }
                }
                Thread.Sleep(5);
            }
        }
        private static byte[] Peek(Queue<byte> q, int limit)
        {
            byte[] result = new byte[Math.Min(q.Count, limit)];
            int i = 0;
            foreach(byte b in q) { if(i == result.Length) break; result[i++] = b; }
            return result;
        }
        private static void Remove(Queue<byte> q, int count) { while(count-- > 0) q.Dequeue(); }
        private static void Disconnect(Channel c)
        {
            if(c.Client != null) { c.Client.Dispose(); c.Client = null; }
            c.ToUsb.Clear(); c.ToSocket.Clear();
        }
        private class Channel
        {
            public Channel(int port) { Listener = new TcpListener(IPAddress.Loopback, port); }
            public readonly TcpListener Listener;
            public Socket Client;
            public readonly Queue<byte> ToUsb = new Queue<byte>();
            public readonly Queue<byte> ToSocket = new Queue<byte>();
        }
        private const int Capacity = 65536;
        private readonly object gate = new object();
        private readonly Channel[] channels;
        private Thread worker;
        private volatile bool stopping;
    }
}
