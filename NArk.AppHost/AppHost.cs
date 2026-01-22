using System.Globalization;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var bitcoin =
    builder
        .AddContainer("bitcoin", "ghcr.io/getumbrel/docker-bitcoind", "v29.0")
        .WithContainerName("bitcoin")
        .WithContainerNetworkAlias("bitcoin")
        .WithEndpoint(port: 18443, targetPort: 18443, protocol: ProtocolType.Tcp, name: "port")
        .WithEndpoint(port: 18444, targetPort: 18444, protocol: ProtocolType.Tcp, name: "rpcport")
        .WithEndpoint(28332, 28332, protocol: ProtocolType.Tcp, name: "zmqpub-block")
        .WithEndpoint(28333, 28333, protocol: ProtocolType.Tcp, name: "zmqpub-tx")
        .WithCommand("generate-blocks", "Generate blocks", async context =>
        {
            var generateProcess =
                await Cli.Wrap("docker")
                .WithArguments(["exec", "bitcoin", "bitcoin-cli", "-rpcwallet=", "-generate", "20"])
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(context.CancellationToken);

            if (!generateProcess.IsSuccess)
            {
                return new ExecuteCommandResult()
                {
                    Success = false,
                    ErrorMessage =
                        $"Block generation failed, output = {generateProcess.StandardOutput}, error = {generateProcess.StandardError}"
                };
            }

            return new ExecuteCommandResult() { Success = true };
        })
        .WithVolume("nark-bitcoind", target: "/data/.bitcoin")
        .WithContainerFiles("/data/.bitcoin/", "Assets/bitcoin.conf");

var electrs =
    builder
        .AddContainer("electrs", "ghcr.io/vulpemventures/electrs", "latest")
        .WithContainerName("electrs")
        .WithContainerNetworkAlias("electrs")
        .WithEntrypoint("/build/electrs")
        .WithEndpoint(50000, 50000, protocol: ProtocolType.Tcp, name: "rpc")
        .WithEndpoint(30000, 30000, protocol: ProtocolType.Tcp, name: "http")
        .WithArgs(
            "-vvvv",
            "--network", "regtest", "--daemon-dir", "/config",
            "--daemon-rpc-addr", "bitcoin:18443", "--cookie", "admin1:123",
            "--http-addr", "0.0.0.0:30000", "--electrum-rpc-addr", "0.0.0.0:50000", "--cors", "\"*\"",
            "--jsonrpc-import"
        )
        .WithVolume("nark-electrs", "/config")
        .WaitFor(bitcoin);

var chopsticks =
    builder
        .AddContainer("chopsticks", "ghcr.io/vulpemventures/nigiri-chopsticks", "latest")
        .WithContainerName("chopsticks")
        .WithContainerNetworkAlias("chopsticks")
        .WithArgs("--use-faucet", "--use-mining", "--use-logger", "--rpc-addr", "bitcoin:18443", "--electrs-addr",
            "electrs:30000", "--addr", "0.0.0.0:3000")
        .WithHttpEndpoint(3000, 3000, name: "http")
        .WaitFor(bitcoin)
        .WaitFor(electrs);

builder
    .AddContainer("esplora", "ghcr.io/vulpemventures/esplora", "latest")
    .WithContainerName("esplora")
    .WithContainerNetworkAlias("esplora")
    .WithEnvironment("API_URL", "http://localhost:3000")
    .WithEndpoint(5000, 5001, protocol: ProtocolType.Tcp, name: "http")
    .WaitFor(chopsticks);

var postgres =
    builder
        .AddPostgres("postgres")
        .WithContainerName("postgres")
        .WithContainerNetworkAlias("postgres")
        .WithHostPort(39372)
        .WithDataVolume("nark-postgres")
        .WithEnvironment("POSTGRES_HOST_AUTH_METHOD", "trust");

var arkdDb = postgres
    .AddDatabase("arkd-db", "arkd");

var nbxplorerDb = postgres
    .AddDatabase("nbxplorer-db", "nbxplorer");

var boltzDb = postgres
    .AddDatabase("boltz-db", "boltz");

var nbxplorer =
    builder
        .AddContainer("nbxplorer", "nicolasdorier/nbxplorer", "2.5.30-1")
        .WithContainerNetworkAlias("nbxplorer")
        .WithHttpEndpoint(32838, 32838, "http")
        .WithEnvironment("NBXPLORER_NETWORK", "regtest")
        .WithEnvironment("NBXPLORER_CHAINS", "btc")
        .WithEnvironment("NBXPLORER_BTCRPCURL", "http://bitcoin:18443/")
        .WithEnvironment("NBXPLORER_BTCNODEENDPOINT", "bitcoin:18444")
        .WithEnvironment("NBXPLORER_BTCRPCUSER", "admin1")
        .WithEnvironment("NBXPLORER_BTCRPCPASSWORD", "123")
        .WithEnvironment("NBXPLORER_VERBOSE", "1")
        .WithEnvironment("NBXPLORER_BIND", "0.0.0.0:32838")
        .WithEnvironment("NBXPLORER_TRIMEVENTS", "10000")
        .WithEnvironment("NBXPLORER_SIGNALFILESDIR", "/datadir")
        .WithEnvironment("NBXPLORER_POSTGRES",
            "User ID=postgres;Host=postgres;Port=5432;Application Name=nbxplorer;MaxPoolSize=20;Database=nbxplorer")
        .WithEnvironment("NBXPLORER_EXPOSERPC", "1")
        .WithEnvironment("NBXPLORER_NOAUTH", "1")
        .WithVolume("nark-nbxplorer", "/datadir")
        .WithHttpHealthCheck("/health", 200, "http")
        .WaitFor(nbxplorerDb)
        .WaitFor(bitcoin);

var arkWallet =
    builder
        .AddContainer("ark-wallet", "ghcr.io/arkade-os/arkd-wallet", "v0.8.10")
        .WithContainerName("ark-wallet")
        .WithContainerNetworkAlias("ark-wallet")
        .WaitFor(bitcoin)
        .WaitFor(nbxplorer)
        .WithEnvironment("ARKD_WALLET_LOG_LEVEL", "5")
        .WithEnvironment("ARKD_WALLET_NBXPLORER_URL", "http://nbxplorer:32838")
        .WithEnvironment("ARKD_WALLET_NETWORK", "regtest")
        .WithEnvironment("ARKD_WALLET_SIGNER_KEY", "19422b10efd05403820ff6a3365422be2fc5f07f34a6d1603f7298328f0f80f6")
        .WithVolume("nark-ark-wallet", "/app/data")
        .WithEndpoint(6060, 6060, protocol: ProtocolType.Tcp, name: "wallet");

var ark =
    builder
        .AddContainer("ark", "ghcr.io/arkade-os/arkd", "v0.8.10")
        .WithContainerName("ark")
        .WaitFor(bitcoin)
        .WaitFor(arkdDb)
        .WaitFor(arkWallet)
        .WithEnvironment("ARKD_LOG_LEVEL", "5")
        .WithEnvironment("ARKD_NO_TLS", "true")
        .WithEnvironment("ARKD_NO_MACAROONS", "true")
        .WithEnvironment("ARKD_WALLET_ADDR", "ark-wallet:6060")
        .WithEnvironment("ARKD_ESPLORA_URL", "http://chopsticks:3000")
        .WithEnvironment("ARKD_VTXO_MIN_AMOUNT", "1")
        .WithEnvironment("ARKD_VTXO_TREE_EXPIRY", args.Contains("--fast-expire") ? "16" : "1024")
        .WithEnvironment("ARKD_UNILATERAL_EXIT_DELAY", args.Contains("--fast-expire") ? "16" : "512")
        .WithEnvironment("ARKD_BOARDING_EXIT_DELAY", args.Contains("--fast-expire") ? "16" : "1024")
        .WithEnvironment("ARKD_DB_TYPE", "sqlite")
        .WithEnvironment("ARKD_EVENT_DB_TYPE", "badger")
        .WithEnvironment("ARKD_LIVE_STORE_TYPE", "inmemory")
        .WithEnvironment("ARKD_UNLOCKER_TYPE", "env")
        .WithEnvironment("ARKD_UNLOCKER_PASSWORD", "secret")
        .WithEnvironment("ARKD_INTENT_OFFCHAIN_INPUT_FEE_PROGRAM", "200.0")
        .WithEnvironment("ARKD_INTENT_ONCHAIN_INPUT_FEE_PROGRAM", "0.01 * amount")
        .WithEnvironment("ARKD_INTENT_OFFCHAIN_OUTPUT_FEE_PROGRAM", "0.0")
        .WithEnvironment("ARKD_INTENT_ONCHAIN_OUTPUT_FEE_PROGRAM", "200.0")
        .WithOtlpExporter(OtlpProtocol.HttpProtobuf)
        .WithEnvironment(callback =>
        {
            var otlpEndpoint = callback.EnvironmentVariables.GetValueOrDefault("OTEL_EXPORTER_OTLP_ENDPOINT", "");
            callback.EnvironmentVariables["ARKD_OTEL_COLLECTOR_ENDPOINT"] = otlpEndpoint;
        })
        .WithCommand("create-note", "Create Note", async ctx =>
            {
                var noteOutput = await Cli.Wrap("docker")
                    .WithArguments(["exec", "-t", "ark", "arkd", "note", "--amount", "1000000"])
                    .ExecuteBufferedAsync(ctx.CancellationToken);
                var note = noteOutput.StandardOutput.Trim();
                return new ExecuteCommandResult() { Success = true, ErrorMessage = note };
            }
        )
        .WithVolume("nark-ark", "/app/data")
        .OnResourceReady(StartArkResource)
        .WithHttpEndpoint(7070, 7070, name: "arkd");
async Task StartArkResource(ContainerResource cr, ResourceReadyEvent @event, CancellationToken cancellationToken)
{
    await Task.Delay(TimeSpan.FromSeconds(5));

    var logger = @event.Services.GetRequiredService<ILogger<NArk_AppHost>>();

    var walletCreationProcess =
        await Cli.Wrap("docker")
            .WithArguments(["exec", "-t", "ark", "arkd", "wallet", "create", "--password", "secret"])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);

    if (!walletCreationProcess.IsSuccess &&
        !walletCreationProcess.StandardOutput.Contains("wallet already initialized") &&
        !walletCreationProcess.StandardError.Contains("wallet already initialized"))
    {
        logger.LogCritical(
            "Wallet creation failed, output = {stdOut}, error = {stdErr}",
            walletCreationProcess.StandardOutput,
            walletCreationProcess.StandardError
        );
    }

    var walletUnlockProcess =
        await Cli.Wrap("docker")
            .WithArguments(["exec", "-t", "ark", "arkd", "wallet", "unlock", "--password", "secret"])
            .ExecuteBufferedAsync(cancellationToken);

    if (!walletUnlockProcess.IsSuccess)
    {
        logger.LogCritical(
            "Wallet unlock failed, output = {stdOut}, error = {stdErr}",
            walletUnlockProcess.StandardOutput,
            walletUnlockProcess.StandardError
        );
    }

    int returnCode;
    do
    {
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        var walletStatus = await Cli.Wrap("docker")
            .WithArguments(["exec", "-t", "ark", "arkd", "wallet", "status"])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);
        returnCode = walletStatus.ExitCode;
    } while (returnCode != 0);

    var arkInit =
        await Cli.Wrap("docker")
            .WithArguments([
                "exec", "-t", "ark", "ark", "init", "--password", "secret", "--server-url", "localhost:7070",
                "--explorer", "http://chopsticks:3000"
            ])
            .ExecuteBufferedAsync(cancellationToken);

    if (!arkInit.IsSuccess)
    {
        logger.LogCritical(
            "Ark init failed, output = {stdOut}, error = {stdErr}",
            arkInit.StandardOutput,
            arkInit.StandardError
        );
    }

    var walletAddress =
        await Cli.Wrap("docker")
            .WithArguments(["exec", "-t", "ark", "arkd", "wallet", "address"])
            .ExecuteBufferedAsync(cancellationToken);

    var address = walletAddress.StandardOutput.Trim();
    var chopsticksEndpoint = await chopsticks.GetEndpoint("http", null!).GetValueAsync(cancellationToken);
    await new HttpClient().PostAsJsonAsync($"{chopsticksEndpoint}/faucet", new
    {
        amount = 10,
        address = address
    }, cancellationToken: cancellationToken);

    var noteOutput = await Cli.Wrap("docker")
        .WithArguments(["exec", "-t", "ark", "arkd", "note", "--amount", "3000000"])
        .ExecuteBufferedAsync(cancellationToken);
    var note = noteOutput.StandardOutput.Trim();
    await Cli.Wrap("docker")
        .WithArguments(["exec", "-t", "ark", "ark", "redeem-notes", "-n", note, "--password", "secret"])
        .ExecuteBufferedAsync(cancellationToken);
}

if (!args.Contains("--noswap"))
{

    var boltzLnd =
        builder
            .AddContainer("boltz-lnd", "btcpayserver/lnd", "v0.19.3-beta")
            .WithContainerName("boltz-lnd")
            .WithContainerNetworkAlias("boltz-lnd")
            .WithEnvironment("LND_CHAIN", "btc")
            .WithEnvironment("LND_ENVIRONMENT", "regtest")
            .WithEnvironment("LND_EXPLORERURL", "http://nbxplorer:32838/")
            .WithEnvironment("LND_REST_LISTEN_HOST", "http://boltz-lnd:8080")
            .WithEnvironment("LND_EXTRA_ARGS", @"bitcoin.node=bitcoind
maxpendingchannels=10
rpclisten=0.0.0.0:10009
restlisten=boltz-lnd:8080
bitcoind.rpchost=bitcoin:18443
bitcoind.rpcuser=admin1
bitcoind.rpcpass=123
bitcoind.zmqpubrawblock=tcp://bitcoin:28332
bitcoind.zmqpubrawtx=tcp://bitcoin:28333
db.bolt.auto-compact=true
db.prune-revocation=true
alias=Ark Labs
externalip=boltz-lnd:9735
protocol.option-scid-alias=true
protocol.wumbo-channels=true
accept-keysend=true
minchansize=25000
noseedbackup=false
gc-canceled-invoices-on-startup=true
coin-selection-strategy=random
protocol.custom-message=513
protocol.custom-nodeann=39
protocol.custom-init=39
no-rest-tls=1
tlsextradomain=boltz-lnd
debuglevel=debug
restcors=*")
            .WithVolume("nark-boltz-lnd", "/root/.lnd")
            .WithEndpoint(9736, 9735, protocol: ProtocolType.Tcp, name: "p2p")
            .WithEndpoint(10010, 10009, protocol: ProtocolType.Tcp, name: "rpc")
            .OnResourceReady(FundBoltzLnd)
            .WaitFor(bitcoin)
            .WaitFor(nbxplorer);

    async Task FundBoltzLnd(ContainerResource containerResource, ResourceReadyEvent @event,
        CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
        var addressResponse = await Cli.Wrap("docker")
            .WithArguments(["exec", "boltz-lnd", "lncli", "--network=regtest", "newaddress", "p2wkh"])
            .ExecuteBufferedAsync(cancellationToken);

        var address =
            (JsonSerializer.Deserialize<JsonObject>(addressResponse.StandardOutput)?["address"]?.GetValue<string>()
             ?? throw new InvalidOperationException("Boltz-LND bootup failed...")).Trim();
        var chopsticksEndpoint = await chopsticks.GetEndpoint("http", null!).GetValueAsync(cancellationToken);
        await new HttpClient().PostAsJsonAsync($"{chopsticksEndpoint}/faucet", new
        {
            amount = 4,
            address = address
        }, cancellationToken: cancellationToken);

        var walletBalanceResponse =
            await Cli.Wrap("docker")
                .WithArguments(["exec", "boltz-lnd", "lncli", "--network=regtest", "walletbalance"])
                .ExecuteBufferedAsync(cancellationToken);
        var balance =
            decimal.Parse(
                (JsonSerializer.Deserialize<JsonObject>(walletBalanceResponse.StandardOutput)?["account_balance"]?
                     ["default"]?["confirmed_balance"]?.GetValue<string>()
                 ?? throw new InvalidOperationException("Boltz-LND bootup failed...")).Trim());

        if (balance < 1_000_000)
        {
            throw new InvalidOperationException(
                $"ERROR: LND wallet balance ({balance}) is less than 1,000,000 sats. Funding failed.");
        }
    }

    var lnd =
        builder
            .AddContainer("lnd", "btcpayserver/lnd", "v0.19.3-beta")
            .WithContainerName("lnd")
            .WithContainerNetworkAlias("lnd")
            .WithEnvironment("LND_CHAIN", "btc")
            .WithEnvironment("LND_ENVIRONMENT", "regtest")
            .WithEnvironment("LND_EXPLORERURL", "http://nbxplorer:32838/")
            .WithEnvironment("LND_REST_LISTEN_HOST", "http://lnd:8080")
            .WithEnvironment("LND_EXTRA_ARGS", @"bitcoin.node=bitcoind
maxpendingchannels=10
rpclisten=0.0.0.0:10009
restlisten=lnd:8080
bitcoind.rpchost=bitcoin:18443
bitcoind.rpcuser=admin1
bitcoind.rpcpass=123
bitcoind.zmqpubrawblock=tcp://bitcoin:28332
bitcoind.zmqpubrawtx=tcp://bitcoin:28333
db.bolt.auto-compact=true
db.prune-revocation=true
alias=Ark Labs User
externalip=lnd:9735
protocol.option-scid-alias=true
protocol.wumbo-channels=true
accept-keysend=true
minchansize=25000
noseedbackup=false
gc-canceled-invoices-on-startup=true
coin-selection-strategy=random
protocol.custom-message=513
protocol.custom-nodeann=39
protocol.custom-init=39
no-rest-tls=1
debuglevel=debug
restcors=*
tlsextradomain=lnd")
            .WithVolume("nark-lnd", "/root/.lnd")
            .WithEndpoint(9735, 9735, protocol: ProtocolType.Tcp, name: "p2p")
            .WithEndpoint(10009, 10009, protocol: ProtocolType.Tcp, name: "rpc")
            .WithCommand("create-invoice", "Create an invoice", async context =>
            {
                var createInvoiceResponse = await Cli.Wrap("docker")
                    .WithArguments([
                        "exec", "lnd", "lncli", "--network=regtest", "addinvoice", "--amt", "10000", "--expiry",
                        TimeSpan.FromSeconds(30).TotalSeconds.ToString(CultureInfo.InvariantCulture)
                    ])
                    .ExecuteBufferedAsync(context.CancellationToken);
                var invoice =
                (
                    JsonSerializer.Deserialize<JsonObject>(createInvoiceResponse.StandardOutput)?["payment_request"]
                        ?.GetValue<string>()
                    ?? throw new InvalidOperationException("Invoice creation on LND failed")
                ).Trim();

                return new ExecuteCommandResult() { Success = true, ErrorMessage = invoice };
            })
            .WithCommand("create-long-invoice", "Create invoice with long expiry", async context =>
            {
                var createInvoiceResponse = await Cli.Wrap("docker")
                    .WithArguments([
                        "exec", "lnd", "lncli", "--network=regtest", "addinvoice", "--amt", "10000"
                    ])
                    .ExecuteBufferedAsync(context.CancellationToken);
                var invoice =
                (
                    JsonSerializer.Deserialize<JsonObject>(createInvoiceResponse.StandardOutput)?["payment_request"]
                        ?.GetValue<string>()
                    ?? throw new InvalidOperationException("Invoice creation on LND failed")
                ).Trim();

                return new ExecuteCommandResult() { Success = true, ErrorMessage = invoice };
            })
            .WithCommand("shutdown", "Shutdown", async context =>
            {
                await Cli.Wrap("docker").WithArguments($"stop {context.ResourceName}").ExecuteBufferedAsync();
                return new ExecuteCommandResult() { Success = true };
            })
            .OnResourceReady(CreateLnd2LndChannel)
            .WaitFor(bitcoin)
            .WaitFor(nbxplorer)
            .WaitFor(boltzLnd);

    async Task CreateLnd2LndChannel(ContainerResource resource, ResourceReadyEvent @event,
        CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
        var pubKeyResponse = await Cli.Wrap("docker")
            .WithArguments(["exec", "lnd", "lncli", "--network=regtest", "getinfo"])
            .ExecuteBufferedAsync(cancellationToken);
        var counterpartyPubKey =
            JsonSerializer.Deserialize<JsonObject>(pubKeyResponse.StandardOutput)?["identity_pubkey"]?
                .GetValue<string>() ??
            throw new InvalidOperationException("ERROR: LND did not provide pubkey for channel creation");
        await Cli.Wrap("docker")
            .WithArguments([
                "exec", "boltz-lnd", "lncli", "--network=regtest", "openchannel", $"--node_key={counterpartyPubKey}",
                "--connect=lnd:9735", "--local_amt=2500000", "--sat_per_vbyte=1", "--min_confs=0", "--push_amt=500000"
            ])
            .ExecuteBufferedAsync(cancellationToken);

        await Cli.Wrap("docker")
            .WithArguments(["exec", "bitcoin", "bitcoin-cli", "-rpcwallet=", "-generate", "10"])
            .ExecuteBufferedAsync(cancellationToken);
    }


    var boltzFulmine =
        builder
            .AddContainer("boltz-fulmine", "ghcr.io/arklabshq/fulmine", "v0.3.10")
            .WithContainerName("boltz-fulmine")
            .WithContainerNetworkAlias("boltz-fulmine")
            .WithEnvironment("FULMINE_ARK_SERVER", "http://ark:7070")
            .WithEnvironment("FULMINE_ESPLORA_URL", "http://chopsticks:3000")
            .WithEnvironment("FULMINE_NO_MACAROONS", "true")
            .WithEnvironment("FULMINE_BOLTZ_URL", "http://boltz:9001")
            .WithEnvironment("FULMINE_BOLTZ_WS_URL", "ws://boltz:9001")
            .WithEnvironment("FULMINE_DISABLE_TELEMETRY", "true")
            .WithEnvironment("FULMINE_UNLOCKER_TYPE", "env")
            .WithEnvironment("FULMINE_UNLOCKER_PASSWORD", "password")
            .WithEnvironment("FULMINE_LND_URL", "http://boltz-lnd:10009")
            .WithEnvironment("FULMINE_LND_DATADIR", "/root/.lnd")
            .WithEndpoint(7002, 7000, protocol: ProtocolType.Tcp, name: "grpc")
            .WithHttpEndpoint(7003, 7001, name: "api")
            .WithHttpHealthCheck("/api/v1/wallet/status", null, "api")
            .OnResourceReady(UnlockAndFundFulmine)
            .WithVolume("nark-boltz-fulmine", "/app/data")
            .WithVolume("nark-boltz-lnd", "/root/.lnd")
            .WaitFor(boltzLnd);

    async Task UnlockAndFundFulmine(ContainerResource resource, ResourceReadyEvent @event,
        CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);

        var fulmineEndpoint =
            await resource.GetEndpoint("api", null!).GetValueAsync(cancellationToken);
        var fulmineHttpClient = new HttpClient() { BaseAddress = new Uri(fulmineEndpoint!) };

        var genSeedResponse =
            await fulmineHttpClient.GetFromJsonAsync<JsonObject>($"/api/v1/wallet/genseed",
                cancellationToken: cancellationToken);
        var privateKey = genSeedResponse?["nsec"]?.GetValue<string>() ??
                         throw new InvalidOperationException("Fulmine's GenSeed didn't work properly.");

        await fulmineHttpClient.PostAsJsonAsync("/api/v1/wallet/create", new
        {
            private_key = privateKey,
            password = "password",
            server_url = "http://ark:7070"
        }, cancellationToken: cancellationToken);

        await Task.Delay(TimeSpan.FromSeconds(1));


        await fulmineHttpClient.PostAsJsonAsync("/api/v1/wallet/unlock", new
        {
            password = "password"
        }, cancellationToken: cancellationToken);

        await Task.Delay(TimeSpan.FromSeconds(1));

        var fulmineAddressResponse =
            await fulmineHttpClient.GetFromJsonAsync<JsonObject>("/api/v1/address", cancellationToken);

        var address =
            (fulmineAddressResponse?["address"] ??
             throw new InvalidOperationException("Reading fulmine address failed."))
            .GetValue<string>()
            .Trim();
        var onchainAddress = new Uri(address).AbsolutePath;

        var chopsticksEndpoint = await chopsticks.GetEndpoint("http", null!).GetValueAsync(cancellationToken);
        await new HttpClient().PostAsJsonAsync($"{chopsticksEndpoint}/faucet", new
        {
            amount = 1,
            address = onchainAddress
        }, cancellationToken: cancellationToken);

        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);

        await fulmineHttpClient.GetAsync("/api/v1/settle", cancellationToken);
        await fulmineHttpClient.GetAsync("/api/v1/transactions", cancellationToken);
    }

    var boltz =
        builder
            .AddContainer("boltz", "boltz/boltz", "latest")
            .WithContainerName("boltz")
            .WithContainerNetworkAlias("boltz")
            .WithEndpoint(9000, 9000, protocol: ProtocolType.Tcp, name: "grpc")
            .WithHttpEndpoint(9001, 9001, name: "api")
            .WithHttpEndpoint(9004, 9004, name: "ws")
            .WithEnvironment("BOLTZ_CONFIG", @"loglevel = ""debug""
network = ""regtest""
[ark]
host = ""boltz-fulmine""
port = 7000
useLocktimeSeconds = true
[ark.unilateralDelays]
claim = 444 # 3d.2h in blocks
refund = 444 # 3d.2h in blocks
refundWithoutReceiver = 720 # 5d in blocks

[api]
host = ""0.0.0.0""
port = 9001
cors = ""*""

[grpc]
host = ""0.0.0.0""
port = 9000

[sidecar]
[sidecar.grpc]
host = ""0.0.0.0""
port = 9003

[sidecar.ws]
host = ""0.0.0.0""
port = 9004

[sidecar.api]
host = ""0.0.0.0""
port = 9005

[postgres]
host = ""postgres""
port = 5432
database = ""boltz""
username = ""postgres""
password = ""postgres""

[swap]
deferredClaimSymbols = [""BTC""]

[[pairs]]
base = ""ARK""
quote = ""BTC""
rate = 1
fee = 0
swapInFee = 0.00
maxSwapAmount = 4294967
minSwapAmount = 1000

[pairs.timeoutDelta]
reverse = 1440
chain = 1440
swapMinimal = 1440
swapMaximal = 2880
swapTaproot = 10080

[[currencies]]
symbol = ""BTC""
network = ""bitcoinRegtest""
minWalletBalance = 10000000
minLocalBalance = 10000000
minRemoteBalance = 10000000
maxSwapAmount = 4294967
minSwapAmount = 50000
maxZeroConfAmount = 100000
preferredWallet = ""core""

[currencies.chain]
host = ""bitcoin""
port = 18443
user = ""admin1""
password = ""123""
zmqpubrawtx = ""tcp://bitcoin:28333""
zmqpubrawblock = ""tcp://bitcoin:28332""

[currencies.lnd]
host = ""boltz-lnd""
port = 10009
certpath = ""/home/boltz/.lnd/tls.cert""
macaroonpath = ""/home/boltz/.lnd/data/chain/bitcoin/regtest/admin.macaroon""")
            .WithVolume("nark-boltz", "/home/boltz/.boltz")
            .WithVolume("nark-boltz-lnd", "/home/boltz/.lnd")
            .WithEntrypoint("sh")
            .WithArgs("-c",
                "echo \"$BOLTZ_CONFIG\" > /home/boltz/.boltz/boltz.config && boltzd --datadir /home/boltz/.boltz --configpath /home/boltz/.boltz/boltz.config")
            .WaitFor(boltzDb)
            .WaitFor(bitcoin)
            .WaitFor(boltzLnd)
            .WaitFor(boltzFulmine)
            .WaitFor(lnd);
}
builder
    .Build()
    .Run();