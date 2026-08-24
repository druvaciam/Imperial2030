using Imperial2030.Server.Helpers;
using Xunit;

namespace Imperial2030.Tests
{
    /// <summary>
    /// Covers the 500-body text shared by the three endpoints that used to return raw `ex.Message`
    /// (GamesController's ImportGame and StartGame, ManeuverController's NextPhase).
    /// </summary>
    public class ErrorResponsesTests
    {
        [Fact]
        public void Internal_IncludesTheTraceIdentifierAsAQuotableReference()
        {
            var message = ErrorResponses.Internal("0HN7GK2M9V:00000003");

            Assert.Contains("0HN7GK2M9V:00000003", message);
            Assert.StartsWith(ErrorResponses.GenericInternalError, message);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Internal_WithoutATraceIdentifier_OmitsTheReferenceLabelEntirely(string? traceIdentifier)
        {
            // Rendering "Reference: " with nothing after it is worse than saying nothing.
            var message = ErrorResponses.Internal(traceIdentifier);

            Assert.Equal(ErrorResponses.GenericInternalError, message);
            Assert.DoesNotContain("Reference", message);
        }
    }
}
