namespace Laveyana.Audio.Playback;

/// <summary>Shares per-deck gains between the controls and the audio worker.</summary>
public sealed class DeckVolumes
{
    private volatile float _gainA = 0.5f;
    private volatile float _gainB = 0.5f;

    /// <summary>Sets a normalized gain for the next render block.</summary>
    public void Set(Deck deck, float gain)
    {
        if (deck is not (Deck.A or Deck.B)) throw new ArgumentOutOfRangeException(nameof(deck));
        if (!float.IsFinite(gain) || gain is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(gain));
        }

        if (deck == Deck.A)
        {
            _gainA = gain;
        }
        else
        {
            _gainB = gain;
        }
    }

    /// <summary>Reads the latest gain without taking a lock on the audio thread.</summary>
    internal float Get(Deck deck) => deck switch
    {
        Deck.A => _gainA, Deck.B => _gainB, _ => throw new ArgumentOutOfRangeException(nameof(deck))
    };
}
