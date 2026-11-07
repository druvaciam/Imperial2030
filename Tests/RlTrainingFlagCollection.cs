using Xunit;

namespace Imperial2030.Tests;

/// <summary>
/// <c>RLBotStrategy.IsTraining</c> is a process-wide switch: a test that turns it on changes what every
/// other test running at that moment sees, and the ones that check behaviour outside training then see
/// training behaviour instead. Every class that sets it - or that depends on it being off - joins this
/// collection, so xunit runs them one at a time and not alongside anything else.
///
/// Found by <c>JevRondelLayerTests</c>, which passed on its own and failed beside
/// <c>RLManeuverParityTests</c>: the layer is inference-only, so it correctly did nothing while another
/// class had the switch on.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RlTrainingFlagCollection
{
    public const string Name = "RLBotStrategy.IsTraining";
}
