using System.Collections.Generic;
using SuUstasi.Gameplay;
using UnityEngine;

namespace SuUstasi.Runtime
{
    public enum Sfx
    {
        Bonk, Drain, Crack, Ice, Unlock, Rotate, Leak, Hammer, Win, Lose, Coin, Click,
    }

    /// <summary>Plays the procedural clips from <see cref="SfxRecipes"/>; water clips are cached per combo step (rising pitch).</summary>
    public sealed class SfxPlayer : MonoBehaviour
    {
        private const int Voices = 6;
        private readonly Dictionary<Sfx, AudioClip> _clips = new Dictionary<Sfx, AudioClip>();
        private readonly AudioClip[] _water = new AudioClip[8];
        private AudioSource[] _sources;
        private int _next;

        public bool Enabled { get; set; } = true;

        private void Awake()
        {
            _sources = new AudioSource[Voices];
            for (int i = 0; i < Voices; i++)
            {
                _sources[i] = gameObject.AddComponent<AudioSource>();
                _sources[i].playOnAwake = false;
                _sources[i].spatialBlend = 0f;
            }
            Add(Sfx.Bonk, SfxRecipes.Bonk());
            Add(Sfx.Drain, SfxRecipes.Drain());
            Add(Sfx.Crack, SfxRecipes.Crack());
            Add(Sfx.Ice, SfxRecipes.Ice());
            Add(Sfx.Unlock, SfxRecipes.Unlock());
            Add(Sfx.Rotate, SfxRecipes.Rotate());
            Add(Sfx.Leak, SfxRecipes.Leak());
            Add(Sfx.Hammer, SfxRecipes.Hammer());
            Add(Sfx.Win, SfxRecipes.Win());
            Add(Sfx.Lose, SfxRecipes.Lose());
            Add(Sfx.Coin, SfxRecipes.Coin());
            Add(Sfx.Click, SfxRecipes.Click());
            for (int i = 0; i < _water.Length; i++) _water[i] = MakeClip("water_" + (i + 1), SfxRecipes.Water(i + 1));
        }

        private void Add(Sfx id, float[] samples) => _clips[id] = MakeClip(id.ToString(), samples);

        private static AudioClip MakeClip(string name, float[] samples)
        {
            var clip = AudioClip.Create("sfx_" + name, samples.Length, 1, SfxRecipes.SampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        public void Play(Sfx id, float volume = 1f)
        {
            if (Enabled && _clips.TryGetValue(id, out var clip)) Voice().PlayOneShot(clip, volume);
        }

        /// <summary>Successful exit; combo 1, 2, 3… climbs 2 semitones each (capped).</summary>
        public void Water(int combo)
        {
            if (!Enabled) return;
            Voice().PlayOneShot(_water[Mathf.Clamp(combo - 1, 0, _water.Length - 1)]);
        }

        private AudioSource Voice()
        {
            var s = _sources[_next];
            _next = (_next + 1) % _sources.Length;
            return s;
        }
    }
}
