// Independent model written from the RP2040 datasheet (PIO FSTAT, DMA DREQ numbering).
// No firmware or library code is incorporated.
//
// The vendored PIO model (emu/refs/Renode_RP2040: rp2040_pio.cs + libpiosim) raises no DMA
// requests, so a DMA channel paced by a PIO TX DREQ never moves. On hardware a TX DREQ is
// asserted while that state machine's TX FIFO is not full. This bridge stands in for those
// signals: every periodMicroseconds of virtual time it reads FSTAT of both PIO blocks and,
// for each state machine whose TX FIFO is not full, gives the DMA one request on that
// state machine's DREQ (PIO0 TX0-3 = 0-3, PIO1 TX0-3 = 8-11).
//
// One request is one Toggle() of the output, not a high/low pulse: the vendored RPDMA moves
// a paced channel one beat per OnGPIO call whatever the level, and Renode's GPIO notifies
// only on a change, so a pulse would be two beats. For the same reason Reset() leaves the
// levels alone. Nothing is driven by an idle DMA: a disabled or unpaced channel ignores it.
// RX DREQs (4-7, 12-15) are not modelled; the course firmware reads no PIO RX FIFO by DMA.
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.DMA
{
    public class RP2040PioDreqBridge : IPeripheral, INumberedGPIOOutput
    {
        public RP2040PioDreqBridge(IMachine machine, IDoubleWordPeripheral pio0, IDoubleWordPeripheral pio1,
            ulong periodMicroseconds = 1000)
        {
            pios = new[] { pio0, pio1 };
            var connections = new Dictionary<int, IGPIO>();
            for(var pio = 0; pio < pios.Length; pio++)
            {
                for(var sm = 0; sm < StateMachines; sm++)
                {
                    var output = new GPIO();
                    outputs[pio, sm] = output;
                    connections[TxDreq(pio, sm)] = output;
                }
            }
            Connections = new ReadOnlyDictionary<int, IGPIO>(connections);
            timer = new LimitTimer(machine.ClockSource, 1000000, this, "PIO TX DREQ", limit: periodMicroseconds,
                direction: Direction.Ascending, enabled: true, workMode: WorkMode.Periodic, eventEnabled: true);
            timer.LimitReached += Tick;
        }

        public void Reset()
        {
            timer.Reset();
        }

        public IReadOnlyDictionary<int, IGPIO> Connections { get; }

        // Requests given since the machine was created (all state machines); for the monitor.
        public ulong Requests { get; private set; }

        private void Tick()
        {
            for(var pio = 0; pio < pios.Length; pio++)
            {
                var fstat = pios[pio].ReadDoubleWord(FstatOffset);
                for(var sm = 0; sm < StateMachines; sm++)
                {
                    if((fstat & (1u << (FstatTxFullShift + sm))) == 0)
                    {
                        outputs[pio, sm].Toggle();
                        Requests++;
                    }
                }
            }
        }

        // DREQ_PIO0_TX0 = 0, DREQ_PIO1_TX0 = 8 (RP2040 datasheet, DMA DREQ table)
        private static int TxDreq(int pio, int sm)
        {
            return pio * 8 + sm;
        }

        private readonly IDoubleWordPeripheral[] pios;
        private readonly GPIO[,] outputs = new GPIO[2, StateMachines];
        private readonly LimitTimer timer;

        private const int StateMachines = 4;
        private const long FstatOffset = 0x4;
        private const int FstatTxFullShift = 16; // FSTAT.TXFULL is bits 19:16, one per state machine
    }
}
