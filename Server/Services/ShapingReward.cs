namespace Imperial2030.Server.Services;

/// <summary>
/// The shaping terms of one training step. They are folded into the step's reward once, scaled by the
/// curriculum; a term added after that could never reach the agent, so <see cref="Add"/> throws instead
/// of dropping it. (Two Investor penalties were computed after the fold for the whole of RL-3's
/// training - logged as applied, never seen by the agent - and nothing observable failed.)
/// </summary>
public sealed class ShapingReward
{
    private float _total;
    private bool _folded;

    public float Total => _total;
    public bool Folded => _folded;

    public void Add(float term)
    {
        if (_folded) throw new InvalidOperationException($"Shaping term {term} added after the fold: it would never reach the agent.");
        _total += term;
    }

    /// <summary>The scaled total, once. The terminal signal (final margin, win/loss) is added to the reward separately and unscaled.</summary>
    public float Fold(float scale)
    {
        if (_folded) throw new InvalidOperationException("Shaping folded twice.");
        _folded = true;
        return _total * scale;
    }
}
