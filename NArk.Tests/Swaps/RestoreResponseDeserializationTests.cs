using System.Text.Json;
using NArk.Swaps.Boltz.Models.Restore;

namespace NArk.Tests;

/// <summary>
/// Pins how a Boltz <c>/v2/swap/restore</c> payload maps onto
/// <see cref="RestorableSwap"/>. The endpoint answers for every public key in one
/// array, so a shape the model cannot read fails recovery for the whole batch — the
/// exact regression this fixture guards.
/// </summary>
[TestFixture]
public class RestoreResponseDeserializationTests
{
    private const string LockupTxId =
        "4f5625fb9402005e7b87eda95013d81dfac0a8ef8f3143704cb3d02f7b0d41ec";

    private static string Payload(string transactionBlock) => $$"""
        [
          {
            "id": "3fNqZk",
            "type": "reverse",
            "status": "transaction.confirmed",
            "createdAt": 1753000000,
            "from": "BTC",
            "to": "ARK",
            "preimageHash": "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
            "claimDetails": {
              "tree": {
                "claimLeaf": { "version": 192, "output": "82012088a914" },
                "refundLeaf": { "version": 192, "output": "20aabbcc" }
              },
              "keyIndex": 7,
              "lockupAddress": "bcrt1qexampleaddress",
              "serverPublicKey": "02aabbccddeeff00112233445566778899aabbccddeeff0011223344556677",
              "timeoutBlockHeight": 812345,
              "amount": 100000{{transactionBlock}}
            }
          }
        ]
        """;

    [Test]
    public void TransactionIsReadAsAnObject_NotAString()
    {
        var json = Payload($$"""
            ,
              "transaction": { "id": "{{LockupTxId}}", "vout": 1 }
            """);

        var swaps = JsonSerializer.Deserialize<RestorableSwap[]>(json);

        var transaction = swaps![0].ClaimDetails!.Transaction;
        Assert.Multiple(() =>
        {
            Assert.That(swaps[0].Id, Is.EqualTo("3fNqZk"));
            Assert.That(transaction, Is.Not.Null);
            Assert.That(transaction!.Id, Is.EqualTo(LockupTxId));
            Assert.That(transaction.Vout, Is.EqualTo(1u));
        });
    }

    [Test]
    public void AbsentTransactionBlock_LeavesTransactionNull()
    {
        var swaps = JsonSerializer.Deserialize<RestorableSwap[]>(Payload(string.Empty));

        Assert.That(swaps![0].ClaimDetails!.Transaction, Is.Null);
    }

    [Test]
    public void PartialTransactionBlock_DoesNotFailTheWholeBatch()
    {
        // A swap variant that omits `vout` must not take the rest of the array with it.
        var json = Payload($$"""
            ,
              "transaction": { "id": "{{LockupTxId}}" }
            """);

        var swaps = JsonSerializer.Deserialize<RestorableSwap[]>(json);

        Assert.Multiple(() =>
        {
            Assert.That(swaps, Has.Length.EqualTo(1));
            Assert.That(swaps![0].ClaimDetails!.Transaction!.Id, Is.EqualTo(LockupTxId));
            Assert.That(swaps[0].ClaimDetails!.Transaction!.Vout, Is.Null);
        });
    }
}
