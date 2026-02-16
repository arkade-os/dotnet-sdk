// Scratchpad for fast iteration against a running Aspire host.
// Start the AppHost first: dotnet run --project NArk.AppHost
//
// Usage: dotnet run --project NArk.Scratchpad -- [command]
// Commands:
//   info           - Get Ark server info + Boltz version + Fulmine status
//   boltz-pairs    - List Boltz chain swap pairs
//   boltz-raw      - Create a raw BTC→ARK chain swap and inspect the response
//   vhtlc-test     - Create chain swap & verify VHTLC address matches Boltz
//   chain-e2e      - Full BTC→ARK chain swap: create → fund → mine → monitor claiming
//   fulmine-info   - Query fulmine API endpoints
//   status <id>    - Get Boltz swap status

using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using NArk.Core.Contracts;
using NArk.Transport.GrpcClient;
using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.DataEncoders;
using NBitcoin.Scripting;

var boltzUrl = Environment.GetEnvironmentVariable("BOLTZ_URL") ?? "http://localhost:9001";
var arkUrl = Environment.GetEnvironmentVariable("ARK_URL") ?? "http://localhost:7070";
var fulmineUrl = Environment.GetEnvironmentVariable("FULMINE_URL") ?? "http://localhost:7003";

var boltzHttp = new HttpClient { BaseAddress = new Uri(boltzUrl) };
var fulmineHttp = new HttpClient { BaseAddress = new Uri(fulmineUrl) };

var command = args.Length > 0 ? args[0] : "info";
var pretty = new JsonSerializerOptions { WriteIndented = true };

try
{
    switch (command)
    {
        case "info":
            await ShowInfo();
            break;
        case "boltz-pairs":
            await ShowBoltzPairs();
            break;
        case "boltz-raw":
            await RawChainSwap(args.Length > 1 ? args[1] : "BTC", args.Length > 2 ? args[2] : "ARK");
            break;
        case "vhtlc-test":
            await VhtlcTest();
            break;
        case "chain-e2e":
            await ChainE2E();
            break;
        case "fulmine-info":
            await ShowFulmineInfo();
            break;
        case "status":
            if (args.Length < 2) { Console.WriteLine("Usage: status <swapId>"); return; }
            await ShowSwapStatus(args[1]);
            break;
        default:
            Console.WriteLine($"Unknown command: {command}");
            Console.WriteLine("Commands: info, boltz-pairs, boltz-raw, vhtlc-test, chain-e2e, fulmine-info, status <id>");
            break;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    if (ex.InnerException != null)
        Console.Error.WriteLine($"  Inner: {ex.InnerException.Message}");
    Console.Error.WriteLine(ex.StackTrace);
}

// --- Helpers ---

Sequence ParseSequence(long val)
    => val >= 512 ? new Sequence(TimeSpan.FromSeconds(val)) : new Sequence((int)val);

OutputDescriptor ParseOutputDescriptor(string str, Network network)
{
    if (!HexEncoder.IsWellFormed(str))
        return OutputDescriptor.Parse(str, network);
    var bytes = Convert.FromHexString(str);
    if (bytes.Length != 32 && bytes.Length != 33)
        throw new ArgumentException("the string must be 32/33 bytes long");
    return OutputDescriptor.Parse($"tr({str})", network);
}

async Task<string> DockerExec(params string[] dockerArgs)
{
    var psi = new ProcessStartInfo("docker")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };
    foreach (var a in dockerArgs) psi.ArgumentList.Add(a);
    var proc = Process.Start(psi)!;
    var stdout = await proc.StandardOutput.ReadToEndAsync();
    var stderr = await proc.StandardError.ReadToEndAsync();
    await proc.WaitForExitAsync();
    if (proc.ExitCode != 0)
        throw new Exception($"docker exit {proc.ExitCode}: {stderr.Trim()}");
    return stdout.Trim();
}

async Task BitcoinCli(params string[] cliArgs)
{
    var allArgs = new[] { "exec", "bitcoin", "bitcoin-cli", "-rpcwallet=" }.Concat(cliArgs).ToArray();
    var result = await DockerExec(allArgs);
    if (!string.IsNullOrEmpty(result))
        Console.WriteLine($"  bitcoin-cli: {result}");
}

async Task<string> BitcoinCliResult(params string[] cliArgs)
{
    var allArgs = new[] { "exec", "bitcoin", "bitcoin-cli", "-rpcwallet=" }.Concat(cliArgs).ToArray();
    return await DockerExec(allArgs);
}

async Task MineBlocks(int n = 1)
{
    var addr = await BitcoinCliResult("getnewaddress");
    await BitcoinCli("generatetoaddress", n.ToString(), addr);
}

async Task<JsonElement?> GetSwapStatusJson(string swapId)
{
    try
    {
        var resp = await boltzHttp.GetStringAsync($"v2/swap/{swapId}");
        return JsonSerializer.Deserialize<JsonElement>(resp);
    }
    catch { return null; }
}

// --- Commands ---

async Task ChainE2E()
{
    Console.WriteLine("=== BTC→ARK Chain Swap End-to-End ===\n");

    // Step 1: Get Ark server info
    var transport = new GrpcClientTransport(arkUrl);
    var serverInfo = await transport.GetServerInfoAsync();
    Console.WriteLine($"[1] Ark server: network={serverInfo.Network}, signer={serverInfo.SignerKey}");

    // Step 2: Generate keys & create swap
    var preimage = RandomUtils.GetBytes(32);
    var preimageHash = Hashes.SHA256(preimage);
    var claimKey = new Key();
    var refundKey = new Key();
    var claimDescriptor = OutputDescriptor.Parse(
        $"tr({Encoders.Hex.EncodeData(claimKey.PubKey.ToBytes())})", serverInfo.Network);

    var request = new JsonObject
    {
        ["from"] = "BTC",
        ["to"] = "ARK",
        ["preimageHash"] = Encoders.Hex.EncodeData(preimageHash),
        ["claimPublicKey"] = Encoders.Hex.EncodeData(claimKey.PubKey.ToBytes()),
        ["refundPublicKey"] = Encoders.Hex.EncodeData(refundKey.PubKey.ToBytes()),
        ["serverLockAmount"] = 50000
    };

    var resp = await boltzHttp.PostAsJsonAsync("v2/swap/chain", request);
    var rawJson = await resp.Content.ReadAsStringAsync();
    if (!resp.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"[2] FAILED: HTTP {(int)resp.StatusCode}: {rawJson}");
        return;
    }
    var swapDoc = JsonDocument.Parse(rawJson);
    var root = swapDoc.RootElement;
    var swapId = root.GetProperty("id").GetString()!;
    var claimDetails = root.GetProperty("claimDetails");
    var lockupDetails = root.GetProperty("lockupDetails");
    var btcAddress = lockupDetails.GetProperty("lockupAddress").GetString()!;
    var expectedSats = lockupDetails.GetProperty("amount").GetInt64();
    var arkLockupAddress = claimDetails.GetProperty("lockupAddress").GetString()!;

    Console.WriteLine($"[2] Swap created: {swapId}");
    Console.WriteLine($"    BTC lockup:   {btcAddress}");
    Console.WriteLine($"    Expected:     {expectedSats} sats");
    Console.WriteLine($"    ARK lockup:   {arkLockupAddress}");

    // Step 3: Verify VHTLC address
    var serverPubKey = claimDetails.GetProperty("serverPublicKey").GetString()!;
    var timeouts = claimDetails.TryGetProperty("timeouts", out var t) ? t
        : claimDetails.GetProperty("timeoutBlockHeights");
    var senderDesc = ParseOutputDescriptor(serverPubKey, serverInfo.Network);
    var vhtlc = new VHTLCContract(
        server: serverInfo.SignerKey,
        sender: senderDesc,
        receiver: claimDescriptor,
        preimage: preimage,
        refundLocktime: new LockTime((uint)timeouts.GetProperty("refund").GetInt64()),
        unilateralClaimDelay: ParseSequence(timeouts.GetProperty("unilateralClaim").GetInt64()),
        unilateralRefundDelay: ParseSequence(timeouts.GetProperty("unilateralRefund").GetInt64()),
        unilateralRefundWithoutReceiverDelay: ParseSequence(timeouts.GetProperty("unilateralRefundWithoutReceiver").GetInt64())
    );
    var computed = vhtlc.GetArkAddress().ToString(false);
    Console.WriteLine($"[3] VHTLC address match: {computed == arkLockupAddress}");
    if (computed != arkLockupAddress)
    {
        Console.Error.WriteLine($"    Computed: {computed}");
        Console.Error.WriteLine($"    Expected: {arkLockupAddress}");
        Console.Error.WriteLine("    ABORTING — address mismatch");
        return;
    }

    // Step 4: Fund the BTC lockup address
    var btcAmount = (expectedSats / 100_000_000m).ToString("0.########");
    Console.WriteLine($"\n[4] Sending {btcAmount} BTC to {btcAddress}...");
    var txid = await BitcoinCliResult("sendtoaddress", btcAddress, btcAmount);
    Console.WriteLine($"    txid: {txid}");

    // Step 5: Mine blocks and monitor swap status
    Console.WriteLine("\n[5] Mining blocks and monitoring swap status...");
    var lastStatus = "";
    for (var i = 0; i < 20; i++)
    {
        await MineBlocks(1);
        await Task.Delay(3000);

        var statusJson = await GetSwapStatusJson(swapId);
        var currentStatus = statusJson?.GetProperty("status").GetString() ?? "unknown";

        if (currentStatus != lastStatus)
        {
            Console.WriteLine($"    Round {i}: status = {currentStatus}");
            lastStatus = currentStatus;
        }
        else
        {
            Console.Write(".");
        }

        // Check fulmine VHTLCs to see if it created the ARK lock
        if (currentStatus.Contains("server"))
        {
            Console.WriteLine($"    → Boltz/fulmine has locked ARK!");
            try
            {
                var fulmineVhtlc = await fulmineHttp.GetStringAsync("api/v1/vhtlc");
                var vhtlcJson = JsonSerializer.Deserialize<JsonElement>(fulmineVhtlc);
                var vhtlcs = vhtlcJson.GetProperty("vhtlcs");
                Console.WriteLine($"    Fulmine has {vhtlcs.GetArrayLength()} VHTLCs");
                foreach (var v in vhtlcs.EnumerateArray())
                {
                    var script = v.GetProperty("script").GetString();
                    var amount = v.GetProperty("amount").GetString();
                    var swept = v.GetProperty("isSwept").GetBoolean();
                    Console.WriteLine($"      script={script?[..Math.Min(40, script.Length)]}... amount={amount} swept={swept}");
                }
            }
            catch (Exception ex) { Console.WriteLine($"    Fulmine VHTLC check failed: {ex.Message}"); }
        }

        if (currentStatus == "transaction.claimed")
        {
            Console.WriteLine($"\n[OK] Swap {swapId} SETTLED! Boltz claimed BTC, we should have ARK VTXOs.");
            break;
        }
    }

    // Final status
    Console.WriteLine($"\n=== Final Status ===");
    var finalStatus = await GetSwapStatusJson(swapId);
    if (finalStatus.HasValue)
        Console.WriteLine(JsonSerializer.Serialize(finalStatus.Value, pretty));
}

async Task VhtlcTest()
{
    Console.WriteLine("=== VHTLC Address Verification Test ===\n");
    var transport = new GrpcClientTransport(arkUrl);
    var serverInfo = await transport.GetServerInfoAsync();
    Console.WriteLine($"Ark signer:  {serverInfo.SignerKey}");
    Console.WriteLine($"Network:     {serverInfo.Network}\n");

    var preimage = RandomUtils.GetBytes(32);
    var preimageHash = Hashes.SHA256(preimage);
    var claimKey = new Key();
    var refundKey = new Key();
    var claimDescriptor = OutputDescriptor.Parse(
        $"tr({Encoders.Hex.EncodeData(claimKey.PubKey.ToBytes())})", serverInfo.Network);

    var request = new JsonObject
    {
        ["from"] = "BTC",
        ["to"] = "ARK",
        ["preimageHash"] = Encoders.Hex.EncodeData(preimageHash),
        ["claimPublicKey"] = Encoders.Hex.EncodeData(claimKey.PubKey.ToBytes()),
        ["refundPublicKey"] = Encoders.Hex.EncodeData(refundKey.PubKey.ToBytes()),
        ["serverLockAmount"] = 50000
    };

    var resp = await boltzHttp.PostAsJsonAsync("v2/swap/chain", request);
    var rawJson = await resp.Content.ReadAsStringAsync();
    if (!resp.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"HTTP {(int)resp.StatusCode}: {rawJson}");
        return;
    }

    var json = JsonDocument.Parse(rawJson);
    var root = json.RootElement;
    var claimDetails = root.GetProperty("claimDetails");
    var serverPubKey = claimDetails.GetProperty("serverPublicKey").GetString()!;
    var timeouts = claimDetails.TryGetProperty("timeouts", out var t) ? t
        : claimDetails.GetProperty("timeoutBlockHeights");

    var senderDescriptor = ParseOutputDescriptor(serverPubKey, serverInfo.Network);
    var vhtlcContract = new VHTLCContract(
        server: serverInfo.SignerKey,
        sender: senderDescriptor,
        receiver: claimDescriptor,
        preimage: preimage,
        refundLocktime: new LockTime((uint)timeouts.GetProperty("refund").GetInt64()),
        unilateralClaimDelay: ParseSequence(timeouts.GetProperty("unilateralClaim").GetInt64()),
        unilateralRefundDelay: ParseSequence(timeouts.GetProperty("unilateralRefund").GetInt64()),
        unilateralRefundWithoutReceiverDelay: ParseSequence(timeouts.GetProperty("unilateralRefundWithoutReceiver").GetInt64())
    );

    var computedAddress = vhtlcContract.GetArkAddress().ToString(false);
    var expectedAddress = claimDetails.GetProperty("lockupAddress").GetString()!;
    Console.WriteLine($"Swap ID:  {root.GetProperty("id").GetString()}");
    Console.WriteLine($"Computed: {computedAddress}");
    Console.WriteLine($"Expected: {expectedAddress}");
    Console.WriteLine($"Match:    {computedAddress == expectedAddress}");
}

async Task ShowInfo()
{
    Console.WriteLine("=== Ark Server ===");
    try
    {
        var transport = new GrpcClientTransport(arkUrl);
        var info = await transport.GetServerInfoAsync();
        Console.WriteLine($"  Network:  {info.Network}");
        Console.WriteLine($"  Dust:     {info.Dust}");
        Console.WriteLine($"  Signer:   {info.SignerKey}");
    }
    catch (Exception ex) { Console.WriteLine($"  FAILED: {ex.Message}"); }

    Console.WriteLine("\n=== Boltz ===");
    try
    {
        var resp = await boltzHttp.GetStringAsync("version");
        Console.WriteLine($"  Version: {resp}");
    }
    catch (Exception ex) { Console.WriteLine($"  FAILED: {ex.Message}"); }

    Console.WriteLine("\n=== Fulmine ===");
    try
    {
        var status = await fulmineHttp.GetStringAsync("api/v1/wallet/status");
        Console.WriteLine($"  Wallet:  {status}");
    }
    catch (Exception ex) { Console.WriteLine($"  FAILED: {ex.Message}"); }
}

async Task ShowBoltzPairs()
{
    var resp = await boltzHttp.GetStringAsync("v2/swap/chain");
    var json = JsonSerializer.Deserialize<JsonElement>(resp);
    Console.WriteLine(JsonSerializer.Serialize(json, pretty));
}

async Task RawChainSwap(string from, string to)
{
    Console.WriteLine($"=== Chain Swap: {from} → {to} ===\n");
    var preimage = new byte[32];
    Random.Shared.NextBytes(preimage);
    var preimageHash = System.Security.Cryptography.SHA256.HashData(preimage);
    var claimKey = new NBitcoin.Key();
    var refundKey = new NBitcoin.Key();

    var request = new JsonObject
    {
        ["from"] = from,
        ["to"] = to,
        ["preimageHash"] = Convert.ToHexString(preimageHash).ToLowerInvariant(),
        ["claimPublicKey"] = Convert.ToHexString(claimKey.PubKey.ToBytes()).ToLowerInvariant(),
        ["refundPublicKey"] = Convert.ToHexString(refundKey.PubKey.ToBytes()).ToLowerInvariant(),
    };
    if (from == "BTC") request["serverLockAmount"] = 50000;
    else request["userLockAmount"] = 50000;

    Console.WriteLine($"Request:\n{request.ToJsonString(pretty)}\n");
    var resp = await boltzHttp.PostAsJsonAsync("v2/swap/chain", request);
    var rawJson = await resp.Content.ReadAsStringAsync();
    if (!resp.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"HTTP {(int)resp.StatusCode}: {rawJson}");
        return;
    }
    var json = JsonSerializer.Deserialize<JsonElement>(rawJson);
    Console.WriteLine($"Response ({(int)resp.StatusCode}):\n{JsonSerializer.Serialize(json, pretty)}");
}

async Task ShowFulmineInfo()
{
    Console.WriteLine("=== Fulmine ===");
    foreach (var endpoint in new[] { "api/v1/address", "api/v1/balance", "api/v1/vhtlc" })
    {
        try
        {
            var resp = await fulmineHttp.GetStringAsync(endpoint);
            var json = JsonSerializer.Deserialize<JsonElement>(resp);
            Console.WriteLine($"\n  GET /{endpoint}:");
            Console.WriteLine($"  {JsonSerializer.Serialize(json, pretty)}");
        }
        catch (Exception ex) { Console.WriteLine($"\n  GET /{endpoint}: FAILED - {ex.Message}"); }
    }
}

async Task ShowSwapStatus(string swapId)
{
    var resp = await boltzHttp.GetStringAsync($"v2/swap/{swapId}");
    var json = JsonSerializer.Deserialize<JsonElement>(resp);
    Console.WriteLine($"Swap {swapId}:");
    Console.WriteLine(JsonSerializer.Serialize(json, pretty));
}
