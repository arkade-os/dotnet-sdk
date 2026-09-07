using System.Runtime.CompilerServices;
using NArk.Core.Contracts;

namespace NArk.Arkade.Contracts;

/// <summary>
/// Makes this assembly's contract types readable back out of storage.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ArkContractParser"/> knows only the types defined alongside it, so a contract added
/// here could be written and never read: storage records <c>HTLCv2</c> without complaint, and every
/// later lookup returns null. What that looks like from the outside is a sweeper that cannot see
/// its own VTXOs and a coin that refuses to be signed — neither of which points at a missing
/// parser registration.
/// </para>
/// <para>
/// A module initializer rather than a DI call, because the fact being registered is not a
/// deployment choice: if this assembly is loaded, its contract types exist and are parseable, and
/// that should not depend on a host remembering to opt in. Hosts that wire services by hand —
/// samples, tests, plugins — get it for free, which is exactly where the opt-in would have been
/// forgotten.
/// </para>
/// </remarks>
internal static class ArkadeContractRegistration
{
    [ModuleInitializer]
    internal static void Register() =>
        ArkContractParser.Register(VHTLCv2Contract.ContractType, VHTLCv2Contract.Parse);
}
