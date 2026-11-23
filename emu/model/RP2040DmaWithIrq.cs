// Independent extension of the vendored RPDMA model (emu/refs/Renode_RP2040, MIT, kept
// byte-identical), written from the RP2040 datasheet's DMA interrupt registers.
// No firmware or library code is incorporated.
//
// The vendored model keeps each channel's completion flag (readable in INTR) but has no
// INTE0/INTE1/INTF0/INTF1 storage, reads INTS0/INTS1 as 0 and drives no interrupt line.
// Firmware that waits for DMA_IRQ_0/1 therefore never sees a transfer finish; NeoPixelBus,
// which drives the course board's status LED, waits for DMA_IRQ_1 before every Show().
// This subclass adds that block:
//     INTS<n> = (INTR | INTF<n>) & INTE<n>        IRQ<n> = (INTS<n> != 0)
// Transfers, INTR and the write-1-to-clear of INTR/INTS stay in the base model.
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.DMA
{
    // IGPIOReceiver is listed again so that DREQ inputs reach OnGPIO below (interface re-implementation).
    public class RP2040DmaWithIrq : RPDMA, IGPIOReceiver
    {
        public RP2040DmaWithIrq(IMachine machine, int numberOfChannels, ulong address)
            : base(numberOfChannels, machine, address)
        {
            for(var n = 0; n < 2; n++)
            {
                RegistersCollection.DefineRegister(IntEOffset + n * IrqBlockStride)
                    .WithValueField(0, 16, out inte[n], writeCallback: (_, __) => UpdateInterrupts(), name: "INTE" + n)
                    .WithReservedBits(16, 16);
                RegistersCollection.DefineRegister(IntFOffset + n * IrqBlockStride)
                    .WithValueField(0, 16, out intf[n], writeCallback: (_, __) => UpdateInterrupts(), name: "INTF" + n)
                    .WithReservedBits(16, 16);
            }
            // Safety net for a flag the events below cannot see in time: the base model raises a
            // CHAIN_TO channel's flag one synced state after the chain trigger itself.
            poll = new LimitTimer(machine.ClockSource, 1000000, this, "DMA IRQ poll", limit: PollMicroseconds,
                direction: Direction.Ascending, enabled: true, workMode: WorkMode.Periodic, eventEnabled: true);
            poll.LimitReached += UpdateInterrupts;
            IRQ0 = new GPIO();
            IRQ1 = new GPIO();
            initialized = true;
        }

        public override void Reset()
        {
            base.Reset();
            if(!initialized)
            {
                return; // the base constructor calls Reset() before this class is set up
            }
            poll.Reset();
            UpdateInterrupts();
        }

        public override uint ReadDoubleWord(long offset)
        {
            switch(offset)
            {
                case IntSOffset:
                    return Status(0);
                case IntSOffset + IrqBlockStride:
                    return Status(1);
                default:
                    return base.ReadDoubleWord(offset);
            }
        }

        public override void WriteDoubleWord(long offset, uint value)
        {
            base.WriteDoubleWord(offset, value);
            // A write-1-to-clear of INTR/INTS, or a CTRL null trigger with IRQ_QUIET, changes the
            // flags now. A transfer started by this write raises its flag from a deferred action.
            UpdateInterrupts();
            UpdateInNearestSyncedState();
        }

        public new void OnGPIO(int number, bool value)
        {
            base.OnGPIO(number, value);
            // A paced beat that completes a channel raises the flag from a deferred action; one
            // queued after it (same synced state, queued later) sees the flag.
            UpdateInNearestSyncedState();
        }

        public GPIO IRQ0 { get; }
        public GPIO IRQ1 { get; }

        // For the monitor: sysbus.dma InterruptState
        public string InterruptState()
        {
            return string.Format("INTR=0x{0:X4} INTE0=0x{1:X4} INTS0=0x{2:X4} IRQ0={3} INTE1=0x{4:X4} INTS1=0x{5:X4} IRQ1={6}",
                base.ReadDoubleWord(IntROffset), inte[0].Value, Status(0), IRQ0.IsSet, inte[1].Value, Status(1), IRQ1.IsSet);
        }

        private uint Status(int n)
        {
            var raw = base.ReadDoubleWord(IntROffset);
            return (uint)((raw | intf[n].Value) & inte[n].Value);
        }

        private void UpdateInterrupts()
        {
            if(!initialized)
            {
                return;
            }
            lock(irqLock)
            {
                IRQ0.Set(Status(0) != 0);
                IRQ1.Set(Status(1) != 0);
            }
        }

        private void UpdateInNearestSyncedState()
        {
            machine.LocalTimeSource.ExecuteInNearestSyncedState(_ => UpdateInterrupts());
        }

        private readonly IValueRegisterField[] inte = new IValueRegisterField[2];
        private readonly IValueRegisterField[] intf = new IValueRegisterField[2];
        private readonly LimitTimer poll;
        private readonly object irqLock = new object();
        private readonly bool initialized;

        private const long IntROffset = 0x400;
        private const long IntEOffset = 0x404;
        private const long IntFOffset = 0x408;
        private const long IntSOffset = 0x40c;
        private const long IrqBlockStride = 0x10;
        private const ulong PollMicroseconds = 1000;
    }
}
