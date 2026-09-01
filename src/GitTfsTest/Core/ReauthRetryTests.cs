using GitTfs.Core;
using Xunit;

namespace GitTfsTest.Core
{
    public class ReauthRetryTests
    {
        [Fact]
        public void Do_ReturnsResult_WhenActionSucceedsFirstTry()
        {
            var result = ReauthRetry.Do(() => 42, ex => false, () => { });

            Assert.Equal(42, result);
        }

        [Fact]
        public void Do_ReauthenticatesAndRetries_WhenPredicateMatches()
        {
            var attempt = 0;
            var reauthenticateCalls = 0;

            var result = ReauthRetry.Do(() =>
            {
                attempt++;
                if (attempt == 1)
                    throw new InvalidOperationException("stale session");
                return "success";
            }, ex => true, () => reauthenticateCalls++);

            Assert.Equal("success", result);
            Assert.Equal(2, attempt);
            Assert.Equal(1, reauthenticateCalls);
        }

        [Fact]
        public void Do_PropagatesException_WhenPredicateDoesNotMatch_WithoutReauthenticating()
        {
            var expected = new InvalidOperationException("not an auth failure");
            var reauthenticateCalls = 0;

            var actual = Assert.Throws<InvalidOperationException>(() => ReauthRetry.Do<int>(() => throw expected, ex => false, () => reauthenticateCalls++));

            Assert.Same(expected, actual);
            Assert.Equal(0, reauthenticateCalls);
        }

        [Fact]
        public void Do_KeepsRetrying_AcrossMultipleConsecutiveAuthFailures()
        {
            var attempt = 0;
            var reauthenticateCalls = 0;

            var result = ReauthRetry.Do(() =>
            {
                attempt++;
                if (attempt <= 5)
                    throw new InvalidOperationException("stale session");
                return "success";
            }, ex => true, () => reauthenticateCalls++);

            Assert.Equal("success", result);
            Assert.Equal(6, attempt);
            Assert.Equal(5, reauthenticateCalls);
        }
    }
}
