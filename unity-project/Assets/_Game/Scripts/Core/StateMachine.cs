using System;
using System.Collections.Generic;

namespace SuUstasi.Core
{
    /// <summary>
    /// Small explicit-transition state machine. Illegal transitions throw, so flow bugs
    /// surface immediately (used by the GameStateMachine MonoBehaviour, tested headless).
    /// </summary>
    public sealed class StateMachine<TState> where TState : struct, Enum
    {
        private readonly Dictionary<TState, HashSet<TState>> _allowed = new Dictionary<TState, HashSet<TState>>();
        private readonly Dictionary<TState, Action> _onEnter = new Dictionary<TState, Action>();
        private readonly Dictionary<TState, Action> _onExit = new Dictionary<TState, Action>();

        public TState Current { get; private set; }
        public TState Previous { get; private set; }
        public event Action<TState, TState> Changed;

        public StateMachine(TState initial)
        {
            Current = initial;
            Previous = initial;
        }

        public StateMachine<TState> Allow(TState from, params TState[] to)
        {
            if (!_allowed.TryGetValue(from, out var set)) _allowed[from] = set = new HashSet<TState>();
            foreach (var t in to) set.Add(t);
            return this;
        }

        public StateMachine<TState> OnEnter(TState state, Action action) { _onEnter[state] = action; return this; }
        public StateMachine<TState> OnExit(TState state, Action action) { _onExit[state] = action; return this; }

        public bool CanGo(TState to) => _allowed.TryGetValue(Current, out var set) && set.Contains(to);

        public void Go(TState to)
        {
            if (!CanGo(to)) throw new InvalidOperationException("Illegal transition " + Current + " -> " + to);
            var from = Current;
            if (_onExit.TryGetValue(from, out var exit)) exit();
            Previous = from;
            Current = to;
            if (_onEnter.TryGetValue(to, out var enter)) enter();
            Changed?.Invoke(from, to);
        }

        public bool TryGo(TState to)
        {
            if (!CanGo(to)) return false;
            Go(to);
            return true;
        }
    }
}
