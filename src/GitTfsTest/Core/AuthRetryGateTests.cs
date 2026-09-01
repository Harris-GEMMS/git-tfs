using GitTfs.Core;
using Xunit;

namespace GitTfsTest.Core
{
    public class AuthRetryGateTests
    {
        private static readonly Uri Uri1 = new Uri("https://tfs.example.com/collection1");
        private static readonly Uri Uri2 = new Uri("https://tfs.example.com/collection2");

        [Fact]
        public void Execute_CallsAuthenticate_WhenNotPreviouslySucceeded()
        {
            var gate = new AuthRetryGate();
            var called = false;

            gate.Execute(Uri1, forceReauthenticate: false, authenticate: () => called = true);

            Assert.True(called);
        }

        [Fact]
        public void Execute_PropagatesException_WhenAuthenticateThrows()
        {
            var gate = new AuthRetryGate();
            var expected = new InvalidOperationException("declined");

            var actual = Assert.Throws<InvalidOperationException>(() => gate.Execute(Uri1, forceReauthenticate: false, authenticate: () => throw expected));

            Assert.Same(expected, actual);
        }

        [Fact]
        public void Execute_DoesNotCallAuthenticateAgain_ForSameUri_AfterASuccess_WhenNotForced()
        {
            var gate = new AuthRetryGate();
            var callCount = 0;

            gate.Execute(Uri1, forceReauthenticate: false, authenticate: () => callCount++);
            gate.Execute(Uri1, forceReauthenticate: false, authenticate: () => callCount++);

            Assert.Equal(1, callCount);
        }

        [Fact]
        public void Execute_CallsAuthenticateAgain_ForSameUri_AfterASuccess_WhenForced()
        {
            // Regression test for the scenario this ticket exists to fix: a session that goes stale a
            // SECOND time (hours into an unattended fetch) must trigger a real re-authentication
            // attempt again, not silently short-circuit off a now-stale "already authenticated" cache.
            var gate = new AuthRetryGate();
            var callCount = 0;

            gate.Execute(Uri1, forceReauthenticate: false, authenticate: () => callCount++);
            gate.Execute(Uri1, forceReauthenticate: true, authenticate: () => callCount++);

            Assert.Equal(2, callCount);
        }

        [Fact]
        public void Execute_CallsAuthenticateAgain_ForADifferentUri_AfterASuccess()
        {
            var gate = new AuthRetryGate();
            var callCount = 0;

            gate.Execute(Uri1, forceReauthenticate: false, authenticate: () => callCount++);
            gate.Execute(Uri2, forceReauthenticate: false, authenticate: () => callCount++);

            Assert.Equal(2, callCount);
        }

        [Fact]
        public void Execute_CallsAuthenticateAgain_ForSameUri_AfterAPriorFailureHasFinished()
        {
            // A failure is not cached forever either: once the failing attempt has completed (and
            // isn't still in-flight), a later call for the same Uri gets its own fresh attempt rather
            // than being permanently blocked by the earlier failure.
            var gate = new AuthRetryGate();
            var callCount = 0;

            Assert.Throws<InvalidOperationException>(() => gate.Execute(Uri1, forceReauthenticate: false, authenticate: () =>
            {
                callCount++;
                throw new InvalidOperationException("declined");
            }));
            gate.Execute(Uri1, forceReauthenticate: false, authenticate: () => callCount++);

            Assert.Equal(2, callCount);
        }

        [Fact]
        public async Task Execute_SerializesConcurrentCallers_ForSameUri_SoAuthenticateRunsOnce()
        {
            var gate = new AuthRetryGate();
            var authenticateEntered = new ManualResetEventSlim();
            var releaseAuthenticate = new ManualResetEventSlim();
            var callCount = 0;

            var firstCaller = Task.Run(() => gate.Execute(Uri1, forceReauthenticate: true, authenticate: () =>
            {
                Interlocked.Increment(ref callCount);
                authenticateEntered.Set();
                releaseAuthenticate.Wait();
            }));
            authenticateEntered.Wait();

            var secondCaller = Task.Run(() => gate.Execute(Uri1, forceReauthenticate: true, authenticate: () => Interlocked.Increment(ref callCount)));
            var secondCallerFinishedTooEarly = await Task.WhenAny(secondCaller, Task.Delay(200)) == secondCaller;

            releaseAuthenticate.Set();
            await Task.WhenAll(firstCaller, secondCaller);

            Assert.False(secondCallerFinishedTooEarly);
            Assert.Equal(1, callCount);
        }

        [Fact]
        public async Task Execute_SharesDeclineAmongConcurrentCallers_ForSameUri_WithoutPromptingAgain()
        {
            // The decline-cascade case: several parallel batches hit TF30063 for the same Uri around
            // the same moment. If the first one to authenticate is declined, the others waiting behind
            // it should fail with that same decline instead of each popping their own prompt.
            var gate = new AuthRetryGate();
            var authenticateEntered = new ManualResetEventSlim();
            var releaseAuthenticate = new ManualResetEventSlim();
            var callCount = 0;
            var declined = new InvalidOperationException("declined");

            var firstCaller = Task.Run(() => gate.Execute(Uri1, forceReauthenticate: true, authenticate: () =>
            {
                Interlocked.Increment(ref callCount);
                authenticateEntered.Set();
                releaseAuthenticate.Wait();
                throw declined;
            }));
            authenticateEntered.Wait();

            var secondCaller = Task.Run(() => gate.Execute(Uri1, forceReauthenticate: true, authenticate: () => Interlocked.Increment(ref callCount)));
            var secondCallerFinishedTooEarly = await Task.WhenAny(secondCaller, Task.Delay(200)) == secondCaller;

            releaseAuthenticate.Set();

            Assert.False(secondCallerFinishedTooEarly);
            var firstException = await Assert.ThrowsAsync<InvalidOperationException>(() => firstCaller);
            var secondException = await Assert.ThrowsAsync<InvalidOperationException>(() => secondCaller);

            Assert.Same(declined, firstException);
            Assert.Same(declined, secondException);
            Assert.Equal(1, callCount);
        }
    }
}
