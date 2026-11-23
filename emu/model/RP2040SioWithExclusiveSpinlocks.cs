// Independent extension of the vendored RP2040SIO model (emu/refs/Renode_RP2040, MIT, kept
// byte-identical), written from the RP2040 datasheet's SIO spinlock semantics:
//     read  SPINLOCKn: atomically claim the lock; return (1 << n) if it was free, else 0
//     write SPINLOCKn: release the lock (any value, from either core)
// A claimed lock cannot be claimed again, not even by the core that holds it.
//
// The vendored model deviates from that in three ways (rp2040_sio.cs, the SPINLOCK_0..31
// register definitions): the claim is a check-and-set on a plain int[] with no synchronization,
// so the two CPU threads can both observe "free" and both claim; a read by the owning core
// claims again instead of returning 0; and a write releases only when it comes from the owning
// core. Both cores of the course firmware share TinyUSB and the pico-sdk alarm pool under SIO
// spinlocks, so a broken claim can corrupt the protected state and lose wakeups.
//
// This subclass intercepts the SPINLOCK0..31 offsets (0x100-0x17c) before the base register
// collection and implements the hardware semantics under one lock. Everything else (GPIO,
// inter-core FIFO, hardware divider) stays in the base model. The atomic SET/CLR/XOR alias
// regions the vendored base registers have no counterpart on real SIO hardware and are not
// intercepted; the firmware does not use them for spinlocks.
using System;
using System.Text;
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.GPIOPort;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class RP2040SioWithExclusiveSpinlocks : RP2040SIO
    {
        public RP2040SioWithExclusiveSpinlocks(Machine machine, RP2040GPIO gpio, RP2040GPIO gpioQspi, ulong address)
            : base(machine, gpio, gpioQspi, address)
        {
            owners = new int[SpinlockCount];
            for(var i = 0; i < owners.Length; i++)
            {
                owners[i] = Free;
            }
            initialized = true;
        }

        public override void Reset()
        {
            base.Reset();
            if(!initialized)
            {
                return; // the base constructor calls Reset() before this class is set up
            }
            lock(spinlockMutex)
            {
                for(var i = 0; i < owners.Length; i++)
                {
                    owners[i] = Free;
                }
            }
        }

        public override uint ReadDoubleWord(long offset)
        {
            int id;
            if(IsSpinlockOffset(offset, out id))
            {
                return Claim(id);
            }
            return base.ReadDoubleWord(offset);
        }

        public override void WriteDoubleWord(long offset, uint value)
        {
            int id;
            if(IsSpinlockOffset(offset, out id))
            {
                Release(id);
                return;
            }
            base.WriteDoubleWord(offset, value);
        }

        // For the monitor: sysbus.sio SpinlockState
        public string SpinlockState()
        {
            lock(spinlockMutex)
            {
                var claimed = 0u;
                var byCore = new StringBuilder();
                for(var i = 0; i < owners.Length; i++)
                {
                    if(owners[i] != Free)
                    {
                        claimed |= 1u << i;
                        byCore.AppendFormat(" L{0}=core{1}", i, owners[i]);
                    }
                }
                return string.Format("claimed=0x{0:X8}{1}", claimed, byCore.Length == 0 ? " (none)" : byCore.ToString());
            }
        }

        private static bool IsSpinlockOffset(long offset, out int id)
        {
            id = (int)((offset - SpinlockBaseOffset) / 4);
            return offset >= SpinlockBaseOffset && offset <= SpinlockLastOffset && (offset & 3) == 0;
        }

        private uint Claim(int id)
        {
            lock(spinlockMutex)
            {
                if(owners[id] != Free)
                {
                    return 0;
                }
                owners[id] = CurrentCore();
                return 1u << id;
            }
        }

        private void Release(int id)
        {
            lock(spinlockMutex)
            {
                owners[id] = Free;
            }
        }

        private int CurrentCore()
        {
            try
            {
                var cpu = machine.SystemBus.GetCurrentCPU();
                if(cpu == null)
                {
                    return ExternalAccessor;
                }
                return machine.SystemBus.GetCPUSlot(cpu);
            }
            catch(Exception)
            {
                // Register accesses from outside a CPU context (e.g. the Renode monitor)
                // have no current CPU; the vendored callbacks throw there. Claim/release do
                // not depend on the accessor's identity, so a placeholder is enough.
                return ExternalAccessor;
            }
        }

        private readonly int[] owners;
        private readonly object spinlockMutex = new object();
        private readonly bool initialized;

        private const int SpinlockCount = 32;
        private const long SpinlockBaseOffset = 0x100;
        private const long SpinlockLastOffset = 0x17c;
        private const int Free = -1;
        private const int ExternalAccessor = -2;
    }
}
