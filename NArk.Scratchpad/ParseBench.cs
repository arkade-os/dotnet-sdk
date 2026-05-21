using System.Diagnostics;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.Scratchpad;

/// <summary>
/// Microbenchmark: where does the time go inside <see cref="OutputDescriptor.Parse"/>?
/// Run via <c>dotnet run --project NArk.Scratchpad -- parse-bench</c>.
/// </summary>
public static class ParseBench
{
    // Representative samples seen in the BTCPay debug log from a live wallet
    // — these are exactly the strings IsOurs / ArkContractParser hit on the
    // hot path during a reverse-submarine claim.
    private const string WildcardWithOrigin =
        "tr([6559cf91/86'/1'/0']tpubDC5B1VgxuR9RmrhG1NhiZczXbTXFLCAAYSYCvC2BK921NyJ2HPmtJLdKwWkG4G3Ue1R665adMHa5xmkfPtz977vfaEt3t87oW63yPk2xGRU/0/*)";

    private const string ResolvedWithOrigin =
        "tr([6559cf91/86'/1'/0']tpubDC5B1VgxuR9RmrhG1NhiZczXbTXFLCAAYSYCvC2BK921NyJ2HPmtJLdKwWkG4G3Ue1R665adMHa5xmkfPtz977vfaEt3t87oW63yPk2xGRU/0/18)";

    private const string PlainPubkey32 =
        "tr(e35799157be4b37565bb5afe4d04e6a0fa0a4b6a4f4e48b0d904685d253cdbdb)";

    public static void Run()
    {
        var network = Network.RegTest;

        var samples = new[]
        {
            ("plain-pubkey-32", PlainPubkey32),
            ("wildcard-w-origin", WildcardWithOrigin),
            ("resolved-w-origin", ResolvedWithOrigin),
        };

        // Warm the JIT / static init so we don't conflate first-call cost
        // with steady-state cost.
        foreach (var (_, s) in samples)
            OutputDescriptor.Parse(s, network);

        const int iter = 100;

        Console.WriteLine($"OutputDescriptor.Parse — {iter} iterations each (idle process)");
        Console.WriteLine($"{"sample",-22} {"avg/call",-12} {"min",-10} {"max",-10}");
        Console.WriteLine(new string('-', 60));
        foreach (var (name, s) in samples)
        {
            var times = new List<double>();
            for (var i = 0; i < iter; i++)
            {
                var sw = Stopwatch.StartNew();
                OutputDescriptor.Parse(s, network);
                sw.Stop();
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            Console.WriteLine($"{name,-22} {times.Average(),8:F3} ms  {times.Min(),5:F3} ms {times.Max(),5:F3} ms");
        }

        Console.WriteLine();
        Console.WriteLine("Now breaking down what Parse actually does for the wildcard descriptor:");
        BreakdownInternals(network);

        Console.WriteLine();
        Console.WriteLine("Simulating thread-pool pressure (BTCPay sees the parse from inside async event handlers");
        Console.WriteLine("while many concurrent tasks contend for worker threads):");
        RunUnderLoad(network, samples);
    }

    private static void RunUnderLoad(Network network, (string name, string sample)[] samples)
    {
        // Spin up busy work that loops, hands back to scheduler, allocates,
        // mimicking the pattern in the live BTCPay (many concurrent async
        // tasks, GC churn). The point isn't to recreate BTCPay precisely —
        // it's to show that the SAME Parse on the SAME string degrades
        // dramatically when the runtime is under pressure, ruling the parser
        // itself out as the cause.
        using var cts = new CancellationTokenSource();
        const int busyTasks = 64;
        var tasks = new Task[busyTasks];
        for (var t = 0; t < busyTasks; t++)
        {
            tasks[t] = Task.Run(async () =>
            {
                var bag = new List<byte[]>();
                while (!cts.IsCancellationRequested)
                {
                    // Allocate (GC pressure) + yield (thread-pool queue).
                    var b = new byte[8192];
                    Random.Shared.NextBytes(b);
                    bag.Add(b);
                    if (bag.Count > 256) bag.Clear();
                    await Task.Yield();
                }
            });
        }

        // Let the load settle for a beat.
        Thread.Sleep(500);

        const int iter = 100;
        Console.WriteLine($"{"sample",-22} {"avg/call",-12} {"min",-10} {"max",-10}");
        Console.WriteLine(new string('-', 60));
        foreach (var (name, s) in samples)
        {
            var times = new List<double>();
            for (var i = 0; i < iter; i++)
            {
                var sw = Stopwatch.StartNew();
                OutputDescriptor.Parse(s, network);
                sw.Stop();
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            Console.WriteLine($"{name,-22} {times.Average(),8:F3} ms  {times.Min(),5:F3} ms {times.Max(),5:F3} ms");
        }

        cts.Cancel();
        try { Task.WhenAll(tasks).Wait(TimeSpan.FromSeconds(2)); } catch { /* OperationCanceledException is expected */ }
    }

    public static void RunMnemonicBench()
    {
        // The real bottleneck in the live wallet path: HierarchicalDeterministicWalletSigner
        // calls `new Mnemonic(secret).DeriveExtKey()` on every Sign/SignMusig/GetPubKey.
        // BIP-39 → BIP-32 master extKey is PBKDF2-HMAC-SHA512 × 2048 iterations.
        const string m =
            "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

        // Warm up
        for (var i = 0; i < 3; i++) _ = new Mnemonic(m).DeriveExtKey();

        const int iter = 50;
        var times = new List<double>();
        for (var i = 0; i < iter; i++)
        {
            var sw = Stopwatch.StartNew();
            _ = new Mnemonic(m).DeriveExtKey();
            sw.Stop();
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        Console.WriteLine($"new Mnemonic(secret).DeriveExtKey()   avg={times.Average():F2} ms  min={times.Min():F2}  max={times.Max():F2}");

        // Compare with subsequent BIP-32 child derivation (after extKey is computed)
        var ek = new Mnemonic(m).DeriveExtKey();
        var path = new KeyPath("86'/1'/0'/0/18");
        times.Clear();
        for (var i = 0; i < iter; i++)
        {
            var sw = Stopwatch.StartNew();
            _ = ek.Derive(path).PrivateKey;
            sw.Stop();
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        Console.WriteLine($"extKey.Derive(86'/1'/0'/0/18)         avg={times.Average():F2} ms  min={times.Min():F2}  max={times.Max():F2}");
    }

    private static void BreakdownInternals(Network network)
    {
        // The interesting candidates inside the parser:
        //   1. Descriptor-grammar lexing / token matching
        //   2. BIP-32 xpub decode (base58check + chain-code parse)
        //   3. BIP-32 child derivation along /0/18
        //   4. Checksum compute (only if the input has one)
        // We can isolate (2) and (3) by going through NBitcoin's public APIs.

        const string xpubB58 = "tpubDC5B1VgxuR9RmrhG1NhiZczXbTXFLCAAYSYCvC2BK921NyJ2HPmtJLdKwWkG4G3Ue1R665adMHa5xmkfPtz977vfaEt3t87oW63yPk2xGRU";

        const int iter = 1_000;

        // ---- (2) xpub decode ----
        {
            var times = new List<double>();
            for (var i = 0; i < iter; i++)
            {
                var sw = Stopwatch.StartNew();
                _ = new BitcoinExtPubKey(xpubB58, network);
                sw.Stop();
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            Console.WriteLine($"  BitcoinExtPubKey decode:       avg={times.Average():F4} ms");
        }

        // ---- (3) BIP-32 child derivation /0/18 ----
        {
            var parent = new BitcoinExtPubKey(xpubB58, network);
            var times = new List<double>();
            for (var i = 0; i < iter; i++)
            {
                var sw = Stopwatch.StartNew();
                _ = parent.Derive(new KeyPath("0/18")).GetPublicKey();
                sw.Stop();
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            Console.WriteLine($"  BIP-32 derive /0/18:           avg={times.Average():F4} ms");
        }

        // ---- (4) checksum compute on the descriptor string body ----
        {
            const string body = "tr([6559cf91/86'/1'/0']tpubDC5B1VgxuR9RmrhG1NhiZczXbTXFLCAAYSYCvC2BK921NyJ2HPmtJLdKwWkG4G3Ue1R665adMHa5xmkfPtz977vfaEt3t87oW63yPk2xGRU/0/18)";
            var times = new List<double>();
            for (var i = 0; i < iter; i++)
            {
                var sw = Stopwatch.StartNew();
                _ = OutputDescriptor.GetCheckSum(body);
                sw.Stop();
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            Console.WriteLine($"  GetCheckSum (no '#'  branch):  avg={times.Average():F4} ms");
        }
    }
}
