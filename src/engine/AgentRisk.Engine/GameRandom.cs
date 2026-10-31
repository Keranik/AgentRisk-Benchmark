namespace AgentRisk.Engine;

/// <summary>
/// The engine's only source of randomness. Dice, deck shuffles and
/// Rebel placements all draw through this interface, so a whole game is
/// reproducible from its seed and every die face can be audited after the fact.
/// </summary>
public interface IGameRandom
{
    /// <summary>Returns an integer in [0, maxExclusive).</summary>
    int NextInt(int maxExclusive);
}

/// <summary>
/// Deterministic seeded generator (SplitMix64). The same seed always produces
/// the same stream, which is what makes seeded games replayable.
/// </summary>
public sealed class SeededRandom : IGameRandom
{
    private ulong _state;

    public SeededRandom(long seed) => _state = unchecked((ulong)seed);

    public int NextInt(int maxExclusive)
    {
        if (maxExclusive <= 0) throw new ArgumentOutOfRangeException(nameof(maxExclusive));
        // SplitMix64 step: one 64-bit draw per call keeps the stream simple to audit.
        _state += 0x9E3779B97F4A7C15UL;
        ulong z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (int)(z % (ulong)maxExclusive);
    }
}

/// <summary>
/// A script of exact draws for tests. Each call dequeues the next scripted
/// value; running dry (or scripting an out-of-range value) fails loudly rather
/// than silently falling back to real randomness, so a test can never pass by
/// accident on unscripted dice.
/// </summary>
public sealed class ScriptedRandom : IGameRandom
{
    private readonly Queue<int> _script;

    public ScriptedRandom(IEnumerable<int> script) => _script = new Queue<int>(script);

    /// <summary>Builds a script from die faces (1-6) as they should come up, in order.</summary>
    public static ScriptedRandom FromDice(params int[] faces) =>
        new(faces.Select(f => f - 1));

    public int Remaining => _script.Count;

    public int NextInt(int maxExclusive)
    {
        if (_script.Count == 0)
            throw new InvalidOperationException("ScriptedRandom exhausted: the game asked for a draw the test did not script.");
        int value = _script.Dequeue();
        if (value < 0 || value >= maxExclusive)
            throw new InvalidOperationException($"Scripted value {value} is out of range for NextInt({maxExclusive}).");
        return value;
    }
}

/// <summary>Dice helpers shared by the engine; a die roll is one RNG draw.</summary>
public static class Dice
{
    public const int DieSides = 6;

    /// <summary>Rolls one die, returning a face in 1..6.</summary>
    public static int Roll(IGameRandom random) => random.NextInt(DieSides) + 1;

    /// <summary>Rolls <paramref name="count"/> dice, faces sorted descending ready for comparison.</summary>
    public static int[] RollMany(IGameRandom random, int count)
    {
        var faces = new int[count];
        for (int i = 0; i < count; i++) faces[i] = Roll(random);
        Array.Sort(faces, (a, b) => b.CompareTo(a));
        return faces;
    }
}
