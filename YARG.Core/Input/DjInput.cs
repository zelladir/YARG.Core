using System;
using System.Collections.Generic;

namespace YARG.Core.Input
{
    // Mode-local IDs. DJ replay payloads remain explicitly unsupported.
    public enum DjAction { Green, Red, Blue, Platter, Crossfader, EffectsDial, Euphoria }
    public enum DjDeck { Left, Right }

    /// <summary>One physical deck's PlasticBand controls; Velocity is the normalized -1..1 axis.</summary>
    public readonly struct DjDeckState
    {
        public readonly bool Green, Red, Blue;
        public readonly float Velocity;
        public DjDeckState(bool green, bool red, bool blue, float velocity)
        {
            Green = green; Red = red; Blue = blue; Velocity = velocity;
        }
    }

    /// <summary>
    /// Boundary for PlasticBand's normalized axes. Select the physical deck without swapping colors.
    /// Submit all controls from one device report with the same timestamp. This adapter is stateless;
    /// reset the engine on disconnect/seek to clear held controls and dial history.
    /// </summary>
    public sealed class DjInputNormalizer
    {
        public DjDeck Deck { get; }
        public bool InvertPlatter { get; }
        public bool InvertCrossfader { get; }

        public DjInputNormalizer(DjDeck deck = DjDeck.Left, bool invertPlatter = false, bool invertCrossfader = false)
        {
            if (!Enum.IsDefined(typeof(DjDeck), deck)) throw new ArgumentOutOfRangeException(nameof(deck));
            Deck = deck; InvertPlatter = invertPlatter; InvertCrossfader = invertCrossfader;
        }

        public static float SignedByteVelocity(float axis)
        {
            if (!Finite(axis)) throw new ArgumentOutOfRangeException(nameof(axis));
            axis = Math.Clamp(axis, -1, 1);
            return axis * (axis < 0 ? 128 : 127);
        }

        public IEnumerable<GameInput> Normalize(double time, DjDeckState left, DjDeckState right,
            float crossfader, float effectsDial, bool euphoria)
        {
            if (double.IsNaN(time) || double.IsInfinity(time)) throw new ArgumentOutOfRangeException(nameof(time));
            var deck = Deck == DjDeck.Left ? left : right;
            // Reject the whole report before emitting any inputs.
            var velocity = SignedByteVelocity(deck.Velocity) * (InvertPlatter ? -1 : 1);
            if (!Finite(crossfader) || !Finite(effectsDial)) throw new ArgumentOutOfRangeException(nameof(crossfader));
            yield return GameInput.Create(time, DjAction.Green, deck.Green);
            yield return GameInput.Create(time, DjAction.Red, deck.Red);
            yield return GameInput.Create(time, DjAction.Blue, deck.Blue);
            yield return GameInput.Create(time, DjAction.Platter, velocity);
            yield return GameInput.Create(time, DjAction.Crossfader, Math.Clamp(crossfader, -1, 1) * (InvertCrossfader ? -1 : 1));
            yield return GameInput.Create(time, DjAction.EffectsDial, effectsDial - (float) Math.Floor(effectsDial));
            yield return GameInput.Create(time, DjAction.Euphoria, euphoria);
        }

        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
