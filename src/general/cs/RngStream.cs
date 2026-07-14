using System;

namespace Buckminster;

// Seeded PRNG stream: PCG32-setseq (O'Neill's pcg32, the setseq variant -- state advances by LCG, output is an xorshift-rotate of the OLD state, and the stream id selects the increment). Pure 64-bit integer math, so identical outputs on every target including wasm (pinned by the reference-sequence test); two ulongs of state, readable (State/StreamIncrement) so owners can serialize or snapshot a stream -- when the determinism-and-cloning era arrives (see PLAN.md's deferred entries), stream state lives in sim state and clones carry their randomness with them. OS entropy never touches this type -- seeds come from the caller.
public sealed class RngStream
{
    private const ulong Multiplier = 6364136223846793005;

    private ulong state;
    private readonly ulong increment;

    // The two words that fully determine the stream: same (State, StreamIncrement) means same future draws.
    public ulong State
    {
        get { return state; }
    }

    public ulong StreamIncrement
    {
        get { return increment; }
    }

    public RngStream(ulong seed, ulong streamId)
    {
        // The reference's pcg32_srandom: the increment is (streamId << 1) | 1 (must be odd for the LCG to be full-period), and the seed is folded in with two advances so nearby seeds don't produce nearby states.
        increment = (streamId << 1) | 1;
        state = 0;
        Step();
        state += seed;
        Step();
    }

    // The PCG reference's output step: 32 uniformly random bits, xorshift-rotated from the state BEFORE the advance.
    public uint NextUInt32()
    {
        ulong oldState = state;
        Step();
        uint xorShifted = (uint)(((oldState >> 18) ^ oldState) >> 27);
        int rotation = (int)(oldState >> 59);
        return (xorShifted >> rotation) | (xorShifted << ((-rotation) & 31));
    }

    // Two draws, high word first -- pinned by test; changing draw order is a determinism event.
    public ulong NextUInt64()
    {
        ulong high = NextUInt32();
        ulong low = NextUInt32();
        return (high << 32) | low;
    }

    // [0, 1), 53 bits of precision from one NextUInt64 (the double mantissa's full width).
    public double NextDouble()
    {
        return (NextUInt64() >> 11) * (1.0 / (1ul << 53));
    }

    private void Step()
    {
        state = state * Multiplier + increment;
    }
}
