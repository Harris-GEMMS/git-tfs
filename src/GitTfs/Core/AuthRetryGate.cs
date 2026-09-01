using System.Runtime.ExceptionServices;

namespace GitTfs.Core
{
    /// <summary>
    /// Serializes re-authentication attempts against a server URI so concurrent callers hitting the
    /// same stale session share one interactive prompt instead of each popping their own: a caller
    /// that arrives while another is already authenticating for the same URI waits for that attempt
    /// and shares its outcome, rather than starting its own.
    ///
    /// A successful outcome is remembered per-URI only for callers that pass
    /// <c>forceReauthenticate: false</c> (an opportunistic "are we still authenticated?" check) - it is
    /// never used to skip a caller that passes <c>forceReauthenticate: true</c>, since that flag means
    /// the caller just observed a fresh authentication failure for this URI and a stale cached success
    /// would silently mask it. Once an attempt (successful or not) is no longer in flight, it is not
    /// cached beyond that: a later re-authentication - e.g. the session going stale again, hours into
    /// an unattended fetch - always gets its own fresh attempt.
    /// </summary>
    public class AuthRetryGate
    {
        private readonly object _lock = new object();
        private readonly Dictionary<Uri, InFlightAttempt> _inFlight = new Dictionary<Uri, InFlightAttempt>();
        private readonly HashSet<Uri> _lastKnownGood = new HashSet<Uri>();

        public void Execute(Uri uri, bool forceReauthenticate, Action authenticate)
        {
            InFlightAttempt attempt;
            bool isOwner;

            lock (_lock)
            {
                if (_inFlight.TryGetValue(uri, out attempt))
                {
                    isOwner = false;
                }
                else if (!forceReauthenticate && _lastKnownGood.Contains(uri))
                {
                    return;
                }
                else
                {
                    attempt = new InFlightAttempt();
                    _inFlight[uri] = attempt;
                    _lastKnownGood.Remove(uri);
                    isOwner = true;
                }
            }

            if (!isOwner)
            {
                attempt.Wait();
                return;
            }

            try
            {
                authenticate();
                lock (_lock)
                {
                    _lastKnownGood.Add(uri);
                }
                attempt.Succeed();
            }
            catch (Exception ex)
            {
                attempt.Fail(ex);
                throw;
            }
            finally
            {
                lock (_lock)
                {
                    _inFlight.Remove(uri);
                }
            }
        }

        private class InFlightAttempt
        {
            private readonly ManualResetEventSlim _done = new ManualResetEventSlim();
            private Exception _exception;

            public void Succeed() => _done.Set();

            public void Fail(Exception ex)
            {
                _exception = ex;
                _done.Set();
            }

            public void Wait()
            {
                _done.Wait();
                if (_exception != null)
                {
                    ExceptionDispatchInfo.Capture(_exception).Throw();
                }
            }
        }
    }
}
