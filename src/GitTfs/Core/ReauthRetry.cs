namespace GitTfs.Core
{
    /// <summary>
    /// Retries an operation that fails due to an authentication problem, re-authenticating first. Takes
    /// a predicate rather than a concrete exception type so this stays free of any VCS-specific
    /// dependency; callers (e.g. GitTfs.VsCommon) supply their own exception check.
    /// </summary>
    public static class ReauthRetry
    {
        public static T Do<T>(Func<T> action, Func<Exception, bool> isAuthFailure, Action reauthenticate)
        {
            while (true)
            {
                try
                {
                    return action();
                }
                catch (Exception ex) when (isAuthFailure(ex))
                {
                    reauthenticate();
                }
            }
        }
    }
}
