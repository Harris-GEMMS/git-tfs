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

            gate.Execute(Uri1, () => called = true);

            Assert.True(called);
        }

        [Fact]
        public void Execute_DoesNotCallAuthenticateAgain_ForSameUri_AfterASuccess()
        {
            var gate = new AuthRetryGate();
            var callCount = 0;

            gate.Execute(Uri1, () => callCount++);
            gate.Execute(Uri1, () => callCount++);

            Assert.Equal(1, callCount);
        }

        [Fact]
        public void Execute_CallsAuthenticateAgain_ForADifferentUri_AfterASuccess()
        {
            var gate = new AuthRetryGate();
            var callCount = 0;

            gate.Execute(Uri1, () => callCount++);
            gate.Execute(Uri2, () => callCount++);

            Assert.Equal(2, callCount);
        }

        [Fact]
        public void Execute_RethrowsCachedFailure_ForSameUri_WithoutCallingAuthenticateAgain()
        {
            var gate = new AuthRetryGate();
            var callCount = 0;
            var declined = new InvalidOperationException("declined");
            void Declining() { callCount++; throw declined; }

            Assert.Throws<InvalidOperationException>(() => gate.Execute(Uri1, Declining));
            var second = Assert.Throws<InvalidOperationException>(() => gate.Execute(Uri1, Declining));

            Assert.Equal(1, callCount);
            Assert.Same(declined, second);
        }

        [Fact]
        public async Task Execute_SerializesConcurrentCallers_ForSameUri_SoAuthenticateRunsOnce()
        {
            var gate = new AuthRetryGate();
            var authenticateEntered = new ManualResetEventSlim();
            var releaseAuthenticate = new ManualResetEventSlim();
            var callCount = 0;

            var firstCaller = Task.Run(() => gate.Execute(Uri1, () =>
            {
                Interlocked.Increment(ref callCount);
                authenticateEntered.Set();
                releaseAuthenticate.Wait();
            }));
            authenticateEntered.Wait();

            var secondCaller = Task.Run(() => gate.Execute(Uri1, () => Interlocked.Increment(ref callCount)));
            var secondCallerFinishedTooEarly = await Task.WhenAny(secondCaller, Task.Delay(200)) == secondCaller;

            releaseAuthenticate.Set();
            await Task.WhenAll(firstCaller, secondCaller);

            Assert.False(secondCallerFinishedTooEarly);
            Assert.Equal(1, callCount);
        }
    }
}
