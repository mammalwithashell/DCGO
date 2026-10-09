namespace Digimon.Harness
{
    /// <summary>
    /// Decides when a scripted line has stalled on a prompt: one has been
    /// showing for the whole grace while the line's cursor and the game's
    /// progress token both stood still.
    /// </summary>
    /// <remarks>
    /// DCGO's AI branch answers a prompt only by calling the selection RPC the
    /// scripted hook lives in, and an AutoSelect that finds no legal pick can
    /// skip that call (SelectCardEffect with fewer valid cards than its
    /// maxCount). The prompt then waits forever on a selection nobody queues,
    /// the poll loop keeps the heartbeat fresh, and the host only noticed once
    /// the claim outlived its limit -- the EX7-073 scope-neg exam held the
    /// player for 15+ minutes on 2026-10-06. Watched from the poll loop, the
    /// same wedge fails the job within seconds as a prompt mismatch naming the
    /// step. The known skips are closed at their sites; this is the net for
    /// the ones nobody has found yet.
    ///
    /// "A prompt is showing" is DCGO's command text, which every AI-branch
    /// selection opens while it waits; the progress token is any cheap
    /// fingerprint that moves when the game does. Requiring the prompt keeps a
    /// long animation chain from reading as a stall -- the job's own
    /// timeout_seconds is the net for everything else.
    ///
    /// Not armed until the line has answered once: cursor 0 is game start
    /// (Photon, scene load, dealing), which has nothing to do with the line.
    /// Pure (no Unity types), like <see cref="ScriptedLine"/>, so the timing
    /// rule is unit-tested.
    /// </remarks>
    public sealed class StallWatch
    {
        private readonly float _graceSeconds;
        private bool _watching;
        private float _since;
        private int _cursor;
        private string _token;
        private bool _fired;

        /// <param name="graceSeconds">Seconds a prompt may sit still; 0 or
        /// less disables the watch.</param>
        public StallWatch(float graceSeconds)
        {
            _graceSeconds = graceSeconds;
        }

        public float GraceSeconds => _graceSeconds;

        /// <summary>
        /// Feed one observation. True exactly once: the first time a prompt
        /// has been showing continuously for the grace with
        /// <paramref name="cursor"/> and <paramref name="progressToken"/>
        /// unchanged.
        /// </summary>
        public bool Observe(float now, int cursor, string progressToken, bool promptShowing)
        {
            if (_fired || _graceSeconds <= 0f) return false;

            if (cursor <= 0 || !promptShowing)
            {
                _watching = false;
                return false;
            }

            if (!_watching || cursor != _cursor || progressToken != _token)
            {
                _watching = true;
                _since = now;
                _cursor = cursor;
                _token = progressToken;
                return false;
            }

            if (now - _since < _graceSeconds) return false;
            _fired = true;
            return true;
        }
    }
}
