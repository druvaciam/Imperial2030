namespace Imperial2030.Client;

/// <summary>
/// Marker type for the cross-cutting resource file (<c>Resources/SharedResource.resx</c>), used as
/// <c>IStringLocalizer&lt;SharedResource&gt;</c>. Holds strings that appear in more than one
/// component (common buttons) plus every domain display name — nations, territories, rondel slots,
/// unit types, phases, statuses.
///
/// This class deliberately lives at the project root rather than in <c>Resources/</c>.
/// <c>ResourceManagerStringLocalizerFactory</c> builds the resource base name as
/// <c>RootNamespace + "." + ResourcesPath + TrimPrefix(type.FullName, RootNamespace + ".")</c>, so a
/// marker in namespace <c>Imperial2030.Client.Resources</c> would resolve to
/// <c>Imperial2030.Client.Resources.Resources.SharedResource</c> — a doubled segment that never
/// matches the .resx. At the root it resolves to <c>Imperial2030.Client.Resources.SharedResource</c>,
/// which does.
/// </summary>
public class SharedResource
{
}
