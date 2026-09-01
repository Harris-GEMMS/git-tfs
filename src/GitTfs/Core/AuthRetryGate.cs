namespace GitTfs.Core
{
    /// <summary>
    /// Serializes re-authentication attempts against a server URI and remembers the outcome, so
    /// concurrent callers hitting the same stale session share one interactive prompt instead of
    /// each popping their own: a caller behind the lock returns immediately once another caller has
    /// already succeeded for that URI, and fails immediately (without prompting again) once another
    /// caller has already been declined for it.
    /// </summary>
    public class AuthRetryGate
    {
        private readonly object _lock = new object();
        private Uri _lastAuthenticatedUri;
        private Uri _lastFailedUri;
        private Exception _lastFailure;

        public void Execute(Uri uri, Action authenticate)
        {
            lock (_lock)
            {
                if (Equals(_lastAuthenticatedUri, uri))
                    return;

                if (Equals(_lastFailedUri, uri))
                    throw _lastFailure;

                try
                {
                    authenticate();
                }
                catch (Exception ex)
                {
                    _lastFailedUri = uri;
                    _lastFailure = ex;
                    throw;
                }

                _lastAuthenticatedUri = uri;
                _lastFailedUri = null;
                _lastFailure = null;
            }
        }
    }
}
