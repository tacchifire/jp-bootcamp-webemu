// Independent minimal USB device model, derived from RP2040 register contracts.
// No firmware/Arduino/TinyUSB implementation code is incorporated.
using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;
using Antmicro.Renode.Logging;

namespace Antmicro.Renode.Peripherals.USB
{
    public class RP2040UsbDevice : IDoubleWordPeripheral, IKnownSize
    {
        public RP2040UsbDevice(IMachine machine)
        {
            this.machine = machine;
            IRQ = new GPIO();
            for(int i=0;i<32;i++) completions[i] = new Queue<int>();
            timer = new LimitTimer(machine.ClockSource, 1000000, this, "USB host tick", limit: 10,
                direction: Direction.Ascending, enabled: true, workMode: WorkMode.Periodic, eventEnabled: true);
            timer.LimitReached += delegate { ticks++; if(Tick != null) Tick(); };
            Reset();
        }
        public event Action Tick;
        public GPIO IRQ { get; private set; }
        public long Size { get { return 0x4000; } }
        public int Epoch { get; private set; }
        public int Address { get { return (int)(regs[0] & 127); } }
        public bool Attached { get { return (regs[0x40/4]&3)==1 && (regs[0x4c/4]&0x10000)!=0 && (regs[0x74/4]&9)==9 && Vbus; } }
        public bool HasPendingCompletions { get { return BufferStatus != 0; } }
        public bool PendingCompletions { get { return HasPendingCompletions; } }
        private bool Vbus { get { return (regs[0x78/4]&8)==0 || (regs[0x78/4]&4)!=0; } }
        public void Log(string message) { this.Log(LogLevel.Info, "USB t={0}us epoch={1} {2}", ticks*10, Epoch, message); }
        public void Reset()
        {
            Array.Clear(regs,0,regs.Length); regs[0x6c/4]=0x100010; regs[0x84/4]=0x1f1f;
            sie=0; sof=false; frame=0; resetting=false; Epoch++; ticks=0;
            ClearTransfers(); IRQ.Unset();
            if(timer!=null) { timer.Reset(); timer.Enabled=true; }
        }
        private void ClearTransfers()
        {
            for(int i=0;i<32;i++) { completions[i].Clear(); selectors[i]=0; }
        }
        public uint ReadDoubleWord(long offset)
        {
            int o=(int)(offset&0xfff);
            switch(o)
            {
                case 0x44: return 0;
                case 0x48: sof=false; UpdateIRQ(); return frame;
                case 0x50: return sie | (Vbus?1u:0u) | (Attached&&!resetting?4u:0u);
                case 0x58: return BufferStatus;
                case 0x5c: uint result=0; for(int i=0;i<32;i++) if(completions[i].Count>0 && completions[i].Peek()==1) result|=1u<<i; return result;
                case 0x8c: return RawInterrupts;
                case 0x98: return (RawInterrupts & regs[0x90/4]) | regs[0x94/4];
                default: return o>=0 && o<regs.Length*4 && o%4==0 ? regs[o/4] : 0;
            }
        }
        public void WriteDoubleWord(long offset,uint value)
        {
            int o=(int)(offset&0xfff), alias=(int)((offset>>12)&3);
            bool attached=Attached;
            if(o==0x50 || o==0x58 || o==0x64 || o==0x70)
            {
                if(alias==1 || alias==2) { Log("unsupported SET/XOR on WC register 0x"+o.ToString("x")); return; }
                if(o==0x50) { sie &= ~(value & 0xff0f0b10u); Log("SIE ACK 0x"+value.ToString("x")); }
                else if(o==0x58) { for(int i=0;i<32;i++) if((value&(1u<<i))!=0 && completions[i].Count>0) completions[i].Dequeue(); }
                else regs[o/4]&=~value;
                UpdateIRQ(); return;
            }
            if(o==0x48 || o==0x5c || o==0x8c || o==0x98) { Log("ignored RO write 0x"+o.ToString("x")); return; }
            uint mask=Mask(o); if(mask==0) return;
            uint old=regs[o/4];
            regs[o/4]=(alias==1?old^value:alias==2?old|value:alias==3?old&~value:value)&mask;
            if(o==0x4c) regs[o/4]&=~(1u<<12); // RESUME pulse completes immediately in this normal-path model.
            if(o==0x60)
            {
                uint abort=regs[o/4]; regs[0x64/4]|=abort;
                for(int i=0;i<32;i++) if((abort&(1u<<i))!=0)
                {
                    completions[i].Clear(); selectors[i]=0;
                    for(int half=0;half<2;half++) { int o2=0x80+4*i+2*half; WriteRam16(o2,(ushort)(ReadRam16(o2)&~0x0400)); }
                }
                Log("ABORT 0x"+abort.ToString("x"));
            }
            if(o==0) Log("ADDR_ENDP write address="+Address);
            if(o==0x80 && regs[o/4]!=0) Log("PHY override stored; analog override behavior unsupported: 0x"+regs[o/4].ToString("x"));
            if(attached!=Attached)
            {
                Epoch++; ClearTransfers(); Log(Attached?"pullup attach":"pullup detach");
                if(!Attached) { sie=0; sof=false; regs[0]=0; }
            }
            UpdateIRQ();
        }
        private uint Mask(int o)
        {
            if(o==0) return 0xf007f;
            if(o>=4 && o<=0x3c && o%4==0) return 0x60f007f;
            switch(o) {
            case 0x40:return 0x80000003; case 0x44:return 0x7ff; case 0x4c:return 0xff07bf5f;
            case 0x54:return 0xfffe; case 0x60:return 0xffffffff; case 0x68:return 3;
            case 0x6c:return 0x3ff03ff; case 0x74:return 15; case 0x78:return 63;
            case 0x7c:return 0x7fff77; case 0x80:return 0x9fff; case 0x84:return 0x1f1f;
            case 0x90:case 0x94:return 0xfffff; default:return 0; }
        }
        private uint BufferStatus { get { uint v=0; for(int i=0;i<32;i++) if(completions[i].Count>0) v|=1u<<i; return v; } }
        private uint RawInterrupts
        {
            get {
                uint v=BufferStatus!=0?0x10u:0;
                if((sie&(1u<<31))!=0) v|=1u<<5;
                if((sie&(1u<<27))!=0) v|=1u<<6;
                if((sie&(1u<<26))!=0) v|=1u<<7;
                if((sie&(1u<<25))!=0) v|=1u<<8;
                if((sie&(1u<<24))!=0) v|=1u<<9;
                if((sie&(1u<<29))!=0) v|=1u<<10;
                if((sie&(1u<<19))!=0) v|=1u<<12;
                if((sie&(1u<<17))!=0) v|=1u<<16;
                if((sie&(1u<<4))!=0) v|=1u<<14;
                if((sie&(1u<<11))!=0) v|=1u<<15;
                if((sie&(1u<<16))!=0) v|=1u<<13;
                if(sof) v|=1u<<17;
                if(regs[0x64/4]!=0) v|=1u<<18;
                if(regs[0x70/4]!=0) v|=1u<<19;
                return v;
            }
        }
        private void UpdateIRQ() { IRQ.Set(((RawInterrupts&regs[0x90/4])|regs[0x94/4])!=0); }
        public void BeginBusReset() { BusReset(true); }
        public void EndBusReset() { BusReset(false); }
        public void BusReset(bool active)
        {
            resetting=active;
            if(active)
            {
                Epoch++; ClearTransfers(); regs[0]=0; regs[0x60/4]=0; regs[0x64/4]=0; regs[0x68/4]=0;
                // Reset ownership, not the 4KB payload memory.
                for(int i=0;i<32;i++) for(int half=0;half<2;half++) { int o2=0x80+4*i+2*half; WriteRam16(o2,(ushort)(ReadRam16(o2)&~0x0400)); }
                sie=1u<<19; sof=false; frame=0;
            }
            Log(active?"bus reset SE0":"bus reset release J"); UpdateIRQ();
        }
        public void FrameTick() { if(Attached&&!resetting) { frame=(frame+1)&0x7ff; sof=true; UpdateIRQ(); } }
        public void SendSetup(byte[] data) { SubmitSetup(data); }
        public void SubmitSetup(byte[] data)
        {
            if(data.Length!=8) throw new ArgumentException("SETUP must contain 8 bytes");
            if(!Attached||resetting) throw new InvalidOperationException("SETUP while detached/reset");
            for(int i=0;i<8;i++) WriteRam8(i,data[i]);
            for(int i=0;i<2;i++) { completions[i].Clear(); selectors[i]=0; for(int half=0;half<2;half++) { int o2=0x80+4*i+2*half; WriteRam16(o2,(ushort)(ReadRam16(o2)&~0x0c00)); } }
            regs[0x68/4]=0; sie|=1u<<17; Log("SETUP "+BitConverter.ToString(data)); UpdateIRQ();
        }
        public bool TryIn(int ep,int expectedPid,out byte[] data) { data=null; return Transfer(ep,true,expectedPid,ref data); }
        public bool TryOut(int ep,int expectedPid,byte[] data) { return Transfer(ep,false,expectedPid,ref data); }
        private bool Transfer(int ep,bool input,int pid,ref byte[] data)
        {
            if(ep<0||ep>15) throw new ArgumentOutOfRangeException("ep");
            int slot=2*ep+(input?0:1); uint bit=1u<<slot;
            if(!Attached||resetting||(regs[0x60/4]&bit)!=0) return false;
            uint control=ep==0?0:ReadRam32(8+8*(ep-1)+(input?0:4));
            if(ep!=0&&(control&0x80000000)==0) return false;
            bool dual=ep==0?(regs[0x4c/4]&(1u<<30))!=0:(control&0x40000000)!=0;
            bool perBuffer=ep==0?(regs[0x4c/4]&(1u<<29))!=0:(control&(1u<<29))!=0;
            bool perDouble=ep==0?(regs[0x4c/4]&(1u<<28))!=0:(control&(1u<<28))!=0;
            if(dual && perDouble && !perBuffer) throw new InvalidOperationException("per-double-only completion mode is not implemented");
            int offset=0x80+slot*4; uint both=ReadRam32(offset);
            if((both&0x1000)!=0) selectors[slot]=0; // Never write the read word back: the firmware may have armed the sibling/this buffer after our read (see stage-10 report).
            int buffer=dual?selectors[slot]:0;
            // Do not consume a rearmed buffer until its previous completion was acknowledged.
            if(completions[slot].Contains(buffer)) return false;
            uint b=(both>>(16*buffer))&0xffff;
            if((b&0x400)==0) return false;
            if((b&0x800)!=0 && (ep!=0 || (regs[0x68/4]&bit)!=0)) throw new InvalidOperationException("STALL ep="+ep+" in="+input);
            if(((b&0x8000)!=0)!=input) throw new InvalidOperationException("FULL ownership mismatch");
            if(pid>=0 && ((b>>13)&1)!=(uint)pid) throw new InvalidOperationException("PID mismatch ep="+ep+" expected="+pid+" control="+b.ToString("x"));
            int length=(int)(b&1023), address=(ep==0?0x100:(int)(control&0xffff))+64*buffer;
            if(length>64 || address<0x100 || address+length>4096) throw new InvalidOperationException("unsupported packet size/DPRAM range");
            if(input) { data=new byte[length]; for(int i=0;i<length;i++) data[i]=ReadRam8(address+i); b&=~0x8400u; }
            else { if(data.Length>length) throw new InvalidOperationException("OUT exceeds capacity"); for(int i=0;i<data.Length;i++) WriteRam8(address+i,data[i]); b=(b&~0x7ffu)|0x8000u|(uint)data.Length; }
            if(buffer==0) b&=~0x1000u; // RESET_BUF_SELECT is cleared at end of transfer (datasheet).
            WriteRam16(offset+2*buffer,(ushort)b); // Only the transferred buffer's halfword: a full-word write-back can clobber a sibling arm that landed after our read.
            bool interrupt=ep==0?(regs[0x4c/4]&(1u<<29))!=0 || (dual&&buffer==1&&(regs[0x4c/4]&(1u<<28))!=0):(control&(1u<<29))!=0 || (dual&&buffer==1&&(control&(1u<<28))!=0);
            if(interrupt) completions[slot].Enqueue(buffer);
            selectors[slot]=dual?1-buffer:0;
            Log("packet ep="+ep+" "+(input?"IN":"OUT")+" buffer="+buffer+" length="+data.Length+" pid="+((b>>13)&1)); UpdateIRQ(); return true;
        }
        // Destructive isolated-controller contract test. Run before loading firmware, then machine reset.
        public string SelfTest()
        {
            Reset();
            WriteDoubleWord(0x40,1); WriteDoubleWord(0x2040,2); Check(ReadDoubleWord(0x40)==3,"RW SET");
            WriteDoubleWord(0x1040,2); Check(ReadDoubleWord(0x40)==1,"RW XOR");
            WriteDoubleWord(0x3040,1); Check(ReadDoubleWord(0x40)==0,"RW CLEAR");
            WriteDoubleWord(0x94,16); Check(IRQ.IsSet,"INTF bypasses INTE"); WriteDoubleWord(0x3094,16); Check(!IRQ.IsSet,"INTF clear");
            WriteDoubleWord(0x40,1); WriteDoubleWord(0x74,9); WriteDoubleWord(0x78,12); WriteDoubleWord(0x4c,0x20010000);
            BeginBusReset(); EndBusReset(); SubmitSetup(new byte[8]);
            WriteDoubleWord(0x90,0x1000); Check(IRQ.IsSet,"INTE normal level assert");
            WriteDoubleWord(0x2090,0x10000); Check(ReadDoubleWord(0x90)==0x11000,"INTE SET");
            WriteDoubleWord(0x1090,0x1000); Check(ReadDoubleWord(0x90)==0x10000 && IRQ.IsSet,"INTE XOR level preserved");
            WriteDoubleWord(0x3090,0x10000); Check(ReadDoubleWord(0x90)==0 && !IRQ.IsSet,"INTE CLEAR level deassert");
            WriteDoubleWord(0x3050,1u<<19); Check((ReadDoubleWord(0x8c)&0x10000)!=0 && (ReadDoubleWord(0x8c)&0x1000)==0,"WC CLEAR preserves SETUP");
            WriteDoubleWord(0x50,1u<<17); Check((ReadDoubleWord(0x8c)&0x10000)==0,"WC normal");
            FrameTick(); Check((ReadDoubleWord(0x8c)&0x20000)!=0,"SOF latch"); ReadDoubleWord(0x48); Check((ReadDoubleWord(0x8c)&0x20000)==0,"SOF read clear");
            byte[] packet;
            WriteRam32(0x80,0xa003); Check(!TryIn(0,1,out packet),"AVAILABLE required");
            WriteRam8(0x100,0x12); WriteRam8(0x101,0x34); WriteRam8(0x102,0x56);
            WriteRam32(0x80,0xa403); Check(TryIn(0,1,out packet)&&packet.Length==3&&packet[1]==0x34,"IN payload");
            Check((ReadRam32(0x80)&0x8400)==0,"IN ownership clear"); WriteDoubleWord(0x58,1);
            Check(!TryIn(0,1,out packet),"no duplicate completion without rearm");
            WriteRam32(0x84,0x2404); Check(TryOut(0,1,new byte[] { 0x71,0x72 }),"OUT payload");
            Check((ReadRam32(0x84)&0x87ff)==0x8002 && ReadRam8(0x101)==0x72,"OUT actual length and payload");
            WriteDoubleWord(0x58,2);
            WriteRam32(0x84,0x2400); Check(TryOut(0,1,new byte[0]),"OUT ZLP"); Check((ReadRam32(0x84)&0x87ff)==0x8000,"OUT FULL length ownership");
            WriteRam32(0x80,0xa400); Check(TryIn(0,1,out packet)&&packet.Length==0,"IN ZLP");
            WriteDoubleWord(0x3058,1); Check(ReadDoubleWord(0x58)==2,"BUFF WC CLEAR preserves other EP"); WriteDoubleWord(0x58,2);
            // EP1 IN double buffer, preserve halfwords and expose buffer1 after first ACK.
            WriteRam32(8,0xe8000180); WriteRam32(0x88,0xa4018401); WriteRam8(0x180,0x41); WriteRam8(0x1c0,0x42);
            Check(TryIn(1,0,out packet)&&packet[0]==0x41,"double buffer0"); Check((ReadRam32(0x88)>>16)==0xa401,"buffer1 halfword preserved");
            Check(TryIn(1,1,out packet)&&packet[0]==0x42,"double buffer1"); Check((ReadDoubleWord(0x5c)&4)==0,"first completion buffer0");
            WriteDoubleWord(0x58,4); Check((ReadDoubleWord(0x58)&4)!=0 && (ReadDoubleWord(0x5c)&4)!=0,"second completion reassert buffer1");
            WriteDoubleWord(0x58,4); Check((ReadDoubleWord(0x58)&4)==0,"second completion ack");
            WriteRam32(0x88,0x8401); WriteDoubleWord(0x60,4); Check((ReadDoubleWord(0x64)&4)!=0 && !TryIn(1,0,out packet),"ABORT done blocks transfer");
            WriteDoubleWord(0x3064,4); Check(ReadDoubleWord(0x64)==0,"ABORT WC");
            // CPU narrow writes are handled by the original MappedMemory, shared with this model.
            machine.GetSystemBus(this).WriteWord(Dpram+0x88,0x1234); WriteRam8(0x8a,0x56);
            Check((ReadRam32(0x88)&0xffffff)==0x561234,"byte and halfword DPRAM");
            Reset(); Log("SELFTEST PASS MMIO aliases/WC/IRQ/SOF and packet ownership/ZLP/double/abort/narrow writes");
            return "PASS";
        }
        private static void Check(bool condition,string name) { if(!condition) throw new InvalidOperationException("SELFTEST FAIL: "+name); }
        public string Snapshot() { return string.Format("ADDR={0} SIE={1:x8} BUFF={2:x8} INTE={3:x8} INTR={4:x8} EP0IN={5:x8} EP0OUT={6:x8}",Address,ReadDoubleWord(0x50),BufferStatus,regs[0x90/4],RawInterrupts,ReadRam32(0x80),ReadRam32(0x84)); }
        private byte ReadRam8(int offset) { return machine.GetSystemBus(this).ReadByte(Dpram+(ulong)offset); }
        private void WriteRam8(int offset,byte value) { machine.GetSystemBus(this).WriteByte(Dpram+(ulong)offset,value); }
        private ushort ReadRam16(int offset) { return machine.GetSystemBus(this).ReadWord(Dpram+(ulong)offset); }
        private void WriteRam16(int offset,ushort value) { machine.GetSystemBus(this).WriteWord(Dpram+(ulong)offset,value); }
        private uint ReadRam32(int offset) { return machine.GetSystemBus(this).ReadDoubleWord(Dpram+(ulong)offset); }
        private void WriteRam32(int offset,uint value) { machine.GetSystemBus(this).WriteDoubleWord(Dpram+(ulong)offset,value); }
        private const ulong Dpram=0x50100000;
        private readonly IMachine machine;
        private readonly LimitTimer timer;
        private readonly uint[] regs=new uint[0x9c/4];
        private readonly Queue<int>[] completions=new Queue<int>[32];
        private readonly int[] selectors=new int[32];
        private uint sie,frame;
        private bool sof,resetting;
        private ulong ticks;
    }
}
