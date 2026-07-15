using NArk.Arkade.Program;

namespace NArk.Tests.Arkade;

[TestFixture]
public class ArkadeProgramValidatorTests
{
    // A 32-byte value usable as an x-only pubkey/hash literal; content is irrelevant to validation.
    private static byte[] Bytes(int length, byte fill = 0xab) => Enumerable.Repeat(fill, length).ToArray();

    /// <summary>A one-function program: literal-pubkey signer plus an optional condition asm.</summary>
    private static ArkadeProgram Program(
        IReadOnlyList<TypedInput>? parameters,
        IReadOnlyList<AsmToken>? asm = null) =>
        new()
        {
            Version = ArkadeProgram.SupportedVersion,
            Params = parameters,
            Functions = new Dictionary<string, ArkadeFunction>
            {
                ["f"] = new()
                {
                    Tapscript = new TapscriptSegment
                    {
                        Signers = [AsmToken.FromBytes(Bytes(32))],
                        Asm = asm,
                    },
                },
            },
        };

    private static Dictionary<string, AsmToken> Args(params (string, AsmToken)[] entries)
    {
        var d = new Dictionary<string, AsmToken>();
        foreach (var (k, v) in entries) d[k] = v;
        return d;
    }

    [Test]
    public void NullParams_NoValidation()
    {
        // No declared params → nothing to validate, even with an undeclared $x reference.
        var program = Program(parameters: null, asm: ["HASH160", "$x", "EQUAL"]);
        Assert.DoesNotThrow(() => ArkadeProgramValidator.Validate(program, Args()));
    }

    [Test]
    public void BareParams_AreStructurallyChecked_UnboundThrows()
    {
        // Bare (untyped) params skip the value type-check but are still checked for binding.
        var program = Program(parameters: ["a"]);
        var ex = Assert.Throws<InvalidOperationException>(
            () => ArkadeProgramValidator.Validate(program, Args()));
        Assert.That(ex!.Message, Does.Contain("'a'").And.Contain("not bound"));
    }

    [Test]
    public void BareParams_AllBoundAndDeclared_Passes()
    {
        // Bound + every $ref declared → passes; no type constraint on bare params (any kind).
        var program = Program(parameters: ["a"], asm: ["HASH160", "$a", "EQUAL"]);
        Assert.DoesNotThrow(() => ArkadeProgramValidator.Validate(
            program, Args(("a", AsmToken.FromBytes(Bytes(20))))));
    }

    [Test]
    public void BareParam_ReferenceNotDeclared_Throws()
    {
        var program = Program(parameters: ["a"], asm: ["HASH160", "$undeclared", "EQUAL"]);
        var ex = Assert.Throws<InvalidOperationException>(
            () => ArkadeProgramValidator.Validate(program, Args(("a", AsmToken.FromBytes(Bytes(4))))));
        Assert.That(ex!.Message, Does.Contain("'$undeclared'").And.Contain("not declared"));
    }

    [Test]
    public void TypedParam_DeclaredButUnbound_Throws()
    {
        var program = Program(parameters: [new TypedInput { Name = "amount", Type = InputType.Int }]);
        var ex = Assert.Throws<InvalidOperationException>(
            () => ArkadeProgramValidator.Validate(program, Args()));
        Assert.That(ex!.Message, Does.Contain("'amount'").And.Contain("not bound"));
    }

    [Test]
    public void TypedParam_ReferenceNotDeclared_Throws()
    {
        // One typed param makes the list authoritative; the asm's $b is undeclared.
        var program = Program(
            parameters: [new TypedInput { Name = "a", Type = InputType.Int }],
            asm: ["HASH160", "$b", "EQUAL"]);
        var ex = Assert.Throws<InvalidOperationException>(
            () => ArkadeProgramValidator.Validate(program, Args(("a", AsmToken.FromNumber(1)))));
        Assert.That(ex!.Message, Does.Contain("'$b'").And.Contain("not declared"));
    }

    [Test]
    public void TypedPubkey_WrongLength_Throws()
    {
        var program = Program(parameters: [new TypedInput { Name = "k", Type = InputType.Pubkey }]);
        var ex = Assert.Throws<InvalidOperationException>(
            () => ArkadeProgramValidator.Validate(program, Args(("k", AsmToken.FromBytes(Bytes(33))))));
        Assert.That(ex!.Message, Does.Contain("32-byte pubkey").And.Contain("33 bytes"));
    }

    [Test]
    public void TypedSig_WrongLength_Throws()
    {
        var program = Program(parameters: [new TypedInput { Name = "s", Type = InputType.Sig }]);
        var ex = Assert.Throws<InvalidOperationException>(
            () => ArkadeProgramValidator.Validate(program, Args(("s", AsmToken.FromBytes(Bytes(63))))));
        Assert.That(ex!.Message, Does.Contain("64-byte sig"));
    }

    [Test]
    public void TypedInt_GotBytes_Throws()
    {
        var program = Program(parameters: [new TypedInput { Name = "n", Type = InputType.Int }]);
        var ex = Assert.Throws<InvalidOperationException>(
            () => ArkadeProgramValidator.Validate(program, Args(("n", AsmToken.FromBytes(Bytes(4))))));
        Assert.That(ex!.Message, Does.Contain("expects an int, got bytes"));
    }

    [Test]
    public void TypedBytesAndHash_AnyLength_Ok()
    {
        // bytes/hash carry no length constraint (only pubkey/sig do).
        var program = Program(parameters:
        [
            new TypedInput { Name = "data", Type = InputType.Bytes },
            new TypedInput { Name = "h", Type = InputType.Hash },
        ]);
        Assert.DoesNotThrow(() => ArkadeProgramValidator.Validate(program, Args(
            ("data", AsmToken.FromBytes(Bytes(7))),
            ("h", AsmToken.FromBytes(Bytes(20))))));
    }

    [Test]
    public void ValidTypedProgram_Passes()
    {
        var program = new ArkadeProgram
        {
            Version = ArkadeProgram.SupportedVersion,
            Params =
            [
                new TypedInput { Name = "server", Type = InputType.Pubkey },
                new TypedInput { Name = "hash", Type = InputType.Hash },
            ],
            Functions = new Dictionary<string, ArkadeFunction>
            {
                ["claim"] = new()
                {
                    Tapscript = new TapscriptSegment
                    {
                        Signers = [AsmToken.FromText("$server")],
                        Asm = ["HASH160", "$hash", "EQUAL"],
                    },
                },
            },
        };

        Assert.DoesNotThrow(() => ArkadeProgramValidator.Validate(program, Args(
            ("server", AsmToken.FromBytes(Bytes(32))),
            ("hash", AsmToken.FromBytes(Bytes(20))))));
    }
}
