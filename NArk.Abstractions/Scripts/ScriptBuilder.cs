using NBitcoin;

namespace NArk.Abstractions.Scripts;

/// <summary>Base class for building tapscript leaves used in Arkade contracts.</summary>
public abstract class ScriptBuilder
{
    /// <summary>Returns the opcodes for this tapscript leaf.</summary>
    public abstract IEnumerable<Op> BuildScript();

    /// <summary>Compiles the opcodes into a <see cref="TapScript"/> at leaf version 0xC0.</summary>
    public virtual TapScript Build()
    {
        return new TapScript(new Script(BuildScript()), TapLeafVersion.C0);
    }
}

